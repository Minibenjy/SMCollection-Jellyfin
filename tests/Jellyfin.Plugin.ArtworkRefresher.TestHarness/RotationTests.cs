using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ArtworkRefresher.TestHarness;

internal static class RotationTests
{
    public static Task RunAsync()
    {
        Picking();
        Settings();
        Merging();
        RecentFirst();
        Queue();
        StatePersistence();
        Security();
        return Task.CompletedTask;
    }

    private static List<string> Pool(int n) => Enumerable.Range(1, n).Select(i => "img" + i).ToList();

    private static void Picking()
    {
        // A whole cycle: every image exactly once, never the current one first, never a repeat.
        var pool = Pool(5);
        var history = new List<string>();
        var rng = new Random(42);
        var seen = new List<string>();
        string? current = null;
        for (var i = 0; i < 5; i++)
        {
            var pick = RotationPolicy.Pick(pool, history, current, rng.Next);
            Check.True(pick is not null, "pick: something to pick " + i);
            Check.True(!seen.Contains(pick!.AssetId), "pick: no repeat inside a cycle (" + pick.AssetId + ")");
            Check.True(!pick.HistoryReset || i == 0 && false, "pick: no reset while unused images remain");
            seen.Add(pick.AssetId);
            current = pick.AssetId;
        }

        Check.Equal(5, seen.Distinct().Count(), "pick: the cycle covers the whole pool");

        // The sixth draw starts a new cycle and is not the image showing now.
        var next = RotationPolicy.Pick(pool, history, current, rng.Next)!;
        Check.True(next.HistoryReset, "pick: history restarts when the pool is used up");
        Check.True(next.AssetId != current, "pick: after the reset it is not the current image");
        Check.Equal(2, history.Count, "pick: new history holds the current image and the new pick");

        // Many cycles: never the same image twice in a row, and every image used about equally.
        var counts = pool.ToDictionary(p => p, _ => 0);
        history = [];
        current = null;
        var immediateRepeat = false;
        var r2 = new Random(7);
        for (var i = 0; i < 1000; i++)
        {
            var p = RotationPolicy.Pick(pool, history, current, r2.Next)!;
            immediateRepeat |= p.AssetId == current;
            counts[p.AssetId]++;
            current = p.AssetId;
        }

        Check.True(!immediateRepeat, "pick: 1000 draws never repeat the previous image");
        Check.True(counts.Values.All(v => v is >= 180 and <= 220), "pick: every image is used about equally (" + string.Join(",", counts.Values) + ")");

        // Deterministic with the same seed (injectable randomness).
        string Run(int seed)
        {
            var h = new List<string>();
            var rr = new Random(seed);
            string? c = null;
            var sb = new List<string>();
            for (var i = 0; i < 8; i++)
            {
                c = RotationPolicy.Pick(Pool(4), h, c, rr.Next)!.AssetId;
                sb.Add(c);
            }

            return string.Join(",", sb);
        }

        Check.Equal(Run(3), Run(3), "pick: same seed, same sequence");
        Check.True(Run(3) != Run(4), "pick: another seed, another sequence");

        // A fixed "random" picks the first unused image: easy to reason about.
        var h2 = new List<string>();
        Check.Equal("img1", RotationPolicy.Pick(Pool(3), h2, null, _ => 0)!.AssetId, "pick: injected zero takes the first");
        Check.Equal("img3", RotationPolicy.Pick(Pool(3), h2, "img1", n => n - 1)!.AssetId, "pick: injected last takes the last unused");

        // A pool of one has nothing different to show; an empty pool too.
        Check.True(RotationPolicy.Pick(Pool(1), new List<string>(), "img1", _ => 0) is null, "pick: a single image has no alternative");
        Check.True(RotationPolicy.Pick([], new List<string>(), null, _ => 0) is null, "pick: empty pool");

        // Images that left the pool are forgotten by the history.
        var stale = new List<string> { "gone", "img2" };
        var after = RotationPolicy.Pick(Pool(3), stale, null, _ => 0)!;
        Check.True(!stale.Contains("gone"), "pick: stale history entries are dropped");
        Check.Equal("img1", after.AssetId, "pick: img2 was used, so img1 is first unused");

        // Out-of-range random values cannot crash the draw.
        Check.True(RotationPolicy.Pick(Pool(3), new List<string>(), null, _ => 99) is not null, "pick: out-of-range random is clamped");
    }

