using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AiAssistant.Configuration;

/// <summary>
/// Persists per-user assistant settings.
/// </summary>
public interface IUserPreferenceStore
{
    /// <summary>Reads a user's settings.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored settings, or null when the user has none.</returns>
    Task<UserPreferences?> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Writes a user's settings.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="preferences">Settings to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetAsync(Guid userId, UserPreferences preferences, CancellationToken cancellationToken);
}
