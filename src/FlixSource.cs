using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Jellystream;

public sealed record FlixServer(string Name, Uri Link);

// Movies and TV from the two FlixHQ layouts, ported from my flixdl downloader. Each site lists
// third-party players; the player page is read as text, packed scripts are unpacked as a pure
// string transform, and nothing from the page is ever executed.
public sealed class FlixSource(SafeHttp http) : IDisposable
{
    public static readonly Uri Watch = new("https://flixhqz.net/");
    public static readonly Uri Classic = new("https://flixhq.ws/");
    public static readonly string[] ServerOrder = ["vidmoly", "doodstream", "vidsrc"];
    private readonly MemoryCache pages = new(new MemoryCacheOptions { SizeLimit = 2000 });
    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
    private static readonly Regex AnchorTag = Rx(@"<a\b([^>]*)>(.*?)</a>");
    private static readonly Regex M3u8 = Rx(@"https?://[^\s""'<>\\]+?\.m3u8(?:\?[^\s""'<>\\]*)?");
    private static readonly Regex Mp4 = Rx(@"[""']?file[""']?\s*:\s*[""'](https?://[^""']+?\.mp4(?:\?[^""']*)?)[""']");
    private static readonly Regex TrackBlock = Rx(@"\{[^{}]*?\.(?:vtt|srt)[^{}]*?\}");
    private static readonly Regex TrackFile = Rx(@"[""']?file[""']?\s*:\s*[""'](https?://[^""']+?\.(?:vtt|srt)(?:\?[^""']*)?)[""']");
    private static readonly Regex TrackLabel = Rx(@"[""']?label[""']?\s*:\s*[""']([^""']+)[""']");
    // Player hand-off. A whole iframe tag is taken first, then every link attribute inside it, because
    // a tag may carry a useless src="about:blank" alongside the real data-src/data-api/data-url that a
    // single pattern over the tag would stop at. Values may be protocol-relative (//host/path), so no
    // scheme is required and Http() decides whether the value resolves to a fetchable page.
    private static readonly Regex Frame = Rx(@"<iframe\b[^>]*>");
    private static readonly Regex FrameLink = Rx(@"(?:\bsrc|data-src|data-api|data-url)\s*=\s*[""']([^""']+)[""']");
    // Some sites answer a hop with a bare JSON body naming the next page instead of an iframe.
    private static readonly Regex JsonHop = Rx(@"""(?:src|url|file)""\s*:\s*""([^""]+)""");
    // A player that keeps the stream behind a small JSON API writes a config object into the page
    // and puts the API beside it, addressed by the page's base.
    private static readonly Regex PlayerConfigRx = Rx(@"\bQ\s*=\s*\{");
    private static readonly Regex BaseHref = Rx(@"<base\s+href\s*=\s*[""']([^""']+)[""']");
    private static readonly Regex Packed = Rx(@"}\s*\(\s*'(.*?)'\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*'(.*?)'\.split\('\|'\)");

    // Player chains now run four or five pages deep, so the walk follows further than the two hops a
    // static FlixHQ page needed; the fan-out narrows at depth to keep a deeply nested page cheap.
    private const int MaxDepth = 5;

    // A stream address found on a page, and whether it is a playlist.
    private sealed record Candidate(Uri Url, bool Hls);

