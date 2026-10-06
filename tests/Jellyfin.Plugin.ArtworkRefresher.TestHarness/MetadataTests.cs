using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using Jellyfin.Plugin.ArtworkRefresher.Sources;

namespace Jellyfin.Plugin.ArtworkRefresher.TestHarness;

internal static class MetadataTests
{
    private static readonly Guid Library = Guid.NewGuid();
    private static readonly Guid BookLibrary = Guid.NewGuid();

    public static async Task RunAsync()
    {
        Gaps();
        Plans();
        Parsing();
        await Fetching();
    }

    private static ItemMetadataSnapshot Item(BaseItemKind kind = BaseItemKind.Movie, string? overview = null, string[]? genres = null, int? year = null, float? rating = null, string[]? studios = null, string[]? taglines = null, string[]? locked = null, bool nativeLocked = false, Guid? library = null)
        => new()
        {
            Kind = kind,
            LibraryIds = [library ?? Library],
            Overview = overview,
            Genres = genres ?? [],
            ProductionYear = year,
            CommunityRating = rating,
            Studios = studios ?? [],
            Taglines = taglines ?? [],
            LockedFields = locked ?? [],
            NativeLocked = nativeLocked
        };

    private static void Gaps()
    {
        var cfg = new PluginConfiguration();
        var all = MetadataGapPolicy.Gaps(cfg, Item());
        Check.Equal(6, all.Count, "gaps: an empty movie has every gap");

        var full = Item(overview: "Text", genres: ["Drama"], year: 2001, rating: 7.1f, studios: ["S"], taglines: ["T"]);
        Check.Equal(0, MetadataGapPolicy.Gaps(cfg, full).Count, "gaps: a complete movie has none");

        var partial = MetadataGapPolicy.Gaps(cfg, Item(overview: "Text", year: 2001));
        Check.True(!partial.Contains("Overview") && !partial.Contains("ProductionYear"), "gaps: filled fields are not gaps");
        Check.True(partial.Contains("Genres") && partial.Contains("Studios"), "gaps: empty ones are");
        Check.Equal(0, MetadataGapPolicy.Gaps(cfg, Item(overview: "   ", year: 0, genres: ["x"], rating: 5, studios: ["s"], taglines: ["t"])).Count - 2, "gaps: a blank overview and year 0 count as empty");

        // Locked fields are never gaps.
        var locked = MetadataGapPolicy.Gaps(cfg, Item(locked: ["Overview", "Genres"]));
        Check.True(!locked.Contains("Overview") && !locked.Contains("Genres"), "gaps: locked fields are skipped");
        Check.True(locked.Contains("Studios"), "gaps: other fields still open");
        Check.Equal(0, MetadataGapPolicy.Gaps(cfg, Item(nativeLocked: true)).Count, "gaps: an item locked in Jellyfin is left alone");
        cfg.RespectNativeLockData = false;
        Check.True(MetadataGapPolicy.Gaps(cfg, Item(nativeLocked: true)).Count > 0, "gaps: unless that protection is switched off");
        cfg.RespectNativeLockData = true;

        // The field list in the settings narrows what may be filled.
        cfg.MetadataFieldsToFill = ["overview", "Genres"];
        var narrow = MetadataGapPolicy.Gaps(cfg, Item());
        Check.Equal("Overview,Genres", string.Join(",", narrow), "gaps: only the chosen fields (case-insensitive)");
        cfg.MetadataFieldsToFill = [];

        // Kinds: episodes have no genres/studios/tagline, people only an overview.
        var ep = MetadataGapPolicy.Gaps(cfg, Item(BaseItemKind.Episode));
        Check.True(!ep.Contains("Genres") && !ep.Contains("Studios") && !ep.Contains("Tagline") && ep.Contains("Overview"), "gaps: episode fields");
        Check.Equal("Overview", string.Join(",", MetadataGapPolicy.Gaps(cfg, Item(BaseItemKind.Person))), "gaps: a person only needs a biography");
        Check.Equal(0, MetadataGapPolicy.Gaps(cfg, Item(BaseItemKind.MusicAlbum)).Count, "gaps: unsupported kinds are never touched");

        // Libraries.
        cfg.ExcludedLibraryIds = [Library];
        Check.Equal(0, MetadataGapPolicy.Gaps(cfg, Item()).Count, "gaps: excluded library");
        cfg.ExcludedLibraryIds = [];
        cfg.IncludedLibraryIds = [Guid.NewGuid()];
        Check.Equal(0, MetadataGapPolicy.Gaps(cfg, Item()).Count, "gaps: not in an included library");
        cfg.IncludedLibraryIds = [];

        // Books, comics and magazines: never, unless that library is opted in.
        Check.Equal(0, MetadataGapPolicy.Gaps(cfg, Item(BaseItemKind.Book, library: BookLibrary)).Count, "gaps: books are not touched by default");
        cfg.MetadataBookLibraryOptIn = [BookLibrary];
        Check.True(!MetadataGapPolicy.IsEligible(cfg, Item(BaseItemKind.Book, library: Library)), "gaps: an opt-in covers only its own library");
        Check.True(!MetadataGapPolicy.IsEligible(cfg, Item(BaseItemKind.Book, library: BookLibrary)), "gaps: TMDb has no book data, so even an opted-in book is not looked up");
    }

