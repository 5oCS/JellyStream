using System.Text.Json;

namespace Jellystream;

// A self-hosted Dispatcharr's VOD catalogue, read over its REST API and played through Dispatcharr's
// own proxy route. Dispatcharr has usually already matched TMDB, so a title is found by id instead of
// by guessing at a name — the hard part of the generic IPTV case, which this source does not have.
//
// Nothing here is cached: the playback address carries the item's UUID, and a Dispatcharr refresh
// regenerates those, so a lookup that is seconds old is worth more than one that is minutes old.
public sealed class DispatcharrSource(SafeHttp http)
{
    public const string Id = "dispatcharr";
    private const string Proxy = "/proxy/vod/";

    private static Configuration? Config => Plugin.Instance?.Configuration;
    private static string Address => (Config?.DispatcharrUrl ?? "").Trim();
    private static string ApiKey => (Config?.DispatcharrApiKey ?? "").Trim();
    private static string Base => Address.TrimEnd('/');
    private static bool HideAdult => Config?.HideAdultContent ?? true;

    public static bool IsConfigured => Address.Length > 0 && ApiKey.Length > 0 && SafeHttp.Origin(Address) is not null;

    // The settings and the state lines name the configured host rather than a label of our own, so two
    // servers on one account can be told apart. The key is never part of it.
    public static string Label => SafeHttp.Origin(Address) is { } origin
        ? origin.Host + (origin.IsDefaultPort ? "" : ":" + origin.Port) : "Dispatcharr";

    // One catalogue row, reduced to what a lookup or a playback address needs.
    private sealed record Entry(string Kind, int Id, string Uuid, string Name, int? Year, int? TmdbId, bool IsAdult);

    // Built from the configured base so a server mounted under a path prefix still works, and so a
    // test can check the shape without a configured server behind it.
    public static Uri Stream(string baseAddress, string kind, string uuid) =>
        new(baseAddress.TrimEnd('/') + Proxy + kind + "/" + uuid);

    public async Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(Film film, int? season, int? episode, CancellationToken ct)
    {
        if (!IsConfigured) throw new ProviderException("No Dispatcharr address and API key are set in the Jellystream settings.");
        if (film.Kind == FilmKind.Movie)
        {
            var movie = await FindAsync("movie", film, ct) ?? throw new ProviderException("Dispatcharr does not hold this film.");
            return [Playable("movie", movie.Uuid)];
        }
        if (season is not { } wanted || episode is not { } number)
            throw new ProviderException("Dispatcharr needs a season and an episode number.");
        var series = await FindAsync("series", film, ct) ?? throw new ProviderException("Dispatcharr does not hold this show.");
        var entry = await EpisodeAsync(series.Id, wanted, number, ct)
            ?? throw new ProviderException($"Dispatcharr does not hold season {wanted} episode {number}.");
        return [Playable("episode", entry.Uuid)];
    }

    // The address is handed to the relay, which probes the file itself: the container and the height
    // come from the bytes, so the label claims no height until the probe has read one.
    private static ResolvedVideo Playable(string kind, string uuid) =>
        new(Stream(Base, kind, uuid), "VOD", new Dictionary<string, string>(), false);

    // Dispatcharr offers no tmdb_id filter, so the title is searched and the id matched here. An id
    // match wins; a title the provider spelled differently still plays when the year agrees; anything
    // else falls through to the next source rather than playing the wrong film.
    private async Task<Entry?> FindAsync(string kind, Film film, CancellationToken ct)
    {
        var found = await ListAsync($"/api/vod/{kind}/?search={Uri.EscapeDataString(film.Title)}&page_size=100", kind, ct);
        return found.FirstOrDefault(entry => entry.TmdbId is { } tmdb && tmdb == film.Id)
            ?? found.FirstOrDefault(entry => SameTitle(entry, film));
    }

    private static bool SameTitle(Entry entry, Film film) => FlixSource.Slug(entry.Name) == FlixSource.Slug(film.Title)
        && (film.Year is not { } year || entry.Year is null || Math.Abs(entry.Year.Value - year) <= 1);

    private async Task<Entry?> EpisodeAsync(int seriesId, int season, int episode, CancellationToken ct)
        => (await ListAsync($"/api/vod/episodes/?series={seriesId}&season_number={season}&episode_number={episode}&page_size=1", "episode", ct)).FirstOrDefault();

    // The settings' test: the catalogue answers, and one of its items really plays. A library with
    // nothing in it is reported as itself rather than as a broken connection.
    public async Task<VideoCheck> TestAsync(CancellationToken ct)
    {
        if (!IsConfigured) return new("no Dispatcharr address and API key are set", false);
        try
        {
            var entry = await FirstAsync(ct);
            if (entry is null) return new("the catalogue holds nothing playable", false);
            using var response = await http.OpenAsync(Stream(Base, entry.Kind, entry.Uuid), true, ct, range: "bytes=0-1", open: true);
            if (!response.IsSuccessStatusCode) return new($"the stream answered HTTP {(int)response.StatusCode}", (int)response.StatusCode >= 500);
            return VideoCheck.Works;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new(ex is ProviderException ? ex.Message : CatalogBreaker.Cause(ex), CatalogBreaker.IsOutage(ex, ct));
        }
    }

    // A film when the library has one, an episode of the first show when it has only series: both
    // prove the whole way to a playable file, which is what a passing test has to mean.
    private async Task<Entry?> FirstAsync(CancellationToken ct)
    {
        var movies = await ListAsync("/api/vod/movies/?page_size=1", "movie", ct);
        if (movies.Count > 0) return movies[0];
        var series = await ListAsync("/api/vod/series/?page_size=1", "series", ct);
        if (series.Count == 0) return null;
        return (await ListAsync($"/api/vod/episodes/?series={series[0].Id}&page_size=5", "episode", ct)).FirstOrDefault();
    }

    private async Task<IReadOnlyList<Entry>> ListAsync(string path, string kind, CancellationToken ct)
    {
        using var document = await GetAsync(path, ct);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        var found = new List<Entry>();
        foreach (var item in results.EnumerateArray())
        {
            var uuid = Text(item, "uuid");
            var name = Text(item, "name");
            // The proxy route addresses a UUID, so a row without one is not playable at all.
            if (uuid is null || !Guid.TryParse(uuid, out _) || string.IsNullOrWhiteSpace(name)) continue;
            var entry = new Entry(kind, Number(item, "id") ?? 0, uuid, name.Trim(), Number(item, "year"),
                Number(item, "tmdb_id"), Flag(item, "is_adult"));
            if (HideAdult && entry.IsAdult) continue;
            found.Add(entry);
        }
        return found;
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { ["X-API-Key"] = ApiKey };
        var text = await http.TextAsync(new Uri(Base + path), ct, headers: headers, open: true);
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { throw new ProviderException("Dispatcharr returned something that is not a catalogue."); }
    }

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Providers hand these over as numbers or as strings; either is a number to us.
    private static int? Number(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : null;
    }

    private static bool Flag(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
