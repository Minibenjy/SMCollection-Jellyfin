namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>
/// Stable source ids used in configuration and in the state file.
/// </summary>
public static class SourceIds
{
    /// <summary>TMDb.</summary>
    public const string Tmdb = "Tmdb";

    /// <summary>Fanart.tv.</summary>
    public const string Fanart = "Fanart";

    /// <summary>Wikimedia Commons.</summary>
    public const string Wikimedia = "Wikimedia";

    /// <summary>Open Library.</summary>
    public const string OpenLibrary = "OpenLibrary";

    /// <summary>Cover Art Archive.</summary>
    public const string CoverArtArchive = "CoverArtArchive";

    /// <summary>Google Programmable Search.</summary>
    public const string GoogleCse = "GoogleCse";

    /// <summary>The local mosaic.</summary>
    public const string LocalMosaic = "LocalMosaic";
}
