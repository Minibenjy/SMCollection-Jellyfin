using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscoverHome.Artwork;

/// <summary>
/// Keeps a local copy of a studio logo for every studio that appears in this library.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin has no studio artwork of its own — <c>/Studios/{name}/Images/Primary</c>
/// returns 404 for every studio on a stock install — so the logos have to come from
/// somewhere public. The default source is the same community repository Jellyfin's
/// own Studio Images plugin points at, which publishes a flat <c>thumbs.txt</c> index
/// of ~3200 studio names plus one <c>thumb.jpg</c> per studio.
/// </para>
/// <para>
/// Only studios this library actually contains are downloaded, so the cache stays
/// proportional to the collection rather than to the size of the upstream index.
/// </para>
/// </remarks>
public class StudioArtworkCache
{
    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StudioArtworkCache> _logger;

    // The daily task and the config page's "Sync artwork now" button both land in
    // SyncAsync; running them at once would race on the same `.part` files.
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private readonly object _availableLock = new();
    private IReadOnlyList<string>? _available;
    private DateTime _availableAtUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="StudioArtworkCache"/> class.
    /// </summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public StudioArtworkCache(
        IApplicationPaths paths,
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ILogger<StudioArtworkCache> logger)
    {
        _paths = paths;
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Gets the directory holding the cached logos.</summary>
    public string CacheDirectory => Path.Combine(_paths.CachePath, "discoverhome", "studios");

    /// <summary>
    /// Resolves the on-disk path a studio's logo would occupy.
    /// </summary>
    /// <param name="studioName">The studio name.</param>
    /// <returns>An absolute path inside <see cref="CacheDirectory"/>.</returns>
    /// <remarks>
    /// Callers pass a studio *name*, never a file name: the path is always derived
    /// here, so a request can't walk out of the cache directory no matter what the
    /// client sends. The short hash suffix keeps two studios whose names sanitise to
    /// the same string from overwriting each other.
    /// </remarks>
    public string GetCachePath(string studioName)
    {
        var safe = Sanitize(studioName);
        var hash = ShortHash(studioName);
        return Path.Combine(
            CacheDirectory,
            string.Format(CultureInfo.InvariantCulture, "{0}-{1}.jpg", safe, hash));
    }

    /// <summary>
    /// Downloads any missing logos for the studios present in this library.
    /// </summary>
    /// <param name="progress">Progress reporter, 0–100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of logos held in the cache afterwards.</returns>
    public async Task<int> SyncAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var total = await SyncCoreAsync(progress, cancellationToken).ConfigureAwait(false);
            RecordSync(total);
            return total;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Lists the studios in this library that have a logo cached.
    /// </summary>
    /// <returns>Studio names, in library spelling.</returns>
    /// <remarks>
    /// Lets the client ask only for logos that exist, instead of firing one request
    /// per tile and eating a 404 for every studio the upstream index doesn't cover.
    /// Held in memory for a while because it walks every studio in the library; a
    /// sync invalidates it immediately.
    /// </remarks>
    public IReadOnlyList<string> GetAvailableStudios()
    {
        lock (_availableLock)
        {
            if (_available is not null && DateTime.UtcNow - _availableAtUtc < TimeSpan.FromMinutes(30))
            {
                return _available;
            }
        }

        var available = Directory.Exists(CacheDirectory)
            ? GetLibraryStudios().Where(name => File.Exists(GetCachePath(name))).ToList()
            : new List<string>();

        lock (_availableLock)
        {
            _available = available;
            _availableAtUtc = DateTime.UtcNow;
        }

        return available;
    }

    private void RecordSync(int total)
    {
        lock (_availableLock)
        {
            _available = null;
        }

        // Written here rather than by the scheduled task, so the manual sync
        // endpoint updates the count and timestamp the config page displays too.
        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            plugin.Configuration.CachedLogoCount = total;
            plugin.Configuration.LastArtworkSyncUtc =
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            plugin.UpdateConfiguration(plugin.Configuration);
        }
    }

    private async Task<int> SyncCoreAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Config;
        if (!config.EnableStudioLogos)
        {
            _logger.LogInformation("DiscoverHome: studio logos are disabled; nothing to sync.");
            return CountCached();
        }

        Directory.CreateDirectory(CacheDirectory);

        var repository = config.StudioArtworkRepository.TrimEnd('/');
        var client = _httpClientFactory.CreateClient(NamedClient.Default);

        var index = await FetchIndexAsync(client, repository, cancellationToken).ConfigureAwait(false);
        if (index.Count == 0)
        {
            _logger.LogWarning("DiscoverHome: the studio artwork index was empty; keeping the existing cache.");
            return CountCached();
        }

        var studios = GetLibraryStudios();
        _logger.LogInformation(
            "DiscoverHome: {Studios} studios in this library, {Index} in the upstream index.",
            studios.Count,
            index.Count);

        var done = 0;
        var downloaded = 0;

        foreach (var studio in studios)
        {
            cancellationToken.ThrowIfCancellationRequested();

            done++;
            progress.Report(done * 100d / studios.Count);

            var target = GetCachePath(studio);
            if (File.Exists(target))
            {
                continue;
            }

            // The index is the authority on what exists upstream; skipping the
            // lookup would mean a 404 round trip per unknown studio, and most of a
            // typical library's studios are not in it.
            if (!index.TryGetValue(studio, out var upstreamName))
            {
                continue;
            }

            if (await TryDownloadAsync(client, repository, upstreamName, target, cancellationToken).ConfigureAwait(false))
            {
                downloaded++;
            }
        }

        var total = CountCached();
        _logger.LogInformation(
            "DiscoverHome: studio logo sync finished; {Downloaded} new, {Total} cached.",
            downloaded,
            total);

        return total;
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        var safe = builder.ToString().Trim('_');
        return safe.Length == 0 ? "studio" : safe;
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(bytes, 0, 4).ToUpperInvariant();
    }

    private async Task<Dictionary<string, string>> FetchIndexAsync(
        HttpClient client,
        string repository,
        CancellationToken cancellationToken)
    {
        // Case-insensitive so "canal+" in the library matches "Canal+" upstream,
        // while the value keeps the upstream spelling needed to build the URL.
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var url = repository + "/thumbs.txt";
            var body = await client.GetStringAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

            foreach (var line in body.Split('\n'))
            {
                var name = line.Trim();
                if (name.Length > 0)
                {
                    index[name] = name;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            _logger.LogError(ex, "DiscoverHome: could not fetch the studio artwork index.");
        }

        return index;
    }

    private async Task<bool> TryDownloadAsync(
        HttpClient client,
        string repository,
        string upstreamName,
        string target,
        CancellationToken cancellationToken)
    {
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/images/{1}/thumb.jpg",
            repository,
            Uri.EscapeDataString(upstreamName));

        try
        {
            using var response = await client
                .GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            response.EnsureSuccessStatusCode();

            // Written to a temporary file first so a cancelled or failed download can
            // never leave a truncated image that later looks like a valid cache hit.
            var temp = target + ".part";
            await using (var file = File.Create(temp))
            {
                await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "DiscoverHome: could not download the logo for {Studio}.", upstreamName);
            return false;
        }
    }

    private List<string> GetLibraryStudios()
    {
        var result = _libraryManager.GetStudios(new InternalItemsQuery());
        return result.Items
            .Select(x => x.Item.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private int CountCached()
    {
        return Directory.Exists(CacheDirectory)
            ? Directory.GetFiles(CacheDirectory, "*.jpg").Length
            : 0;
    }
}
