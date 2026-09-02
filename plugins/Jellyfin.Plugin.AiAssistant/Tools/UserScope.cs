using System;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// The identity every tool call executes as.
/// </summary>
/// <remarks>
/// This type is the plugin's authorization boundary. Tools receive a scope and
/// nothing else; they have no way to obtain an administrative context, and no
/// tool may accept a user id as an argument. That keeps the model from being able
/// to request data on behalf of somebody else even if it is successfully
/// manipulated into trying — the authorization decision is made by Jellyfin
/// against this user, never by the model.
/// </remarks>
public sealed class UserScope
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UserScope"/> class.
    /// </summary>
    /// <param name="user">The authenticated user the assistant acts for.</param>
    public UserScope(User user)
    {
        User = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <summary>Gets the user this conversation belongs to.</summary>
    public User User { get; }

    /// <summary>Gets the user's identifier.</summary>
    public Guid UserId => User.Id;
}
