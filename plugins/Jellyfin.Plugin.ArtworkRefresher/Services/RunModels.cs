using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ArtworkRefresher.Services;

/// <summary>A log line of a run.</summary>
public sealed class RunLogEntry
{
    /// <summary>Gets or sets the sequence number.</summary>
    public long Sequence { get; set; }

    /// <summary>Gets or sets the time.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Gets or sets the level: info, ok, skip, error.</summary>
    public string Level { get; set; } = "info";

    /// <summary>Gets or sets the message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>The counters of a run.</summary>
public sealed class RunSummary
{
    /// <summary>Gets or sets the items looked at.</summary>
    public int Scanned { get; set; }

    /// <summary>Gets or sets the items with at least one slot to fill.</summary>
    public int Eligible { get; set; }

    /// <summary>Gets or sets the slots skipped because of a lock or exclusion.</summary>
    public int SkippedLocked { get; set; }

    /// <summary>Gets or sets the slots skipped by the book or Auto Thumbnails protection.</summary>
    public int SkippedProtected { get; set; }

    /// <summary>Gets or sets the slots skipped because they were tried recently or unchanged.</summary>
    public int SkippedFresh { get; set; }

    /// <summary>Gets or sets the slots updated (or that a dry run would update).</summary>
    public int Updated { get; set; }

    /// <summary>Gets or sets the slots with no candidate.</summary>
    public int NoCandidate { get; set; }

    /// <summary>Gets or sets the slots that hit a rate limit.</summary>
    public int RateLimited { get; set; }

    /// <summary>Gets or sets the slots that failed.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets the pools of candidate images built or refreshed.</summary>
    public int PoolsBuilt { get; set; }

    /// <summary>Gets or sets the images that really changed by daily rotation.</summary>
    public int RotationChanges { get; set; }

    /// <summary>Gets or sets the rotations a dry run would have made.</summary>
    public int RotationDryRun { get; set; }

    /// <summary>Gets or sets the items that had at least one metadata field filled (or would have, in a dry run).</summary>
    public int MetadataItems { get; set; }

    /// <summary>Gets or sets the metadata fields filled (or that a dry run would fill), by field.</summary>
    public Dictionary<string, int> MetadataFilled { get; set; } = [];

    /// <summary>Gets or sets the items with gaps that TMDb could not fill.</summary>
    public int MetadataNoData { get; set; }
}

/// <summary>The state of the current or last run.</summary>
public sealed class RunStatus
{
    /// <summary>Gets or sets the state: idle, running, cancelling, completed, cancelled, failed.</summary>
    public string State { get; set; } = "idle";

    /// <summary>Gets or sets what the run is doing.</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>Gets or sets the items processed.</summary>
    public int Processed { get; set; }

    /// <summary>Gets or sets the items to process.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the percentage.</summary>
    public double Percent { get; set; }

    /// <summary>Gets or sets a value indicating whether the run is a dry run.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets when it started.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Gets or sets when it finished.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Gets or sets the summary.</summary>
    public RunSummary Summary { get; set; } = new();

    /// <summary>Gets or sets the counters per source.</summary>
    public Dictionary<string, SourceCountersDto> Sources { get; set; } = [];

    /// <summary>Gets or sets the highest log sequence.</summary>
    public long LatestSequence { get; set; }

    /// <summary>Gets or sets the log lines newer than the caller's sequence.</summary>
    public List<RunLogEntry> Log { get; set; } = [];
}

/// <summary>Source counters as sent to the page.</summary>
public sealed class SourceCountersDto
{
    /// <summary>Gets or sets the queries.</summary>
    public int Queries { get; set; }

    /// <summary>Gets or sets the downloads.</summary>
    public int Downloads { get; set; }

    /// <summary>Gets or sets the 429 answers.</summary>
    public int TooManyRequests { get; set; }

    /// <summary>Gets or sets the failures.</summary>
    public int Failures { get; set; }
}

/// <summary>What a run should do.</summary>
public sealed class RunRequest
{
    /// <summary>Gets or sets a value indicating whether the run was started by the scheduler.</summary>
    public bool Scheduled { get; set; }

    /// <summary>Gets or sets a value indicating whether to only log. Null uses the configuration.</summary>
    public bool? DryRun { get; set; }

    /// <summary>Gets or sets the mode. Null uses the configuration.</summary>
    public ArtworkRefreshMode? Mode { get; set; }

    /// <summary>Gets or sets the library ids to limit the run to. Empty means the configured ones.</summary>
    public Guid[] LibraryIds { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether empty metadata fields are filled. Null uses the configuration.</summary>
    public bool? FillMetadata { get; set; }
}

/// <summary>A request to refresh one item.</summary>
public sealed class ItemRefreshRequest
{
    /// <summary>Gets or sets the image types. Empty means the enabled ones.</summary>
    public ImageType[] ImageTypes { get; set; } = [];

    /// <summary>Gets or sets the mode. Null means Replace for a manual refresh.</summary>
    public ArtworkRefreshMode? Mode { get; set; }

    /// <summary>Gets or sets a value indicating whether to skip missing-only, item locks and book protection (administrators only).</summary>
    public bool Force { get; set; }

    /// <summary>Gets or sets a value indicating whether to only log.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets a value indicating whether an excluded library may be entered (needs Force).</summary>
    public bool OverrideExcludedLibraries { get; set; }
}

/// <summary>The outcome of refreshing one item.</summary>
public sealed class ItemRefreshResult
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets what happened to each slot.</summary>
    public List<SlotOutcome> Slots { get; set; } = [];

    /// <summary>Gets a value indicating whether anything was (or would be) updated.</summary>
    public bool Updated => Slots.Exists(s => s.Status == "updated" || s.Status == "dryrun");
}

/// <summary>What happened to one image slot.</summary>
public sealed class SlotOutcome
{
    /// <summary>Gets or sets the slot key, for example Primary:0.</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary>Gets or sets the status: updated, dryrun, locked, protected, fresh, nocandidate, ratelimited, failed.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the source used, when any.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets a short reason.</summary>
    public string? Detail { get; set; }
}