    private static void Settings()
    {
        var cfg = new PluginConfiguration
        {
            DefaultPoolSize = 5,
            Rotation =
            [
                new RotationTypeSetting { ImageType = "Primary", Mode = RotationMode.PerLoad, PoolSize = 0 },
                new RotationTypeSetting { ImageType = "backdrop", Mode = RotationMode.Daily, PoolSize = 8 },
                new RotationTypeSetting { ImageType = "Logo", Mode = RotationMode.Off, PoolSize = 100 }
            ]
        };
        Check.Equal(RotationMode.PerLoad, RotationPolicy.ModeFor(cfg, ImageType.Primary), "settings: per load");
        Check.Equal(RotationMode.Daily, RotationPolicy.ModeFor(cfg, ImageType.Backdrop), "settings: type names are case-insensitive");
        Check.Equal(RotationMode.Off, RotationPolicy.ModeFor(cfg, ImageType.Thumb), "settings: a type without entry is off");
        Check.Equal(5, RotationPolicy.PoolSizeFor(cfg, ImageType.Primary), "settings: 0 means the default size");
        Check.Equal(8, RotationPolicy.PoolSizeFor(cfg, ImageType.Backdrop), "settings: own size");
        Check.Equal(RotationPolicy.MaxPool, RotationPolicy.PoolSizeFor(cfg, ImageType.Logo), "settings: size is capped");
        Check.True(RotationPolicy.AnyEnabled(cfg), "settings: any enabled");
        Check.True(!RotationPolicy.AnyEnabled(new PluginConfiguration()), "settings: off by default");
        Check.Equal("Primary", string.Join(",", RotationPolicy.PerLoadTypes(cfg)), "settings: per-load types");
        cfg.DefaultPoolSize = 1;
        Check.Equal(RotationPolicy.MinPool, RotationPolicy.PoolSizeFor(cfg, ImageType.Thumb), "settings: size has a floor");
        Check.Equal(5, new PluginConfiguration().DefaultPoolSize, "settings: default pool is 5");
    }

    private static PoolEntry E(string id) => new() { AssetId = id, Source = "Tmdb", Uri = "https://image.tmdb.org/t/p/w780/" + id + ".jpg" };

    private static void Merging()
    {
        var existing = new List<PoolEntry> { new() { AssetId = "a", CachedFile = "a.jpg", Source = "Tmdb", Uri = "https://x/a" }, E("b") };
        var found = new List<PoolEntry> { E("c"), E("a"), E("d"), E("c"), E("e"), E("f") };
        var merged = RotationPolicy.Merge(existing, found, 4, null);
        Check.Equal(4, merged.Count, "merge: respects the size");
        Check.Equal(4, merged.Select(m => m.AssetId).Distinct().Count(), "merge: no duplicates");
        Check.Equal("a.jpg", merged.First(m => m.AssetId == "a").CachedFile, "merge: a kept image keeps its cache");
        Check.True(merged.All(m => m.AssetId != "b"), "merge: an image no longer on offer is dropped");

        var withCurrent = RotationPolicy.Merge(existing, found, 2, "e");
        Check.Equal("e", withCurrent[0].AssetId, "merge: the image showing now stays in the pool");
        Check.True(RotationPolicy.Merge(null, [], 5, null).Count == 0, "merge: nothing found, empty pool");
        Check.True(RotationPolicy.Merge(null, [E(string.Empty)], 5, null).Count == 0, "merge: entries without an id are ignored");
    }

    private static void RecentFirst()
    {
        var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        var items = new[] { ("old1", now.AddDays(-100)), ("new1", now.AddDays(-2)), ("old2", now.AddDays(-30)), ("new2", now.AddDays(-1)), ("edge", now.AddDays(-14)) };
        var (recent, others) = RecentFirstPolicy.Split(items, i => i.Item2, now.AddDays(-14));
        Check.Equal("new2,new1,edge", string.Join(",", recent.Select(r => r.Item1)), "recent: newest first, cutoff inclusive");
        Check.Equal("old1,old2", string.Join(",", others.Select(r => r.Item1)), "recent: the rest keeps its order");
    }

