using System;

namespace Jellyfin.Plugin.EnhancedPdfReader.Progress;

/// <summary>
/// The reading position of one user in one document.
/// </summary>
public class ReadingPosition
{
    /// <summary>
    /// Gets or sets the 1-based page the user was last on. Zero means "no saved position".
    /// </summary>
    public int Page { get; set; }

    /// <summary>
    /// Gets or sets the total number of pages of the document, as seen by the client.
    /// </summary>
    public int NumPages { get; set; }

    /// <summary>
    /// Gets or sets the UTC time the position was last written.
    /// </summary>
    public DateTime UpdatedUtc { get; set; }
}
