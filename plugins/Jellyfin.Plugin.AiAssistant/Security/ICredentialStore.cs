using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AiAssistant.Security;

/// <summary>
/// Stores per-user provider credentials encrypted at rest.
/// </summary>
/// <remarks>
/// Credentials are encrypted, not hashed: the plugin must be able to recover the
/// original secret to authenticate against the provider, which a one-way hash
/// makes impossible. See SECURITY.md for the threat model this does and does not
/// cover.
/// </remarks>
public interface ICredentialStore
{
    /// <summary>
    /// Stores a credential for a user and provider, replacing any existing one.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="providerId">Provider identifier.</param>
    /// <param name="secret">The plaintext secret.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetAsync(Guid userId, string providerId, string secret, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves and decrypts a credential.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="providerId">Provider identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plaintext secret, or null when none is stored.</returns>
    Task<string?> GetAsync(Guid userId, string providerId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a non-reversible display hint for the UI, e.g. the last four characters.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="providerId">Provider identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A masked hint, or null when no credential is stored.</returns>
    Task<string?> GetHintAsync(Guid userId, string providerId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a stored credential.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="providerId">Provider identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(Guid userId, string providerId, CancellationToken cancellationToken);
}