    private static void Queue()
    {
        var q = new NewItemQueue(3);
        var t0 = new DateTimeOffset(2026, 10, 6, 3, 0, 0, TimeSpan.Zero);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        q.Add(a, t0);
        q.Add(b, t0.AddSeconds(100));
        Check.Equal(0, q.Due(t0.AddSeconds(60), TimeSpan.FromSeconds(120), 10).Count, "queue: nothing is due before the delay");
        var due = q.Due(t0.AddSeconds(130), TimeSpan.FromSeconds(120), 10);
        Check.Equal(1, due.Count, "queue: only the old one is due");
        Check.Equal(a, due[0], "queue: the right one");
        q.Add(a, t0.AddSeconds(125));
        Check.Equal(0, q.Due(t0.AddSeconds(130), TimeSpan.FromSeconds(120), 10).Count, "queue: an item seen again waits again");
        Check.Equal(2, q.Due(t0.AddSeconds(500), TimeSpan.FromSeconds(120), 10).Count, "queue: both due later");
        Check.Equal(1, q.Due(t0.AddSeconds(500), TimeSpan.FromSeconds(120), 1).Count, "queue: limited batch");
        q.Remove([a]);
        Check.Equal(1, q.Count, "queue: removal");
        q.Add(c, t0.AddSeconds(200));
        q.Add(Guid.NewGuid(), t0.AddSeconds(201));
        q.Add(Guid.NewGuid(), t0.AddSeconds(202));
        Check.Equal(3, q.Count, "queue: bounded");
        Check.True(!q.Due(t0.AddSeconds(900), TimeSpan.Zero, 10).Contains(b), "queue: the oldest was dropped when full");
    }

    private static void StatePersistence()
    {
        var dir = Path.Combine(Path.GetTempPath(), "arf-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(dir, "state.json");
            var store = new ArtworkStateStore(file);
            var id = Guid.NewGuid();
            store.TouchPool(id, "Primary:0", p =>
            {
                p.Entries = [E("a"), E("b")];
                p.History = ["a"];
                p.LastRotationDay = "2026-10-06";
                p.RotationChanges = 3;
                p.WrittenHash = "abc";
                p.OriginalBackup = "orig.jpg";
            });
            store.Touch(id, "Metadata:0", s => s.FilledFields = new Dictionary<string, string> { ["Overview"] = "h1" });
            store.Update(d =>
            {
                d.Scheduler.LastRunRotationChanges = 4;
                d.Scheduler.LastRunMetadataFilled = new Dictionary<string, int> { ["Overview"] = 2 };
                d.SigningKey = RotationSigner.NewKey();
            });
            store.SaveAsync(true).GetAwaiter().GetResult();

            Check.True(File.Exists(Path.Combine(dir, "pools.json")), "state: pools live in their own file");
            Check.True(!File.ReadAllText(file).Contains("\"a\"", StringComparison.Ordinal) || !File.ReadAllText(file).Contains("Entries", StringComparison.Ordinal), "state: the main file holds no pool entries");

            var reloaded = new ArtworkStateStore(file);
            var pool = reloaded.GetPool(id, "Primary:0");
            Check.Equal(2, pool?.Entries.Count ?? 0, "state: pool survives a restart");
            Check.Equal("a", pool?.History.FirstOrDefault() ?? string.Empty, "state: history survives a restart");
            Check.Equal("2026-10-06", pool?.LastRotationDay ?? string.Empty, "state: rotation day survives");
            Check.Equal(3, pool?.RotationChanges ?? 0, "state: change counter survives");
            Check.Equal("orig.jpg", pool?.OriginalBackup ?? string.Empty, "state: original backup name survives");
            Check.Equal("abc", pool?.WrittenHash ?? string.Empty, "state: hash of the last written image survives");
            Check.Equal("h1", reloaded.Get(id, "Metadata:0")?.FilledFields?["Overview"] ?? string.Empty, "state: filled-field record survives");
            Check.Equal(4, reloaded.Use(d => d.Scheduler.LastRunRotationChanges), "state: rotation changes of the last run are kept");
            Check.Equal(2, reloaded.Use(d => d.Scheduler.LastRunMetadataFilled["Overview"]), "state: metadata counters are kept");
            Check.True(!string.IsNullOrEmpty(reloaded.Use(d => d.SigningKey)), "state: signing key survives");

            // The pools file is written lazily: a plain save right after a change does not rewrite it.
            reloaded.TouchPool(id, "Primary:0", p => p.RotationChanges = 8);
            reloaded.SaveAsync().GetAwaiter().GetResult();
            var before = File.GetLastWriteTimeUtc(Path.Combine(dir, "pools.json"));
            Thread.Sleep(30);
            reloaded.TouchPool(id, "Primary:0", p => p.RotationChanges = 9);
            reloaded.SaveAsync().GetAwaiter().GetResult();
            Check.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(dir, "pools.json")), "state: pools file is not rewritten on every save");
            reloaded.SaveAsync(true).GetAwaiter().GetResult();
            Check.Equal(9, new ArtworkStateStore(file).GetPool(id, "Primary:0")?.RotationChanges ?? 0, "state: a forced save writes the pools");