    public async Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(Film film, int? season, int? episode, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var site in new[] { Watch, Classic })
        {
            try
            {
                var page = await FindAsync(site, film, ct);
                if (page is null) { errors.Add(site.Host + ": not listed"); continue; }
                var (servers, referer) = film.Kind == FilmKind.Movie ? (await MovieServersAsync(site, page, ct), page)
                    : await EpisodeServersAsync(site, page, season!.Value, episode!.Value, ct);
                foreach (var server in servers.OrderBy(s => Rank(s.Name)))
                {
                    try
                    {
                        if ((await PlayerAsync(server.Link, referer, 0, ct)).Video is not { } video) continue;
                        var known = video.Subtitles!.Select(s => s.Url).ToHashSet();
                        var extra = (await SubtitleIndexAsync(server.Link, referer, ct)).Where(s => !known.Contains(s.Url));
                        return [video with { Subtitles = Languages.Arrange(video.Subtitles!.Concat(extra)) }];
                    }
                    catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        errors.Add(server.Name);
                    }
                }
                errors.Add(site.Host + ": no working player");
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                errors.Add(site.Host + ": unavailable");
            }
        }
        throw new ProviderException("No source could play this title right now (" + string.Join(", ", errors.Distinct().Take(6)) + "). Please try again later.");
    }

    public static int Rank(string name) => Array.FindIndex(ServerOrder, p => name.Contains(p, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : ServerOrder.Length;


    public static string Slug(string title) => Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(title).ToLowerInvariant().Replace("&", "and"),
        @"['’]", "", RegexOptions.None, TimeSpan.FromSeconds(1)), @"[^a-z0-9]+", "-", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim('-');

    // The settings page's test searches again rather than trusting a page found earlier.
    public void Forget(Film film)
    {
        foreach (var site in new[] { Watch, Classic }) pages.Remove(PageKey(site, film));
    }

    private static string PageKey(Uri site, Film film) => $"{site.Host}/{film.Kind}/{film.Id}";

    private async Task<Uri?> FindAsync(Uri site, Film film, CancellationToken ct)
    {
        var key = PageKey(site, film);
        if (pages.TryGetValue<Uri>(key, out var known)) return known;
        var html = await http.TextAsync(new Uri(site, "search?keyword=" + Uri.EscapeDataString(film.Title)), ct);
        var found = site == Watch ? MatchWatch(html, film) : MatchClassic(html, film);
        if (found is not null) pages.Set(key, found, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12) });
        return found;
    }

    // flixhqz.net slugs carry the title and year: /watch-movie/<title>-<year>-watch-online/.
    public static Uri? MatchWatch(string html, Film film)
    {
        var kind = film.Kind == FilmKind.Movie ? "watch-movie" : "watch-series";
        var slugs = Rx($@"href=""https://flixhqz\.net/{kind}/([a-z0-9-]+)/""").Matches(html).Select(m => m.Groups[1].Value).Distinct().ToArray();
        var title = Slug(film.Title);
        var pick = (film.Year is { } year ? slugs.FirstOrDefault(s => s == $"{title}-{year}-watch-online") : null)
            ?? slugs.FirstOrDefault(s => film.Year is { } y && (s == $"{title}-{y - 1}-watch-online" || s == $"{title}-{y + 1}-watch-online"))
            ?? (film.Year is null ? slugs.FirstOrDefault(s => s == $"{title}-watch-online") : null);
        return pick is null ? null : new Uri(Watch, $"{kind}/{pick}/");
    }

    // flixhq.ws search cards: a /movie/ or /series/ link, its title, and a year for movies.
    public static Uri? MatchClassic(string html, Film film)
    {
        var kind = film.Kind == FilmKind.Movie ? "movie" : "series";
        var title = Slug(film.Title);
        var cards = Rx(@"<div class=""flw-item"">(.*?)<div class=""clearfix""").Matches(html).Select(m => m.Groups[1].Value);
        foreach (var card in cards)
        {
            var link = Rx($@"href=""https://flixhq\.ws/{kind}/([a-z0-9-]+)/""[^>]*title=""([^""]*)""").Match(card);
            if (!link.Success || Slug(link.Groups[2].Value) != title) continue;
            var year = Rx(@"<span class=""fdi-item"">(\d{4})</span>").Match(card);
            if (film.Kind == FilmKind.Movie && film.Year is { } wanted && year.Success
                && Math.Abs(int.Parse(year.Groups[1].Value, CultureInfo.InvariantCulture) - wanted) > 1) continue;
            return new Uri(Classic, $"{kind}/{link.Groups[1].Value}/");
        }
        return null;
    }


    public static IEnumerable<(Dictionary<string, string> Attributes, string Text)> Anchors(string html, string cls) => AnchorTag.Matches(html)
        .Select(m => (Attributes: Attributes(m.Groups[1].Value), Text: Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(m.Groups[2].Value, "<[^>]+>", " ",
            RegexOptions.None, TimeSpan.FromSeconds(1))), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim()))
        .Where(a => a.Attributes.TryGetValue("class", out var c) && c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(cls));

    private static Dictionary<string, string> Attributes(string tag) => Rx(@"([a-z-]+)\s*=\s*(?:""([^""]*)""|'([^']*)')").Matches(tag)
        .GroupBy(m => m.Groups[1].Value.ToLowerInvariant()).ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups[2].Success && g.First().Groups[2].Length > 0
            ? g.First().Groups[2].Value : g.First().Groups[3].Value));

    private static string? JsString(string html, string name) => Rx($@"\b{name}\s*=\s*['""]([^'""]+)['""]").Match(html) is { Success: true } m ? m.Groups[1].Value : null;

    private static Uri? Http(Uri page, string? link) => Uri.TryCreate(page, link?.Trim(), out var url) && url.Scheme is "https" or "http" ? url : null;

    // flixhq.ws: the page names a listing URL, whose links carry each player in data-id.
    private async Task<IReadOnlyList<FlixServer>> ListedServersAsync(Uri page, Uri referer, CancellationToken ct)
    {
        var html = await http.TextAsync(page, ct, headers: Referer(referer));
        if (JsString(html, "pl_url") is not { } listing || Http(page, listing) is not { } listingUrl) return [];
        return ParseListing(await http.TextAsync(listingUrl, ct, headers: Referer(page), open: listingUrl.Host != page.Host));
    }
    public static IReadOnlyList<FlixServer> ParseListing(string html) => Anchors(html, "sv-item")
        .Select(a => (Name: a.Attributes.GetValueOrDefault("data-srv") is { Length: > 0 } n ? n : a.Text, Link: a.Attributes.GetValueOrDefault("data-id")))
        .Where(s => s.Link is not null && Uri.TryCreate(s.Link.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
        .Select(s => new FlixServer(s.Name.Length > 0 ? s.Name : "unknown", new Uri(s.Link!.Trim()))).ToArray();

    // flixhqz.net: the player element holds a token that its ajax endpoint exchanges for players.
    private async Task<IReadOnlyList<FlixServer>> TokenServersAsync(Uri page, Uri referer, string field, CancellationToken ct)
    {
        var html = await http.TextAsync(page, ct, headers: Referer(referer));
        var shell = Rx(@"<[^>]+id=""series-player""[^>]*>").Match(html);
        var token = Rx(@"data-token=""([^""]+)""").Match(shell.Success ? shell.Value : html);
        if (!token.Success) return [];
        var body = await http.TextAsync(new Uri(page, "/ajax/ajax.php"), ct, headers: Referer(page),
            form: new Dictionary<string, string> { [field] = WebUtility.HtmlDecode(token.Groups[1].Value) });
        return ParsePlayers(body);
    }
    public static IReadOnlyList<FlixServer> ParsePlayers(string json)
    {
        using var document = JsonDocument.Parse(json.TrimStart('﻿'));
        var items = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
        return items.Where(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.String
                && Uri.TryCreate(l.GetString(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
            .Select(i => new FlixServer(i.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "unknown",
                new Uri(i.GetProperty("link").GetString()!))).ToArray();
    }

    private Task<IReadOnlyList<FlixServer>> MovieServersAsync(Uri site, Uri page, CancellationToken ct) =>
        site == Watch ? TokenServersAsync(page, site, "players", ct) : ListedServersAsync(page, site, ct);

    private async Task<(IReadOnlyList<FlixServer>, Uri)> EpisodeServersAsync(Uri site, Uri series, int season, int episode, CancellationToken ct)
    {
        var html = await http.TextAsync(series, ct, headers: Referer(site));
        if (site == Watch)
        {
            var link = Anchors(html, "eps-item").Select(a => Http(series, a.Attributes.GetValueOrDefault("href")))
                .FirstOrDefault(u => u is not null && u.AbsolutePath.TrimEnd('/').EndsWith($"/s{season}-e{episode}", StringComparison.Ordinal));
            if (link is null) throw new ProviderException($"Season {season} episode {episode} is not listed.");
            return (await TokenServersAsync(link, series, "players_show", ct), link);
        }
        var seasons = Anchors(html, "ss-item").Where(a => a.Attributes.ContainsKey("data-ss") && a.Attributes.ContainsKey("data-id"))
            .ToDictionary(a => a.Attributes["data-ss"], a => a.Attributes["data-id"]);
        var seasonId = seasons.GetValueOrDefault(season.ToString(CultureInfo.InvariantCulture));
        if (seasonId is null && seasons.Count == 0 && season == 1 && JsString(html, "current_url") is { } current
            && Http(series, current) is { } currentUrl)
            seasonId = System.Web.HttpUtility.ParseQueryString(currentUrl.Query)["episode"];
        if (string.IsNullOrEmpty(seasonId)) throw new ProviderException($"Season {season} is not listed.");
        var listing = await http.TextAsync(new Uri(site, "ajax/ajax.php?episode=" + Uri.EscapeDataString(seasonId)), ct, headers: Referer(series));
        var page = Anchors(listing, "eps-item").Select(a => Http(site, a.Attributes.GetValueOrDefault("href")))
            .FirstOrDefault(u => u is not null && Regex.IsMatch(u.AbsolutePath, $@"/{season}-{episode}/?\z", RegexOptions.None, TimeSpan.FromSeconds(1)));
        if (page is null) throw new ProviderException($"Season {season} episode {episode} is not listed.");
        return (await ListedServersAsync(page, series, ct), page);
    }

    private static Dictionary<string, string> Referer(Uri page) => new() { ["Referer"] = page.AbsoluteUri };


    // The walk, with the reasons it found nothing: the page it reached and what that page said.
    public async Task<(ResolvedVideo? Video, IReadOnlyList<string> Problems)> PlayerAsync(Uri embed, Uri referer, int depth, CancellationToken ct)
    {
        var problems = new List<string>();
        var video = await WalkAsync(embed, referer, depth, [], problems, ct);
        return (video, problems);
    }

    private async Task<ResolvedVideo?> WalkAsync(Uri embed, Uri referer, int depth, HashSet<Uri> seen, List<string> problems, CancellationToken ct)
    {
        if (!seen.Add(embed)) return null;
        var (final, html) = await http.PageAsync(embed, ct, headers: Referer(referer), open: true);
        var candidate = Media(html) ?? await PlayerApiAsync(final, html, ct, problems);
        if (candidate is { } found)
        {
            var origin = final.GetLeftPart(UriPartial.Authority);
            var headers = new Dictionary<string, string> { ["Referer"] = origin + "/", ["Origin"] = origin };
            if (found.Hls && !(await http.TextAsync(found.Url, ct, headers: headers, open: true)).TrimStart('﻿', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal))
                throw new ProviderException("The player did not return an HLS playlist.");
            return new ResolvedVideo(found.Url, found.Hls ? "HLS" : "MP4", headers, false, found.Hls, FindSubtitles(html));
        }
        if (depth >= MaxDepth) { problems.Add($"{final} reached the end of the chain"); return null; }
        foreach (var frame in Hops(final, html).Distinct().Take(depth < 2 ? 4 : 2))
        {
            try { if (await WalkAsync(frame, final, depth + 1, seen, problems, ct) is { } video) return video; }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException && !ct.IsCancellationRequested) { }
        }
        problems.Add($"{final} named no playable video");
        return null;
    }

    // A stream address in the page itself.
    private static Candidate? Media(string html)
    {
        if (FindMedia(html) is not { } url) return null;
        return new Candidate(url, url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));
    }

    // Some players do not put the stream in the page: they carry a JSON config naming a signed token
    // and the servers they offer, and a small JSON API beside them trades one server reference for the
    // playable address. Servers that are not ready answer 502 and some answer with a list of adverts
    // dressed as a playlist, so a server is only used when its playlist really holds the stream.
    private async Task<Candidate?> PlayerApiAsync(Uri page, string html, CancellationToken ct, List<string> problems)
    {
        if (PlayerConfig(html) is not { } config) return null;
        var href = BaseHref.Match(html);
        var root = href.Success ? new Uri(page, href.Groups[1].Value) : page;
        var api = new Uri(root, "api.php");
        foreach (var reference in config.Servers)
        {
            var query = "?a=play&ref=" + Uri.EscapeDataString(reference) + "&t=" + Uri.EscapeDataString(config.Token);
            string body;
            try { body = await http.TextAsync(new Uri(api, query), ct, headers: Referer(page), open: true); }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                problems.Add($"{api} did not hand over a stream ({ex.Message})");
                continue;
            }
            if (PlayAddress(body) is not { } address || Http(page, address) is not { } stream) continue;
            if (await PlaylistAsync(stream, page, ct) is { } playlist) return new Candidate(playlist, true);
            problems.Add($"{stream} offered no playlist");
        }
        problems.Add($"{page} offered {config.Servers.Count} servers, none carried a stream");
        return null;
    }

    // The address of the playlist itself: the one the player names, or, when it names a master, the
    // first of its variants that really is a playlist. A master may list an advert server first.
    private async Task<Uri?> PlaylistAsync(Uri stream, Uri page, CancellationToken ct)
    {
        string text;
        try { text = await http.TextAsync(stream, ct, headers: Referer(page), open: true); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return null; }
        if (!text.StartsWith("#EXTM3U", StringComparison.Ordinal)) return null;
        if (!Hls.IsMaster(text)) return stream;
        foreach (var variant in Variants(text, stream).Take(4))
        {
            try
            {
                var body = await http.TextAsync(variant, ct, headers: Referer(page), open: true);
                if (!body.StartsWith("#EXTM3U", StringComparison.Ordinal)) continue;
                // A playlist of adverts dressed as a playlist is refused: its first part is an image,
                // not video, and the server behind it carries no stream.
                if (!await CarriesVideoAsync(variant, body, page, ct)) continue;
                return variant;
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
        }
        return null;
    }

    // Whether the first part of a media playlist is really video rather than an advert.
    private async Task<bool> CarriesVideoAsync(Uri playlist, string text, Uri page, CancellationToken ct)
    {
        var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        if (first is null) return false;
        if (!Uri.TryCreate(playlist, first, out var part)) return false;
        try
        {
            using var response = await http.OpenAsync(part, true, ct, range: "bytes=0-15", headers: Referer(page), open: true);
            if (!response.IsSuccessStatusCode) return false;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 8) return false;
            // MPEG-TS starts with a sync byte; a fragmented MP4 starts with a box named ftyp, styp or moof.
            return bytes[0] == 0x47 || (bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
                || (bytes[4] == 0x73 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
                || (bytes[4] == 0x6D && bytes[5] == 0x6F && bytes[6] == 0x6F && bytes[7] == 0x66);
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    // The variants a master playlist lists, each one the address of a playlist of its own.
    private static IEnumerable<Uri> Variants(string master, Uri playlist)
    {
        var lines = master.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;
            for (var j = i + 1; j < lines.Length; j++)
            {
                var line = lines[j].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith('#')) break;
                if (Uri.TryCreate(playlist, line, out var variant)) yield return variant;
                break;
            }
        }
    }

    // The API answers with the address of the stream, relative to the site.
    public static string? PlayAddress(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // The player config is a JSON object written into the page; take it by matching braces so that
    // nested objects and strings inside it do not confuse the reader.
    private static string? JsonObject(string html, string name)
    {
        var match = Rx(@"\b" + name + @"\s*=\s*\{").Match(html);
        if (!match.Success) return null;
        var start = html.IndexOf('{', match.Index);
        var depth = 0; var quoted = false; var escaped = false;
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') { quoted = true; continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return html[start..(i + 1)];
        }
        return null;
    }

    // The signed token and the server references the page offers.
    public static (string Token, IReadOnlyList<string> Servers)? PlayerConfig(string html)
    {
        if (JsonObject(html, "Q") is not { } json) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("t", out var token) || token.GetString() is not { Length: > 0 } signed) return null;
            if (!root.TryGetProperty("ssr", out var ssr) || !ssr.TryGetProperty("servers", out var servers)) return null;
            var references = new List<string>();
            foreach (var server in servers.EnumerateArray())
                if (server.TryGetProperty("ref", out var value) && value.GetString() is { Length: > 0 } reference) references.Add(reference);
            return references.Count == 0 ? null : (signed, references);
        }
        catch (JsonException) { return null; }
    }

    // The next page a player points at: whatever the iframes link (however they spell the attribute)
    // plus, for a bare JSON body, the src/url/file it names. HTML entities are decoded first because
    // an attribute keeps its query separators escaped (&amp;).
    public static IEnumerable<Uri> Hops(Uri page, string html)
    {
        foreach (Match frame in Frame.Matches(html))
            foreach (Match link in FrameLink.Matches(frame.Value))
                if (Http(page, WebUtility.HtmlDecode(link.Groups[1].Value)) is { } hop) yield return hop;
        if (!IsJson(html)) yield break;
        foreach (Match m in JsonHop.Matches(html))
            if (Http(page, WebUtility.HtmlDecode(m.Groups[1].Value)) is { } hop) yield return hop;
    }
    private static bool IsJson(string html)
    {
        foreach (var c in html)
            if (!char.IsWhiteSpace(c)) return c is '{' or '[';
        return false;
    }

    public static Uri? FindMedia(string html)
    {
        var blobs = Unpack(html).Prepend(html).Select(b => b.Replace("\\/", "/", StringComparison.Ordinal)).ToArray();
        foreach (var blob in blobs)
            if (M3u8.Match(blob) is { Success: true } m && Valid(m.Value) is { } url) return url;
        foreach (var blob in blobs)
            if (Mp4.Match(blob) is { Success: true } m && Valid(m.Groups[1].Value) is { } url) return url;
        return null;
    }
    private static Uri? Valid(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var url)) return null;
        try { SafeHttp.ValidateOpen(url); return url; }
        catch (ProviderException) { return null; }
    }

    public static IReadOnlyList<Subtitle> FindSubtitles(string html)
    {
        var found = new List<Subtitle>();
        foreach (var blob in Unpack(html).Prepend(html))
            foreach (Match block in TrackBlock.Matches(blob.Replace("\\/", "/", StringComparison.Ordinal)))
            {
                var file = TrackFile.Match(block.Value);
                var label = TrackLabel.Match(block.Value);
                if (!file.Success || !label.Success || label.Groups[1].Value.Trim().Length == 0
                    || label.Groups[1].Value.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)) continue;
                if (Valid(file.Groups[1].Value) is { } url) found.Add(new(label.Groups[1].Value.Trim(), Languages.Code("", label.Groups[1].Value), url));
            }
        return found.DistinctBy(s => s.Url).ToArray();
    }

    // Some players link a JSON subtitle index from their own URL (sub.info=...).
    public async Task<IReadOnlyList<Subtitle>> SubtitleIndexAsync(Uri player, Uri referer, CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(player.Query);
        foreach (var name in new[] { "sub.info", "subget", "sub_info" })
        {
            if (Valid(query[name] ?? "") is not { } index) continue;
            try { return ParseSubtitleIndex(await http.TextAsync(index, ct, headers: Referer(referer), open: true)); }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or JsonException && !ct.IsCancellationRequested) { return []; }
        }
        return [];
    }
    public static IReadOnlyList<Subtitle> ParseSubtitleIndex(string json)
    {
        using var document = JsonDocument.Parse(json.TrimStart('﻿'));
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        static string Field(JsonElement e, string a, string b) => e.TryGetProperty(a, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString()!
            : e.TryGetProperty(b, out var y) && y.ValueKind == JsonValueKind.String ? y.GetString()! : "";
        return document.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e => (Url: Valid(Field(e, "file", "src")), Label: Field(e, "label", "lang").Trim()))
            .Where(s => s.Url is not null && s.Label.Length > 0 && !s.Label.Contains("thumbnail", StringComparison.OrdinalIgnoreCase))
            .Select(s => new Subtitle(s.Label, Languages.Code("", s.Label), s.Url!)).ToArray();
    }

    // Dean Edwards' packer: a word list plus a payload whose base-N tokens index into it.
    // Substituting the words back is a string operation; the script itself never runs.
    public static IReadOnlyList<string> Unpack(string html)
    {
        var output = new List<string>();
        foreach (Match m in Packed.Matches(html))
        {
            if (!int.TryParse(m.Groups[2].Value, out var radix) || !int.TryParse(m.Groups[3].Value, out var count) || radix is < 2 or > 62) continue;
            var words = m.Groups[4].Value.Split('|');
            var payload = m.Groups[1].Value.Replace("\\'", "'", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            // A hostile page could repeat a huge word millions of times; stop well before that.
            var size = 0L;
            output.Add(Regex.Replace(payload, @"\b\w+\b", w =>
            {
                var word = Unbase(w.Value, radix) is { } i && i < words.Length && i < count && words[i].Length > 0 ? words[i] : w.Value;
                if ((size += word.Length) > 8_000_000) throw new ProviderException("The player page is too large.");
                return word;
            }, RegexOptions.None, TimeSpan.FromSeconds(2)));
        }
        return output;
    }
    public static long? Unbase(string word, int radix)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        long value = 0;
        foreach (var ch in word)
        {
            var index = radix <= 36 ? alphabet.IndexOf(char.ToLowerInvariant(ch)) : alphabet.IndexOf(ch);
            if (index < 0 || index >= radix || value > int.MaxValue) return null;
            value = value * radix + index;
        }
        return value;
    }
    public void Dispose() => pages.Dispose();
}
