using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ArtworkRefresher.TestHarness;

internal static class PolicyTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    public static Task RunAsync()
    {
        Window();
        Locks();
        Selector();
        Safety();
        Masking();
        State();
        return Task.CompletedTask;
    }

    private static DateTimeOffset At(int h, int m = 0, int day = 5) => new(2026, 10, day, h, m, 0, TimeSpan.Zero);

    private static void Window()
    {
        var s = new TimeOnly(2, 0);
        var e = new TimeOnly(6, 0);
        Check.True(RefreshWindowPolicy.IsInside(At(3), Utc, s, e), "window: inside");
        Check.True(!RefreshWindowPolicy.IsInside(At(1, 59), Utc, s, e), "window: just before");
        Check.True(!RefreshWindowPolicy.IsInside(At(6), Utc, s, e), "window: end is exclusive");
        Check.True(RefreshWindowPolicy.IsInside(At(2), Utc, s, e), "window: start is inclusive");

        // A window that crosses midnight.
        var ns = new TimeOnly(22, 0);
        var ne = new TimeOnly(4, 0);
        Check.True(RefreshWindowPolicy.IsInside(At(23), Utc, ns, ne), "midnight window: evening");
        Check.True(RefreshWindowPolicy.IsInside(At(1), Utc, ns, ne), "midnight window: after midnight");
        Check.True(!RefreshWindowPolicy.IsInside(At(12), Utc, ns, ne), "midnight window: noon outside");
        var close = RefreshWindowPolicy.WindowCloses(At(23), Utc, ns, ne);
        Check.Equal(At(4, 0, 6), close!.Value, "midnight window: closes the next day");
        Check.Equal(At(4, 0, 5), RefreshWindowPolicy.WindowCloses(At(1), Utc, ns, ne)!.Value, "midnight window: closes the same day after midnight");

        // Same start and end is the whole day.
        Check.True(RefreshWindowPolicy.IsInside(At(13), Utc, s, s), "equal start and end means all day");
        Check.True(RefreshWindowPolicy.WindowCloses(At(13), Utc, s, s) is null, "all day has no close");

        // Due logic: not before the persisted next-eligible time, not outside the window.
        Check.True(RefreshWindowPolicy.IsDue(At(3), null, true, Utc, s, e), "due: never run, inside window");
        Check.True(!RefreshWindowPolicy.IsDue(At(3), At(5), true, Utc, s, e), "not due: next eligible later");
        Check.True(!RefreshWindowPolicy.IsDue(At(8), At(1), true, Utc, s, e), "not due: eligible but outside window");
        Check.True(RefreshWindowPolicy.IsDue(At(8), At(1), false, Utc, s, e), "due: window off");

        // Deadline is the earlier of the window end and the longest run.
        Check.Equal(At(5, 0), RefreshWindowPolicy.Deadline(At(3), true, Utc, s, e, 120)!.Value, "deadline: max run earlier");
        Check.Equal(At(6, 0), RefreshWindowPolicy.Deadline(At(3), true, Utc, s, e, 600)!.Value, "deadline: window earlier");
        Check.True(RefreshWindowPolicy.Deadline(At(3), false, Utc, s, e, 0) is null, "deadline: none");

        // Daylight saving: Europe/Madrid springs forward on 2026-03-29 (02:00 -> 03:00), falls back on 2026-10-25.
        TimeZoneInfo? madrid = null;
        try
        {
            madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
        }
        catch (TimeZoneNotFoundException)
        {
        }

        if (madrid is not null)
        {
            // 2026-10-05 is CEST (UTC+2): 03:00 local is 01:00 UTC.
            Check.True(RefreshWindowPolicy.IsInside(At(1), madrid, s, e), "dst: summer time offset");
            // 2026-12-05 is CET (UTC+1): 03:00 local is 02:00 UTC; 01:00 UTC is 02:00 local, still inside the 02-06 window.
            var winter = new DateTimeOffset(2026, 12, 5, 0, 30, 0, TimeSpan.Zero); // 01:30 local, outside
            Check.True(!RefreshWindowPolicy.IsInside(winter, madrid, s, e), "dst: winter time offset");
            var closes = RefreshWindowPolicy.WindowCloses(new DateTimeOffset(2026, 12, 5, 2, 0, 0, TimeSpan.Zero), madrid, s, e)!.Value;
            Check.Equal(new DateTimeOffset(2026, 12, 5, 5, 0, 0, TimeSpan.Zero), closes, "dst: 06:00 CET is 05:00 UTC");
            var summerCloses = RefreshWindowPolicy.WindowCloses(new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero), madrid, s, e)!.Value;
            Check.Equal(new DateTimeOffset(2026, 10, 5, 4, 0, 0, TimeSpan.Zero), summerCloses, "dst: 06:00 CEST is 04:00 UTC");
        }

        Check.True(RefreshWindowPolicy.TryParseTime("02:30", out var t) && t == new TimeOnly(2, 30), "parse HH:mm");
        Check.True(!RefreshWindowPolicy.TryParseTime("25:00", out _), "parse rejects 25:00");
        Check.True(!RefreshWindowPolicy.TryParseTime(null, out _), "parse rejects null");
        Check.Equal(TimeZoneInfo.Local.Id, RefreshWindowPolicy.ResolveZone("not/a/zone").Id, "unknown zone falls back to local");
    }

    private static LockContext Ctx(BaseItemKind kind, ImageType type = ImageType.Primary, Action<Builder>? change = null)
    {
        var b = new Builder { Kind = kind, ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Type = type };
        change?.Invoke(b);
        return new LockContext
        {
            Kind = b.Kind,
            ItemId = b.ItemId,
            LibraryIds = b.Libraries,
            NativeLocked = b.Native,
            HasAutoThumbnailsMarker = b.Marker,
            Slot = new ImageSlot(b.Type, 0),
            SlotHasImage = b.HasImage,
            Force = b.Force,
            OverrideExcludedLibraries = b.OverrideExcluded,
            IsCategory = b.Category
        };
    }

    private sealed class Builder
    {
        public BaseItemKind Kind { get; set; }

        public Guid ItemId { get; set; }

        public ImageType Type { get; set; }

        public Guid[] Libraries { get; set; } = [];

        public bool Native { get; set; }

        public bool Marker { get; set; }

        public bool HasImage { get; set; }

        public bool Force { get; set; }

        public bool OverrideExcluded { get; set; }

        public bool Category { get; set; }
    }

    private static void Locks()
    {
        var lib = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var other = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var cfg = new PluginConfiguration();
        var missing = ArtworkRefreshMode.MissingOnly;

        Check.Equal(LockDecision.Allowed, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie), missing), "lock: plain movie allowed");
        Check.Equal(LockDecision.AlreadyHasImage, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => b.HasImage = true), missing), "lock: has image, missing-only");
        Check.Equal(LockDecision.Allowed, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => b.HasImage = true), ArtworkRefreshMode.Replace), "lock: has image, replace");

        // Excluded library beats everything below it.
        cfg.ExcludedLibraryIds = [lib];
        Check.Equal(LockDecision.ExcludedLibrary, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => b.Libraries = [lib]), missing), "lock: excluded library");
        Check.Equal(LockDecision.ExcludedLibrary, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => { b.Libraries = [lib]; b.Force = true; }), missing), "lock: force does not enter excluded library");
        Check.Equal(LockDecision.Allowed, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => { b.Libraries = [lib]; b.Force = true; b.OverrideExcluded = true; }), missing), "lock: force plus override enters excluded library");
        Check.Equal(LockDecision.Allowed, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => b.Libraries = [other]), missing), "lock: other library fine");

        // Included list: items outside it are not covered.
        cfg.ExcludedLibraryIds = [];
        cfg.IncludedLibraryIds = [other];
        Check.Equal(LockDecision.NotIncluded, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => b.Libraries = [lib]), missing), "lock: not in included");
        Check.Equal(LockDecision.Allowed, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Genre, change: b => b.Category = true), missing), "lock: categories ignore library lists");
        cfg.IncludedLibraryIds = [];

        // Native lock, then library lock, then item lock.
        Check.Equal(LockDecision.NativeLock, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => b.Native = true), missing), "lock: native lock");
        Check.Equal(LockDecision.NativeLock, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => { b.Native = true; b.Force = true; }), missing), "lock: force does not skip native lock");
        cfg.RespectNativeLockData = false;
        Check.Equal(LockDecision.Allowed, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => b.Native = true), missing), "lock: native lock can be ignored");
        cfg.RespectNativeLockData = true;

        cfg.ImageLocks = [new ImageLockRule { TargetId = lib, IsLibrary = true, ImageType = "Primary" }];
        Check.Equal(LockDecision.LibraryLock, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => b.Libraries = [lib]), missing), "lock: library lock on Primary");
        Check.Equal(LockDecision.Allowed, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, ImageType.Backdrop, b => b.Libraries = [lib]), missing), "lock: library lock leaves Backdrop");
        Check.Equal(LockDecision.LibraryLock, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => { b.Libraries = [lib]; b.Force = true; }), missing), "lock: force does not skip a library lock");

        var item = Guid.Parse("11111111-1111-1111-1111-111111111111");
        cfg.ImageLocks = [new ImageLockRule { TargetId = item, IsLibrary = false, ImageType = string.Empty }];
        Check.Equal(LockDecision.ItemLock, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie), missing), "lock: item lock, every type");
        Check.Equal(LockDecision.ItemLock, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, ImageType.Logo), missing), "lock: item lock covers Logo too");
        Check.Equal(LockDecision.Allowed, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => b.Force = true), missing), "lock: force skips an item lock");
        cfg.ImageLocks = [];

        // Book primary protection and the Auto Thumbnails marker.
        Check.Equal(LockDecision.BookPrimaryProtected, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Book), missing), "lock: book primary protected");
        Check.Equal(LockDecision.BookPrimaryProtected, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Book), ArtworkRefreshMode.Replace), "lock: book primary protected in replace mode");
        Check.Equal(LockDecision.Allowed, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Book, change: b => b.Force = true), missing), "lock: forced book primary allowed");
        Check.Equal(LockDecision.BookPrimaryProtected, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => b.Marker = true), missing), "lock: Auto Thumbnails marker protects primary");
        Check.Equal(LockDecision.Allowed, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, ImageType.Backdrop, b => b.Marker = true), missing), "lock: marker protects only Primary");
        cfg.RespectAutoThumbnailsMarker = false;
        Check.Equal(LockDecision.Allowed, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, change: b => b.Marker = true), missing), "lock: marker can be ignored");
        cfg.ProtectBookPrimary = false;
        Check.Equal(LockDecision.Allowed, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Book), missing), "lock: protection can be turned off");

        // Unsupported kind and type.
        Check.Equal(LockDecision.UnsupportedKind, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Photo), missing), "lock: unsupported kind");
        cfg.EnabledImageTypes = [ImageType.Primary];
        Check.Equal(LockDecision.UnsupportedType, ImageLockPolicy.Evaluate(cfg, Ctx(BaseItemKind.Movie, ImageType.Logo), missing), "lock: type switched off");
    }

    private static void Selector()
    {
        var cfg = new PluginConfiguration();

        // No keys: TMDb and Fanart are unusable, so a movie only gets Wikimedia.
        Check.True(SourceSelector.Select(cfg, BaseItemKind.Movie).SequenceEqual([SourceIds.Wikimedia]), "selector: no keys, movie -> Wikimedia only");
        cfg.Sources.Tmdb.Enabled = true;
        Check.True(SourceSelector.Select(cfg, BaseItemKind.Movie).SequenceEqual([SourceIds.Wikimedia]), "selector: enabled but no key is unusable");
        cfg.Sources.Tmdb.ApiKey = "k";
        Check.True(SourceSelector.Select(cfg, BaseItemKind.Movie).SequenceEqual([SourceIds.Tmdb, SourceIds.Wikimedia]), "selector: TMDb first with a key");
        cfg.Sources.Fanart.Enabled = true;
        cfg.Sources.Fanart.ApiKey = "f";
        Check.True(SourceSelector.Select(cfg, BaseItemKind.Movie).SequenceEqual([SourceIds.Tmdb, SourceIds.Fanart, SourceIds.Wikimedia]), "selector: default movie order");
        Check.True(SourceSelector.Select(cfg, BaseItemKind.MusicAlbum).SequenceEqual([SourceIds.CoverArtArchive, SourceIds.Fanart]), "selector: album order");
        Check.True(SourceSelector.Select(cfg, BaseItemKind.Genre).SequenceEqual([SourceIds.LocalMosaic]), "selector: genre is a mosaic");
        cfg.MosaicEnabled = false;
        Check.True(SourceSelector.Select(cfg, BaseItemKind.Genre).Count == 0, "selector: genre without mosaic has nothing");
        cfg.MosaicEnabled = true;

        // Custom order, Google forced last and only with key plus engine id.
        cfg.SourceOrder = [new SourceOrderEntry { ItemKind = "Movie", Sources = [SourceIds.GoogleCse, SourceIds.Fanart, SourceIds.Tmdb] }];
        cfg.Sources.GoogleCse.Enabled = true;
        cfg.Sources.GoogleCse.ApiKey = "g";
        Check.True(SourceSelector.Select(cfg, BaseItemKind.Movie).SequenceEqual([SourceIds.Fanart, SourceIds.Tmdb]), "selector: Google needs the engine id");
        cfg.Sources.GoogleCse.ApiKey2 = "cx";
        Check.True(SourceSelector.Select(cfg, BaseItemKind.Movie).SequenceEqual([SourceIds.Fanart, SourceIds.Tmdb, SourceIds.GoogleCse]), "selector: Google always last");
        Check.True(SourceSelector.Select(cfg, BaseItemKind.Series).SequenceEqual([SourceIds.Tmdb, SourceIds.Fanart, SourceIds.Wikimedia]), "selector: custom order is per kind");

        // Google is never added by default.
        cfg.SourceOrder = [];
        Check.True(!SourceSelector.Select(cfg, BaseItemKind.Movie).Contains(SourceIds.GoogleCse), "selector: Google is opt-in per kind");
        Check.True(!new PluginConfiguration().Sources.GoogleCse.Enabled, "default: Google off");
    }

    private static void Safety()
    {
        var sensitiveLib = Guid.NewGuid();
        var cfg = new PluginConfiguration { SensitiveLibraryIds = [sensitiveLib], SensitiveTags = ["adult"], MaximumSharedParentalRatingValue = 12 };
        bool Ok(Guid[] libs, string[] tags, int? rating) => SharedArtworkSafetyPolicy.IsAllowed(cfg, new SharedArtworkSubject(libs, tags, rating));

        Check.True(Ok([], [], 10), "safety: low rating allowed");
        Check.True(!Ok([], [], 16), "safety: rating above limit refused");
        Check.True(!Ok([], [], null), "safety: unrated refused by default");
        Check.True(!Ok([sensitiveLib], [], 0), "safety: sensitive library refused");
        Check.True(!Ok([], ["Adult"], 0), "safety: sensitive tag refused, case-insensitive");
        cfg.ExcludeUnratedFromSharedArtwork = false;
        Check.True(Ok([], [], null), "safety: unrated can be allowed");
        cfg.SharedArtworkSafetyEnabled = false;
        Check.True(Ok([sensitiveLib], ["adult"], 99), "safety: policy can be turned off");
        Check.True(new PluginConfiguration().SharedArtworkSafetyEnabled, "safety: on by default");
    }

    private static void Masking()
    {
        var stored = new SourceConfiguration();
        stored.Tmdb.ApiKey = "secret-tmdb";
        stored.Fanart.ApiKey = "secret-fanart";
        stored.Fanart.ApiKey2 = "client";

        var masked = ConfigurationMasking.MaskedCopy(stored);
        Check.Equal("***", masked.Tmdb.ApiKey, "mask: stored key is masked");
        Check.Equal(string.Empty, masked.GoogleCse.ApiKey, "mask: no key stays empty");
        Check.Equal("secret-tmdb", stored.Tmdb.ApiKey, "mask: stored value untouched");

        var merged = ConfigurationMasking.Merge(stored, masked);
        Check.Equal("secret-tmdb", merged.Tmdb.ApiKey, "merge: mask keeps the stored key");
        Check.Equal("client", merged.Fanart.ApiKey2, "merge: second key kept");

        var incoming = ConfigurationMasking.MaskedCopy(stored);
        incoming.Tmdb.ApiKey = string.Empty;
        incoming.Fanart.ApiKey = " new-key ";
        merged = ConfigurationMasking.Merge(stored, incoming);
        Check.Equal(string.Empty, merged.Tmdb.ApiKey, "merge: empty deletes");
        Check.Equal("new-key", merged.Fanart.ApiKey, "merge: new value replaces (trimmed)");
        Check.True(merged.Tmdb.ApiKey != "***" && merged.Fanart.ApiKey2 != "***", "merge: the mask is never stored");
    }

    private static void State()
    {
        var path = Path.Combine(Path.GetTempPath(), "arf-state-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new ArtworkStateStore(path);
            var id = Guid.NewGuid();
            store.Touch(id, "Primary:0", s => { s.Source = "Tmdb"; s.FailureCount = 2; });
            store.Update(d => d.Scheduler.NextEligibleRunUtc = new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.Zero));
            store.SaveAsync().Wait();
            Check.True(File.Exists(path) && !File.Exists(path + ".tmp"), "state: written atomically, no temp left");

            var again = new ArtworkStateStore(path);
            Check.Equal("Tmdb", again.Get(id, "Primary:0")?.Source, "state: slot survives a restart");
            Check.Equal(2, again.Get(id, "Primary:0")?.FailureCount ?? -1, "state: failure count survives");
            Check.Equal(new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.Zero), again.Use(d => d.Scheduler.NextEligibleRunUtc)!.Value, "state: next eligible survives");

            // A second save replaces the first (File.Replace path).
            again.Touch(id, "Backdrop:0", s => s.Source = "Fanart");
            again.SaveAsync().Wait();
            Check.Equal("Fanart", new ArtworkStateStore(path).Get(id, "Backdrop:0")?.Source, "state: second save replaces the file");

            File.WriteAllText(path, "{ this is not json");
            Check.True(new ArtworkStateStore(path).Get(id, "Primary:0") is null, "state: a damaged file starts clean");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
