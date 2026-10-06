using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtworkRefresher.Services;

/// <summary>A Home section a mosaic can be made for.</summary>
/// <param name="Key">A stable key, for example library-&lt;id&gt;.</param>
/// <param name="Name">A display name.</param>
/// <param name="LibraryId">The library the section shows, when it is one.</param>
public sealed record HomeArtworkSection(string Key, string Name, Guid? LibraryId);

/// <summary>
/// Supplies the Home sections and the items a user can see in them. Internal contract: a Home
/// plugin does not implement it, it reads the HTTP endpoints instead.
/// </summary>
public interface IHomeArtworkAdapter
{
    /// <summary>Lists the sections for a user.</summary>
    /// <param name="user">The user.</param>
    /// <returns>The sections.</returns>
    IReadOnlyList<HomeArtworkSection> GetSections(User user);

    /// <summary>Gets the items of a section that the user is allowed to see.</summary>
    /// <param name="section">The section.</param>
    /// <param name="user">The user.</param>
    /// <returns>The items.</returns>
    IReadOnlyList<BaseItem> GetVisibleItems(HomeArtworkSection section, User user);
}

/// <summary>
/// Works on a plain Jellyfin: one section per library the user can see.
/// </summary>
public sealed class VanillaHomeArtworkAdapter : IHomeArtworkAdapter
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="VanillaHomeArtworkAdapter"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    public VanillaHomeArtworkAdapter(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public IReadOnlyList<HomeArtworkSection> GetSections(User user)
        => _libraryManager.GetUserRootFolder()
            .GetChildren(user, true)
            .Select(f => new HomeArtworkSection("library-" + f.Id.ToString("N"), f.Name, f.Id))
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetVisibleItems(HomeArtworkSection section, User user)
    {
        if (section.LibraryId is not { } libraryId)
        {
            return [];
        }

        // The query runs as the user, so the server applies their library access and parental limits.
        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            AncestorIds = [libraryId],
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.MusicAlbum, BaseItemKind.Book],
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(true)
        });
    }
}

/// <summary>
/// Builds mosaics for Home sections from what a user may see, and caches them on disk. The cache
/// key holds the user, the section, the source items and the rendering settings, so one user's
/// image is never served to another.
/// </summary>
public sealed class HomeArtworkService
{
    private readonly IUserManager _userManager;
    private readonly ArtworkRefreshService _service;
    private readonly IHomeArtworkAdapter _adapter;
    private readonly ILogger<HomeArtworkService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeArtworkService"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="service">The refresh service (for the state store and data folder).</param>
    /// <param name="logger">The logger.</param>
    public HomeArtworkService(IUserManager userManager, ILibraryManager libraryManager, ArtworkRefreshService service, ILogger<HomeArtworkService> logger)
    {
        _userManager = userManager;
        _service = service;
        _adapter = new VanillaHomeArtworkAdapter(libraryManager);
        _logger = logger;
    }

    /// <summary>
    /// Lists the sections for a user, with the address of each image.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The sections, or null when the user does not exist.</returns>
    public IReadOnlyList<HomeArtworkSection>? GetSections(Guid userId)
        => _userManager.GetUserById(userId) is { } user ? _adapter.GetSections(user) : null;

    /// <summary>
    /// Gets the mosaic of a section for a user, building it when needed.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="sectionKey">The section key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The file path and its ETag, or null when there is nothing to show.</returns>
    public async Task<(string Path, string ETag)?> GetImageAsync(Guid userId, string sectionKey, CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        var section = _adapter.GetSections(user).FirstOrDefault(s => s.Key == sectionKey);
        if (section is null)
        {
            return null;
        }

        var configuration = Plugin.Config;
        var inputs = new List<MosaicInput>();
        var isAdmin = _userManager.GetUserDto(user, string.Empty).Policy?.IsAdministrator ?? false;
        foreach (var item in _adapter.GetVisibleItems(section, user))
        {
            // The server already limited the items to what this user may see. For everyone but an
            // administrator the shared-safety rules apply as well, which keeps what a restricted
            // profile (for instance a kids account managed by another plugin) must not see out.
            if (!isAdmin && !SharedArtworkSafetyPolicy.IsAllowed(
                    configuration,
                    new SharedArtworkSubject(item.GetAncestorIds().ToArray(), item.Tags.Concat(item.GetInheritedTags()).ToArray(), item.InheritedParentalRatingValue)))
            {
                continue;
            }

            var path = item.GetImageInfo(ImageType.Primary, 0)?.Path;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                inputs.Add(new MosaicInput(item.Id, path + "|" + File.GetLastWriteTimeUtc(path).Ticks, path));
            }
        }

        if (inputs.Count == 0)
        {
            return null;
        }

        var settings = string.Join(',', configuration.MosaicColumns, configuration.MosaicRows, configuration.MosaicWidth, configuration.MosaicHeight, configuration.JpegQuality);
        var sectionId = Guid.TryParse(sectionKey.Replace("library-", string.Empty, StringComparison.Ordinal), out var g) ? g : Guid.Empty;
        var signature = MosaicComposer.Signature(sectionId, inputs, userId.ToString("N") + "|" + settings);
        var stateKey = userId.ToString("N") + ":" + sectionKey;
        var folder = Path.Combine(_service.DataFolder, "home");
        var existing = _service.State.Use(d => d.Home.TryGetValue(stateKey, out var h) ? h : null);

        if (existing?.SourceItemsSignature == signature && existing.FileName is { } name && File.Exists(Path.Combine(folder, name)))
        {
            return (Path.Combine(folder, name), "\"" + existing.ContentHash + "\"");
        }

        var chosen = MosaicComposer.Choose(sectionId, 0, inputs, Math.Max(1, configuration.MosaicColumns) * Math.Max(1, configuration.MosaicRows));
        var jpeg = MosaicComposer.Render(chosen.Select(c => c.Path).ToList(), configuration.MosaicColumns, configuration.MosaicRows, configuration.MosaicWidth, configuration.MosaicHeight, configuration.JpegQuality);
        if (jpeg is null)
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant();
        Directory.CreateDirectory(folder);
        var fileName = hash + ".jpg";
        await File.WriteAllBytesAsync(Path.Combine(folder, fileName), jpeg, cancellationToken).ConfigureAwait(false);

        // One file per user and section: drop the previous one so the folder does not grow.
        if (existing?.FileName is { } old && old != fileName)
        {
            try
            {
                File.Delete(Path.Combine(folder, old));
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Artwork Refresher: could not delete an old Home image");
            }
        }

        _service.State.Update(d => d.Home[stateKey] = new HomeImageState
        {
            ContentHash = hash,
            SourceItemsSignature = signature,
            GeneratedUtc = DateTimeOffset.UtcNow,
            FileName = fileName
        });
        await _service.State.SaveAsync().ConfigureAwait(false);
        return (Path.Combine(folder, fileName), "\"" + hash + "\"");
    }
}