            // An old state file without the new files or fields still loads; a damaged pools file only loses the pools.
            File.Delete(Path.Combine(dir, "pools.json"));
            File.WriteAllText(file, "{\"SchemaVersion\":1,\"Items\":{\"" + id + "\":{\"Primary:0\":{\"Source\":\"Tmdb\"}}}}");
            var old = new ArtworkStateStore(file).Get(id, "Primary:0");
            Check.Equal("Tmdb", old?.Source ?? string.Empty, "state: a 1.0 state file loads");
            File.WriteAllText(Path.Combine(dir, "pools.json"), "{ not json");
            var damaged = new ArtworkStateStore(file);
            Check.True(damaged.GetPool(id, "Primary:0") is null && damaged.Get(id, "Primary:0") is not null, "state: a damaged pools file loses only the pools");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    public static void Security()
    {
        var key = RotationSigner.NewKey();
        var id = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 6, 3, 0, 0, TimeSpan.Zero);
        var token = RotationSigner.Sign(key, id, "Primary", now);
        Check.True(RotationSigner.Validate(key, id, "Primary", token, now.AddMinutes(30)), "signer: a fresh token is valid");
        Check.True(RotationSigner.Validate(key, id, "primary", token, now), "signer: the type is case-insensitive");
        Check.True(!RotationSigner.Validate(key, id, "Backdrop", token, now), "signer: another type is refused");
        Check.True(!RotationSigner.Validate(key, Guid.NewGuid(), "Primary", token, now), "signer: another item is refused");
        Check.True(!RotationSigner.Validate(key, id, "Primary", token, now.Add(RotationSigner.Lifetime).AddSeconds(5)), "signer: an expired token is refused");
        Check.True(!RotationSigner.Validate(RotationSigner.NewKey(), id, "Primary", token, now), "signer: another key is refused");
        Check.True(!RotationSigner.Validate(key, id, "Primary", token[..^2] + "00", now) || token.EndsWith("00", StringComparison.Ordinal), "signer: a changed signature is refused");
        var dot = token.IndexOf('.', StringComparison.Ordinal);
        Check.True(!RotationSigner.Validate(key, id, "Primary", (long.Parse(token[..dot]) + 99999) + token[dot..], now), "signer: a changed expiry is refused");
        Check.True(!RotationSigner.Validate(key, id, "Primary", null, now) && !RotationSigner.Validate(null, id, "Primary", token, now) && !RotationSigner.Validate(key, id, "Primary", "garbage", now), "signer: missing or malformed input is refused");

        var memo = new PickMemo(TimeSpan.FromMinutes(5), 3);
        memo.Set("k1", "imgA", now);
        Check.True(memo.TryGet("k1", now.AddMinutes(1), out var v) && v == "imgA", "memo: the same page view gets the same image");
        Check.True(!memo.TryGet("k1", now.AddMinutes(6), out _), "memo: forgotten after its lifetime");
        Check.True(!memo.TryGet("other", now, out _), "memo: unknown key");
        memo.Set("k2", "b", now.AddSeconds(1));
        memo.Set("k3", "c", now.AddSeconds(2));
        memo.Set("k4", "d", now.AddSeconds(3));
        Check.True(!memo.TryGet("k1", now.AddSeconds(4), out _) && memo.TryGet("k4", now.AddSeconds(4), out _), "memo: bounded, the oldest goes first");
    }
}
