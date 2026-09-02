using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AiAssistant.Configuration;

/// <summary>
/// Administrator-controlled plugin settings.
/// </summary>
/// <remarks>
/// This class is serialized to XML in the plugin configuration directory, which
/// administrators can read, edit and back up. No secret is ever stored here — see
/// <see cref="Security.EncryptedCredentialStore"/>.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether users may configure their own provider.
    /// </summary>
    public bool AllowUserProviders { get; set; } = true;

    /// <summary>
    /// Gets or sets the comma-separated provider ids users are permitted to choose.
    /// An empty value permits every built-in provider.
    /// </summary>
    public string AllowedProviders { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider used by users who have not configured their own.
    /// </summary>
    public string DefaultProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the endpoint for the default provider, when it needs one.
    /// </summary>
    public string DefaultBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model used with the default provider.
    /// </summary>
    public string DefaultModel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the server-supplied credential may be
    /// used by users who have not supplied their own.
    /// </summary>
    /// <remarks>
    /// The credential itself lives in the encrypted vault, never in this file. Enabling
    /// this means every user's requests are billed to the server owner's account.
    /// </remarks>
    public bool ShareServerCredential { get; set; }

    /// <summary>
    /// Gets or sets the maximum assistant requests one user may make per hour.
    /// Zero disables the limit.
    /// </summary>
    /// <remarks>Mitigates OWASP LLM10, unbounded consumption of a paid API.</remarks>
    public int MaxRequestsPerUserPerHour { get; set; } = 60;

    /// <summary>
    /// Gets or sets the maximum tool calls the assistant may chain in one exchange.
    /// </summary>
    /// <remarks>
    /// This is a ceiling on runaway loops, not a budget to be spent carefully. Set too
    /// low it truncates ordinary work: "three random episodes each of three shows,
    /// added to a playlist" is a lookup per show plus the write, and at eight there was
    /// no room left for a single wrong turn.
    /// </remarks>
    public int MaxToolCallsPerExchange { get; set; } = 16;

    /// <summary>
    /// Gets or sets a value indicating whether tools that change server state
    /// (creating collections and playlists) are available at all.
    /// </summary>
    public bool EnableMutatingTools { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the client script is injected into the web client.
    /// </summary>
    public bool InjectClientScript { get; set; } = true;

    /// <summary>
    /// Gets or sets the language the library's metadata is written in, as a plain
    /// name such as "Spanish". Empty means unknown.
    /// </summary>
    /// <remarks>
    /// Applies to every user unless they override it. A model knows titles in the
    /// language it was trained on, which is frequently not the language the library
    /// is catalogued in; telling it which to search in avoids a whole class of
    /// "you do not have it" answers about things the user does have.
    /// </remarks>
    public string MetadataLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name the assistant uses for this server when talking to users.
    /// </summary>
    public string ServerLabel { get; set; } = "this media server";
}