    private static void Plans()
    {
        var remote = new RemoteMetadata { Overview = " New text ", Genres = ["Action"], ProductionYear = 1999, CommunityRating = 8.2f, Studios = ["Studio"], Tagline = "Tag" };
        var cfg = new PluginConfiguration();

        // Existing values are never in the plan, because only gaps are planned.
        var snapshot = Item(overview: "Mine", year: 2000);
        var plan = MetadataGapPolicy.Plan(MetadataGapPolicy.Gaps(cfg, snapshot), remote);
        Check.True(plan.All(p => p.Field is not ("Overview" or "ProductionYear")), "plan: an existing overview and year are never replaced");
        Check.Equal(4, plan.Count, "plan: the four real gaps");
        Check.Equal("Action", ((string[])plan.First(p => p.Field == "Genres").Value)[0], "plan: genres value");
        Check.Equal("Tag", (string)plan.First(p => p.Field == "Tagline").Value, "plan: tagline value");

        var trimmed = MetadataGapPolicy.Plan(["Overview"], remote);
        Check.Equal("New text", (string)trimmed[0].Value, "plan: overview is trimmed");

        // A gap the source cannot fill stays a gap.
        Check.Equal(0, MetadataGapPolicy.Plan(["Overview", "Genres", "ProductionYear", "CommunityRating", "Studios", "Tagline"], new RemoteMetadata()).Count, "plan: nothing from an empty answer");
        Check.Equal(0, MetadataGapPolicy.Plan(["CommunityRating"], new RemoteMetadata { CommunityRating = 0 }).Count, "plan: a zero rating is not data");
        Check.Equal(0, MetadataGapPolicy.Plan(["ProductionYear"], new RemoteMetadata { ProductionYear = 0 }).Count, "plan: year 0 is not data");
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static void Parsing()
    {
        var movie = TmdbMetadataSource.Parse(Json("""{"overview":"  Plot ","tagline":"Tag","release_date":"1999-03-31","vote_average":8.2345,"vote_count":100,"genres":[{"name":"Action"},{"name":"Action"},{"name":" Sci-Fi "}],"production_companies":[{"name":"Warner"}]}"""), BaseItemKind.Movie);
        Check.Equal("Plot", movie.Overview ?? string.Empty, "parse movie: overview trimmed");
        Check.Equal(1999, movie.ProductionYear ?? 0, "parse movie: year");
        Check.Equal(8.2f, movie.CommunityRating ?? 0, "parse movie: rating rounded to one decimal");
        Check.Equal("Action,Sci-Fi", string.Join(",", movie.Genres), "parse movie: genres deduplicated");
        Check.Equal("Warner", movie.Studios[0], "parse movie: studio");
        Check.Equal("Tag", movie.Tagline ?? string.Empty, "parse movie: tagline");

        var unrated = TmdbMetadataSource.Parse(Json("""{"overview":"","vote_average":0,"vote_count":0,"release_date":""}"""), BaseItemKind.Movie);
        Check.True(unrated.Overview is null && unrated.CommunityRating is null && unrated.ProductionYear is null, "parse movie: empty values are null");
        var noVotes = TmdbMetadataSource.Parse(Json("""{"vote_average":7.5,"vote_count":0}"""), BaseItemKind.Movie);
        Check.True(noVotes.CommunityRating is null, "parse movie: a rating without votes is ignored");

        var tv = TmdbMetadataSource.Parse(Json("""{"overview":"S","first_air_date":"2008-01-20","networks":[{"name":"AMC"}],"production_companies":[{"name":"Sony"},{"name":"amc"}]}"""), BaseItemKind.Series);
        Check.Equal(2008, tv.ProductionYear ?? 0, "parse series: first air year");
        Check.Equal("AMC,Sony", string.Join(",", tv.Studios), "parse series: networks first, no duplicates");

        var ep = TmdbMetadataSource.Parse(Json("""{"overview":"E","air_date":"2010-05-01","vote_average":7,"vote_count":3}"""), BaseItemKind.Episode);
        Check.Equal(2010, ep.ProductionYear ?? 0, "parse episode: air date year");

        var person = TmdbMetadataSource.Parse(Json("""{"biography":"Bio","overview":"ignored"}"""), BaseItemKind.Person);
        Check.Equal("Bio", person.Overview ?? string.Empty, "parse person: biography is the overview");
    }

    private sealed class FakeHttp : IArtworkHttp
    {
        public List<Uri> Requests { get; } = [];

        public Dictionary<string, string> Headers { get; } = [];

        public Func<Uri, string?> Answer { get; set; } = _ => null;

        public Task<JsonDocument?> GetJsonAsync(string sourceId, Uri uri, int requestsPerMinute, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            Requests.Add(uri);
            foreach (var kv in headers ?? new Dictionary<string, string>())
            {
                Headers[kv.Key] = kv.Value;
            }

            var body = Answer(uri);
            return Task.FromResult(body is null ? null : JsonDocument.Parse(body));
        }
    }

    private static async Task Fetching()
    {
        var cfg = new PluginConfiguration();
        cfg.Sources.Tmdb.Enabled = true;
        cfg.Sources.Tmdb.ApiKey = "tmdbkey";
        var http = new FakeHttp { Answer = u => u.AbsolutePath.StartsWith("/3/movie/603", StringComparison.Ordinal) ? """{"overview":"Neo","release_date":"1999-03-31"}""" : null };
        var q = new ArtworkQuery { Kind = BaseItemKind.Movie, Name = "X", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "603" }, Languages = ["es"] };
        var got = await TmdbMetadataSource.FetchAsync(q, http, cfg, CancellationToken.None);
        Check.Equal("Neo", got?.Overview ?? string.Empty, "fetch: by TMDb id");
        Check.True(http.Requests[0].Query.Contains("language=es", StringComparison.Ordinal), "fetch: the configured language is requested");
        Check.True(http.Requests[0].Query.Contains("api_key=tmdbkey", StringComparison.Ordinal), "fetch: v3 key as parameter");
        Check.Equal(1, http.Requests.Count, "fetch: a single call when the id is known");

        // No usable id: no name search (a wrong match would put wrong text on the item).
        var none = new FakeHttp { Answer = _ => "{}" };
        var unknown = new ArtworkQuery { Kind = BaseItemKind.Movie, Name = "Some Title", Year = 2000 };
        Check.True(await TmdbMetadataSource.FetchAsync(unknown, none, cfg, CancellationToken.None) is null, "fetch: no id, no answer");
        Check.Equal(0, none.Requests.Count, "fetch: no id means no request at all");

        // IMDb id goes through find, then one detail call.
        var imdb = new FakeHttp { Answer = u => u.AbsolutePath.Contains("/find/", StringComparison.Ordinal) ? """{"tv_results":[{"id":1396}]}""" : """{"overview":"BB","first_air_date":"2008-01-20"}""" };
        var viaImdb = await TmdbMetadataSource.FetchAsync(new ArtworkQuery { Kind = BaseItemKind.Series, Name = "BB", ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt0903747" } }, imdb, cfg, CancellationToken.None);
        Check.Equal(2008, viaImdb?.ProductionYear ?? 0, "fetch: series by IMDb id");
        Check.True(imdb.Requests.Any(r => r.AbsolutePath.EndsWith("/tv/1396", StringComparison.Ordinal)), "fetch: detail call uses the found id");

        // Episode goes through the series id.
        var epHttp = new FakeHttp { Answer = _ => """{"overview":"Ep"}""" };
        var ep = await TmdbMetadataSource.FetchAsync(new ArtworkQuery { Kind = BaseItemKind.Episode, Name = "E", SeriesProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1396" }, SeasonNumber = 2, EpisodeNumber = 3 }, epHttp, cfg, CancellationToken.None);
        Check.Equal("Ep", ep?.Overview ?? string.Empty, "fetch: episode");
        Check.True(epHttp.Requests[0].AbsolutePath.EndsWith("/tv/1396/season/2/episode/3", StringComparison.Ordinal), "fetch: episode path");

        // TMDb off or without key: nothing is sent.
        var off = new FakeHttp { Answer = _ => "{}" };
        cfg.Sources.Tmdb.Enabled = false;
        Check.True(await TmdbMetadataSource.FetchAsync(q, off, cfg, CancellationToken.None) is null && off.Requests.Count == 0, "fetch: TMDb disabled, no request");

        // v4 read token goes in a header, not in the address.
        cfg.Sources.Tmdb.Enabled = true;
        cfg.Sources.Tmdb.ApiKey = "eyJhbGciOi.token";
        var bearer = new FakeHttp { Answer = _ => """{"overview":"x"}""" };
        await TmdbMetadataSource.FetchAsync(q, bearer, cfg, CancellationToken.None);
        Check.True(bearer.Headers.TryGetValue("Authorization", out var h) && h.StartsWith("Bearer ", StringComparison.Ordinal), "fetch: bearer token header");
        Check.True(!bearer.Requests[0].Query.Contains("api_key", StringComparison.Ordinal), "fetch: token never in the address");
    }
}
