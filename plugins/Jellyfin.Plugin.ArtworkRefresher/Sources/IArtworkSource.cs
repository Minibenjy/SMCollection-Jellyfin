using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;

namespace Jellyfin.Plugin.ArtworkRefresher.Sources;

/// <summary>
/// A place artwork can come from. A source only searches and returns candidates: it never
/// downloads and never chooses the slot an image goes in.
/// </summary>
public interface IArtworkSource
{
    /// <summary>Gets the stable source id (see <see cref="SourceIds"/>).</summary>
    string Id { get; }

    /// <summary>
    /// Looks for candidates.
    /// </summary>
    /// <param name="query">What to look for.</param>
    /// <param name="http">The guarded HTTP client.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The candidates, best first. Empty when there is nothing or the source does not apply.</returns>
    Task<IReadOnlyList<ArtworkCandidate>> FindAsync(
        ArtworkQuery query,
        IArtworkHttp http,
        PluginConfiguration configuration,
        CancellationToken cancellationToken);
}

/// <summary>
/// The guarded network access sources use.
/// </summary>
public interface IArtworkHttp
{
    /// <summary>
    /// Gets a JSON document from a provider.
    /// </summary>
    /// <param name="sourceId">The provider id (for the host allow-list and the rate limit).</param>
    /// <param name="uri">The address.</param>
    /// <param name="requestsPerMinute">The request budget.</param>
    /// <param name="headers">Extra headers, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The document, or null on any failure. The caller must dispose it.</returns>
    Task<JsonDocument?> GetJsonAsync(
        string sourceId,
        System.Uri uri,
        int requestsPerMinute,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken);
}
