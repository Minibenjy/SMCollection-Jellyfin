using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AutoThumbnails.Services;

/// <summary>
/// What a run should cover.
/// </summary>
public class JobRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether books, comics and magazines are included.
    /// </summary>
    public bool Books { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether movies, episodes and videos are included.
    /// </summary>
    public bool Videos { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether series, seasons and box sets are included.
    /// </summary>
    public bool Folders { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether items that already have an image are
    /// regenerated. <c>false</c> only fills in what is missing.
    /// </summary>
    public bool Regenerate { get; set; }

    /// <summary>
    /// Gets or sets the libraries to limit the run to. Empty means every library.
    /// </summary>
    public IReadOnlyList<string> LibraryIds { get; set; } = Array.Empty<string>();
}

/// <summary>
/// One line of the running log shown in the dashboard.
/// </summary>
public class JobLogEntry
{
    /// <summary>
    /// Gets or sets the monotonic sequence number, used by the UI to fetch only new lines.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// Gets or sets the server time the line was written.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the severity: <c>info</c>, <c>ok</c>, <c>skip</c> or <c>error</c>.
    /// </summary>
    public string Level { get; set; } = "info";

    /// <summary>
    /// Gets or sets the message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// A snapshot of the current or last run.
/// </summary>
public class JobStatus
{
    /// <summary>
    /// Gets or sets the state: <c>idle</c>, <c>running</c>, <c>cancelling</c>,
    /// <c>completed</c>, <c>cancelled</c> or <c>failed</c>.
    /// </summary>
    public string State { get; set; } = "idle";

    /// <summary>
    /// Gets or sets what the run is doing right now.
    /// </summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of items handled so far.
    /// </summary>
    public int Processed { get; set; }

    /// <summary>
    /// Gets or sets the number of items in the run.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// Gets or sets the number of thumbnails created.
    /// </summary>
    public int Created { get; set; }

    /// <summary>
    /// Gets or sets the number of items that yielded nothing usable.
    /// </summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Gets or sets the number of items that failed outright.
    /// </summary>
    public int Failed { get; set; }

    /// <summary>
    /// Gets or sets the completion percentage.
    /// </summary>
    public double Percent { get; set; }

    /// <summary>
    /// Gets or sets when the run started.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// Gets or sets when the run ended.
    /// </summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>
    /// Gets or sets the log lines newer than the sequence the caller asked from.
    /// </summary>
    public IReadOnlyList<JobLogEntry> Log { get; set; } = Array.Empty<JobLogEntry>();

    /// <summary>
    /// Gets or sets the highest sequence number issued so far.
    /// </summary>
    public long LatestSequence { get; set; }
}

/// <summary>
/// A library the user can pick in the dashboard.
/// </summary>
public class LibraryInfo
{
    /// <summary>
    /// Gets or sets the library item id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection type, such as <c>books</c> or <c>movies</c>.
    /// </summary>
    public string CollectionType { get; set; } = string.Empty;
}
