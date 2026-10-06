using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using Jellyfin.Plugin.ArtworkRefresher.Sources;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ArtworkRefresher.TestHarness;

internal static class SourceParserTests
{
    public static async Task RunAsync()
    {
        Tmdb();
        Fanart();
        Wikimedia();
        OpenLibrary();
        CoverArt();
        Google();
        await Flows();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static ArtworkQuery Q(BaseItemKind kind, string name = "Some Title", string[]? langs = null, bool neutral = true, params ImageType[] wanted)
        => new() { Kind = kind, Name = name, Languages = langs ?? ["es"], IncludeNeutral = neutral, WantedTypes = wanted.Length == 0 ? [ImageType.Primary, ImageType.Backdrop, ImageType.Logo] : wanted };

    private static void AllHttps(IEnumerable<ArtworkCandidate> candidates, string name)
        => Check.True(candidates.All(c => c.Uri.Scheme == Uri.UriSchemeHttps), name + ": every candidate address is https");

    private static void Tmdb()
    {
        var json = Json("""
        { "posters": [
            {"file_path":"/p_es.jpg","iso_639_1":"es","width":2000,"height":3000,"vote_average":5.0},
            {"file_path":"/p_en.jpg","iso_639_1":"en","width":2000,"height":3000,"vote_average":9.0},
            {"file_path":"/p_none.jpg","iso_639_1":null,"width":2000,"height":3000,"vote_average":1.0},
            {"file_path":"no-slash.jpg","iso_639_1":"es","width":1,"height":1}
          ],
          "backdrops": [ {"file_path":"/b_none.jpg","iso_639_1":null,"width":3840,"height":2160,"vote_average":6}, {"file_path":"/b_es.jpg","iso_639_1":"es","width":1920,"height":1080,"vote_average":9} ],
          "logos": [ {"file_path":"/l_es.png","iso_639_1":"es","width":500,"height":200,"vote_average":3} ] }
        """);
        var list = TmdbSource.ParseImages(json, Q(BaseItemKind.Movie), ("w780", "w1280", "w500"), false, false);
        AllHttps(list, "tmdb");
        var posters = list.Where(c => c.ImageType == ImageType.Primary).ToList();
        Check.Equal("/p_es.jpg", posters[0].RemoteId, "tmdb: preferred language ranks first despite lower votes");
        Check.True(posters.All(c => c.RemoteId != "/p_en.jpg"), "tmdb: foreign language dropped");
        Check.True(posters.Any(c => c.RemoteId == "/p_none.jpg"), "tmdb: language-neutral kept");
        Check.True(posters.All(c => c.RemoteId != "no-slash.jpg"), "tmdb: path without a slash ignored");
        Check.Equal("https://image.tmdb.org/t/p/w780/p_es.jpg", posters[0].Uri.ToString(), "tmdb: poster size and host");
        var backdrops = list.Where(c => c.ImageType == ImageType.Backdrop).ToList();
        Check.Equal("/b_none.jpg", backdrops[0].RemoteId, "tmdb: neutral backdrop beats a text one");
        Check.True(list.Any(c => c.ImageType == ImageType.Thumb), "tmdb: backdrops also offered as thumbs");
        Check.True(list.Any(c => c.ImageType == ImageType.Logo && c.Uri.ToString().Contains("/w500/", StringComparison.Ordinal)), "tmdb: logo size");

        var noNeutral = TmdbSource.ParseImages(json, Q(BaseItemKind.Movie, neutral: false), ("w780", "w1280", "w500"), false, false);
        Check.True(noNeutral.All(c => c.Language is not null), "tmdb: neutral images dropped when not wanted");

        var company = TmdbSource.ParseImages(Json("""{"logos":[{"file_path":"/c.png","iso_639_1":null,"width":300,"height":300}]}"""), Q(BaseItemKind.Studio), ("w780", "w1280", "w500"), false, true);
        Check.True(company.Any(c => c.ImageType == ImageType.Primary), "tmdb: a studio logo is its primary image");

        var person = TmdbSource.ParseImages(Json("""{"profiles":[{"file_path":"/x.jpg","iso_639_1":null,"width":500,"height":700}]}"""), Q(BaseItemKind.Person), ("w780", "w1280", "w500"), true, false);
        Check.True(person.Count == 1 && person[0].ImageType == ImageType.Primary, "tmdb: person profile is primary");
        Check.True(TmdbSource.ParseImages(Json("{}"), Q(BaseItemKind.Movie), ("a", "b", "c"), false, false).Count == 0, "tmdb: empty response");
    }

    private static void Fanart()
    {
        var json = Json("""
        { "movieposter":[{"id":"1","url":"https://assets.fanart.tv/fanart/movies/1/movieposter/a.jpg","lang":"es","likes":"5"},{"id":"2","url":"not a url","lang":"es"},{"id":"3","url":"https://assets.fanart.tv/x/b.jpg","lang":"de","likes":"50"}],
          "hdmovielogo":[{"id":"9","url":"https://assets.fanart.tv/fanart/movies/1/hdmovielogo/l.png","lang":"es","likes":"1"}],
          "seasonposter":[{"id":"7","url":"https://assets.fanart.tv/s1.jpg","lang":"es","season":"1"},{"id":"8","url":"https://assets.fanart.tv/s2.jpg","lang":"es","season":"2"}] }
        """);
        var list = FanartSource.Parse(json, Q(BaseItemKind.Movie), [("movieposter", ImageType.Primary), ("hdmovielogo", ImageType.Logo)]);
        AllHttps(list, "fanart");
        Check.Equal(2, list.Count, "fanart: bad url and foreign language dropped");
        Check.Equal("1", list[0].RemoteId, "fanart: best candidate first");
        var seasons = FanartSource.Parse(json, Q(BaseItemKind.Season), [("seasonposter", ImageType.Primary)], 2);
        Check.True(seasons.Count == 1 && seasons[0].RemoteId == "8", "fanart: season filter");
    }

    private static void Wikimedia()
    {
        var json = Json("""
        { "query": { "pages": {
          "1": {"title":"File:Some Person 2015.jpg","imageinfo":[{"url":"https://upload.wikimedia.org/a/Some_Person_2015.jpg","thumburl":"https://upload.wikimedia.org/a/thumb/Some_Person_2015.jpg/1200px-x.jpg","thumbwidth":1200,"thumbheight":1600,"mime":"image/jpeg","descriptionurl":"https://commons.wikimedia.org/wiki/File:Some_Person_2015.jpg","extmetadata":{"LicenseShortName":{"value":"CC BY-SA 4.0"},"LicenseUrl":{"value":"https://creativecommons.org/licenses/by-sa/4.0"},"Artist":{"value":"<a href='x'>Jane Doe</a>"}}}]},
          "2": {"title":"File:Other Name.jpg","imageinfo":[{"url":"https://upload.wikimedia.org/b.jpg","mime":"image/jpeg","extmetadata":{"LicenseShortName":{"value":"CC0"}}}]},
          "3": {"title":"File:Some Person fair.jpg","imageinfo":[{"url":"https://upload.wikimedia.org/c.jpg","mime":"image/jpeg","extmetadata":{"LicenseShortName":{"value":"Fair use"}}}]},
          "4": {"title":"File:Some Person nolicense.jpg","imageinfo":[{"url":"https://upload.wikimedia.org/d.jpg","mime":"image/jpeg","extmetadata":{}}]},
          "5": {"title":"File:Some Person.gif","imageinfo":[{"url":"https://upload.wikimedia.org/e.gif","mime":"image/gif","extmetadata":{"LicenseShortName":{"value":"CC0"}}}]}
        } } }
        """);
        var list = WikimediaSource.Parse(json, Q(BaseItemKind.Person, "Some Person"), false);
        AllHttps(list, "wikimedia");
        Check.Equal(1, list.Count, "wikimedia: wrong name, fair use, no licence and gif all dropped");
        Check.Equal("CC BY-SA 4.0", list[0].Attribution?.License, "wikimedia: licence carried");
        Check.Equal("Jane Doe", list[0].Attribution?.Author, "wikimedia: author without html");
        Check.True(list[0].Uri.ToString().Contains("thumb", StringComparison.Ordinal), "wikimedia: raster thumbnail preferred");
        Check.Equal(0, WikimediaSource.Parse(json, Q(BaseItemKind.Movie, "Some Person"), true).Count, "wikimedia: a film needs 'poster' in the file name");
    }

    private static void OpenLibrary()
    {
        var json = Json("""{"docs":[{"key":"/works/OL1W","title":"The Book Title","cover_i":12345},{"key":"/works/OL2W","title":"Different","cover_i":9},{"key":"/works/OL3W","title":"The Book Title","cover_i":0}]}""");
        var list = OpenLibrarySource.Parse(json, Q(BaseItemKind.Book, "The Book Title"));
        AllHttps(list, "openlibrary");
        Check.Equal(1, list.Count, "openlibrary: only the matching title with a cover");
        Check.Equal("https://covers.openlibrary.org/b/id/12345-L.jpg", list[0].Uri.ToString(), "openlibrary: cover address");
    }

    private static void CoverArt()
    {
        var json = Json("""{"images":[{"id":1,"front":false,"image":"http://coverartarchive.org/back.jpg"},{"id":2,"front":true,"image":"http://coverartarchive.org/f.jpg","thumbnails":{"500":"https://archive.org/500.jpg","1200":"https://archive.org/1200.jpg"}}]}""");
        var list = CoverArtArchiveSource.Parse(json);
        Check.Equal(1, list.Count, "caa: only the front cover");
        Check.Equal("https://archive.org/1200.jpg", list[0].Uri.ToString(), "caa: 1200 px rendition preferred");
        var http = CoverArtArchiveSource.Parse(Json("""{"images":[{"id":3,"front":true,"image":"http://coverartarchive.org/f.jpg"}]}"""));
        Check.Equal(0, http.Count, "caa: an http-only image is dropped at parse time (contract: candidates are https)");
    }

    private static void Google()
    {
        var json = Json("""{"items":[{"link":"https://img.example.org/a.jpg","image":{"width":1000,"height":1500,"contextLink":"https://example.org/page"}},{"link":"javascript:alert(1)"},{"link":"https://img.example.org/b.jpg"}]}""");
        var list = GoogleCseSource.Parse(json, ImageType.Primary);
        Check.Equal(2, list.Count, "google: non-address results dropped");
        Check.True(list.All(c => c.AnyPublicHost), "google: search results may be on any public host");
        Check.Equal("Some Title 2020 poster", GoogleCseSource.BuildText(new ArtworkQuery { Kind = BaseItemKind.Movie, Name = "Some Title", Year = 2020 }, ImageType.Primary), "google: search text for a poster");
        Check.Equal("Band album cover", GoogleCseSource.BuildText(new ArtworkQuery { Kind = BaseItemKind.MusicAlbum, Name = "Band" }, ImageType.Primary), "google: search text for an album");
    }

    private sealed class FakeHttp : IArtworkHttp
    {
        public List<Uri> Requests { get; } = [];

        public Func<Uri, string?> Answer { get; set; } = _ => null;

        public Task<JsonDocument?> GetJsonAsync(string sourceId, Uri uri, int requestsPerMinute, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            Requests.Add(uri);
            var body = Answer(uri);
            return Task.FromResult(body is null ? null : JsonDocument.Parse(body));
        }
    }

    private static async Task Flows()
    {
        var cfg = new PluginConfiguration();
        cfg.Sources.Tmdb.ApiKey = "tmdbkey";
        cfg.Sources.Fanart.ApiKey = "fkey";

        // With a TMDb id on the item there is no text search at all.
        var http = new FakeHttp
        {
            Answer = u => u.AbsolutePath.EndsWith("/images", StringComparison.Ordinal) ? """{"posters":[{"file_path":"/p.jpg","iso_639_1":null,"width":1000,"height":1500}]}""" : null
        };
        var withId = new ArtworkQuery { Kind = BaseItemKind.Movie, Name = "X", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "603" }, WantedTypes = [ImageType.Primary], Languages = [] };
        var found = await new TmdbSource().FindAsync(withId, http, cfg, CancellationToken.None);
        Check.Equal(1, found.Count, "tmdb flow: candidate from the id");
        Check.True(http.Requests.All(r => !r.AbsolutePath.Contains("search", StringComparison.Ordinal)), "tmdb flow: an exact id means no text search");
        Check.True(http.Requests[0].Query.Contains("api_key=tmdbkey", StringComparison.Ordinal), "tmdb flow: v3 key sent as a parameter");

        // Without an id, an IMDb id is resolved before any name search.
        http.Requests.Clear();
        http.Answer = u =>
        {
            if (u.AbsolutePath.Contains("/find/", StringComparison.Ordinal))
            {
                return """{"movie_results":[{"id":603}]}""";
            }

            return u.AbsolutePath.EndsWith("/images", StringComparison.Ordinal) ? """{"posters":[{"file_path":"/p.jpg","iso_639_1":null,"width":1000,"height":1500}]}""" : null;
        };
        var imdb = new ArtworkQuery { Kind = BaseItemKind.Movie, Name = "X", ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt0133093" }, WantedTypes = [ImageType.Primary], Languages = [] };
        Check.Equal(1, (await new TmdbSource().FindAsync(imdb, http, cfg, CancellationToken.None)).Count, "tmdb flow: imdb id resolved");
        Check.True(http.Requests.All(r => !r.AbsolutePath.Contains("search", StringComparison.Ordinal)), "tmdb flow: imdb id avoids text search");

        // A v4 token goes in a header and never in the address.
        http.Requests.Clear();
        cfg.Sources.Tmdb.ApiKey = "eyJhbGciOi.v4token";
        FakeHeaderHttp headerHttp = new();
        await new TmdbSource().FindAsync(withId, headerHttp, cfg, CancellationToken.None);
        Check.True(headerHttp.Headers?.ContainsKey("Authorization") == true && !headerHttp.LastUri!.Query.Contains("v4token", StringComparison.Ordinal), "tmdb flow: v4 token in a header only");
        cfg.Sources.Tmdb.ApiKey = "tmdbkey";

        // Name search takes only an exact title: a loose hit would put the wrong picture on the item.
        http.Requests.Clear();
        http.Answer = u =>
        {
            if (u.AbsolutePath.Contains("search/movie", StringComparison.Ordinal))
            {
                return """{"results":[{"id":1,"title":"Something Else"},{"id":2,"title":"the matrix"}]}""";
            }

            return u.AbsolutePath.Contains("/movie/2/images", StringComparison.Ordinal) ? """{"posters":[{"file_path":"/m.jpg","iso_639_1":null,"width":1000,"height":1500}]}""" : null;
        };
        var byName = new ArtworkQuery { Kind = BaseItemKind.Movie, Name = "The Matrix", WantedTypes = [ImageType.Primary], Languages = [] };
        var named = await new TmdbSource().FindAsync(byName, http, cfg, CancellationToken.None);
        Check.True(named.Count == 1 && named[0].RemoteId == "/m.jpg", "tmdb flow: exact title match chosen");

        // Fanart needs ids: no TVDB id, no request.
        http.Requests.Clear();
        var noId = new ArtworkQuery { Kind = BaseItemKind.Series, Name = "Show", WantedTypes = [ImageType.Primary], Languages = [] };
        Check.Equal(0, (await new FanartSource().FindAsync(noId, http, cfg, CancellationToken.None)).Count, "fanart flow: nothing without an id");
        Check.Equal(0, http.Requests.Count, "fanart flow: no request without an id");

        // Cover Art Archive: no MusicBrainz id, no request.
        var album = new ArtworkQuery { Kind = BaseItemKind.MusicAlbum, Name = "Album", WantedTypes = [ImageType.Primary], Languages = [] };
        Check.Equal(0, (await new CoverArtArchiveSource().FindAsync(album, http, cfg, CancellationToken.None)).Count, "caa flow: nothing without an id");
        Check.Equal(0, http.Requests.Count, "caa flow: no request without an id");

        // Open Library answers for books only.
        var movie = new ArtworkQuery { Kind = BaseItemKind.Movie, Name = "Film", WantedTypes = [ImageType.Primary], Languages = [] };
        Check.Equal(0, (await new OpenLibrarySource().FindAsync(movie, http, cfg, CancellationToken.None)).Count, "openlibrary flow: not for films");
    }

    private sealed class FakeHeaderHttp : IArtworkHttp
    {
        public IReadOnlyDictionary<string, string>? Headers { get; private set; }

        public Uri? LastUri { get; private set; }

        public Task<JsonDocument?> GetJsonAsync(string sourceId, Uri uri, int requestsPerMinute, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            Headers = headers;
            LastUri = uri;
            return Task.FromResult<JsonDocument?>(null);
        }
    }
}
