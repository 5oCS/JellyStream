using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Jellystream;

// Shared audiobook shapes: LibriVox is the catalog, Internet Archive answers search and mirrors the shelf.
public sealed record AudioBook(string Id, string Source, string Title, IReadOnlyList<string> Authors, string? Image, string? Description, int? Year, int? Chapters, double? Seconds);
public sealed record AudioBookPage(IReadOnlyList<AudioBook> Items, bool HasNext);
public sealed record AudioChapter(string Id, string BookId, string Source, string Title, int Number, string Url, double? Seconds);

public sealed class LibriVoxCatalog(SafeHttp http) : IDisposable
{
    public const string SourceId = "lv";
    public const int PageSize = 50;
    private static readonly Uri Endpoint = new("https://librivox.org/api/feed/");
    private const string Fields = "id,title,authors,description,copyright_year,num_sections,totaltimesecs,coverart_jpg,coverart_thumbnail";

    // The feed answers HTTP 500 for names outside its fixed taxonomy, so shelves carry the exact wording.
    public static readonly (string Slug, string Name)[] Genres =
    [
        ("poetry", "Poetry"),
        ("general-fiction", "General Fiction"),
        ("mystery", "Crime & Mystery Fiction"),
        ("science-fiction", "Science Fiction"),
        ("horror", "Horror & Supernatural Fiction"),
        ("romance", "Romance"),
        ("historical-fiction", "Historical Fiction"),
        ("adventure", "Action & Adventure Fiction"),
        ("short-stories", "Short Stories"),
        ("humor", "Humorous Fiction"),
        ("fantasy", "Fantasy Fiction"),
        ("history", "History"),
        ("biography", "Biography & Autobiography"),
        ("philosophy", "Philosophy"),
        ("children", "Children's Fiction"),
        ("westerns", "Westerns"),
        ("true-crime", "True Crime"),
        ("science", "Science"),
    ];
    private static readonly Dictionary<string, string> BySlug = Genres.ToDictionary(g => g.Slug, g => g.Name, StringComparer.Ordinal);

    public static string? GenreName(string slug) => BySlug.TryGetValue(slug, out var name) ? name : null;
    public static bool IsBookId(string id) => Regex.IsMatch(id, @"\A[0-9]{1,10}\z", RegexOptions.None, TimeSpan.FromSeconds(1));

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private readonly RateWindow budget = new(8, TimeSpan.FromSeconds(10));
    private readonly FreshCache<object> fresh = new(_ => true, 500);
    private int Revision => Plugin.Instance?.Configuration.CacheRevision ?? 0;

    public Task<AudioBookPage> BrowseAsync(string category, int page, CancellationToken ct)
    {
        if (page is < 1 or > 500) throw new ProviderException("Invalid catalog page.");
        var genre = category == "recent" ? null : GenreName(category) ?? throw new ProviderException("Invalid catalog page.");
        return CachedAsync<AudioBookPage>($"browse/{category}/{page}", TimeSpan.FromHours(6), async token =>
        {
            var query = "audiobooks?format=json&extended=1&coverart=1&fields=" + Fields
                + "&limit=" + PageSize + "&offset=" + ((page - 1) * PageSize) + "&sort_order=desc";
            if (genre is not null) query += "&genre=" + Uri.EscapeDataString(genre);
            using var document = await QueryAsync(query, token);
            var books = ReadBooks(document.RootElement);
            return new AudioBookPage(books, books.Count == PageSize);
        }, ct);
    }

    public Task<AudioBook> GetAsync(string id, CancellationToken ct)
    {
        if (!IsBookId(id)) throw new ProviderException("Invalid catalog page.");
        return CachedAsync($"book/{id}", TimeSpan.FromHours(24), async token =>
        {
            using var document = await QueryAsync("audiobooks?format=json&extended=1&coverart=1&fields=" + Fields + "&id=" + id + "&limit=1", token);
            return ReadBooks(document.RootElement).FirstOrDefault(b => b.Id == id)
                ?? throw new ProviderException("This audiobook is unavailable.");
        }, ct);
    }

    public Task<IReadOnlyList<AudioChapter>> ChaptersAsync(string bookId, CancellationToken ct)
    {
        if (!IsBookId(bookId)) throw new ProviderException("Invalid catalog page.");
        return CachedAsync<IReadOnlyList<AudioChapter>>($"chapters/{bookId}", TimeSpan.FromHours(24), async token =>
        {
            using var document = await QueryAsync("audiotracks?format=json&project_id=" + bookId, token);
            var root = document.RootElement;
            // The tracks feed nests sections at the root; older answers bury them under the first book.
            var sections = root.TryGetProperty("sections", out var direct) ? direct
                : root.TryGetProperty("books", out var books) && books.ValueKind == JsonValueKind.Array && books.GetArrayLength() > 0 && books[0].TryGetProperty("sections", out var nested)
                    ? nested : default;
            var chapters = new List<AudioChapter>();
            if (sections.ValueKind == JsonValueKind.Array)
            {
                var order = 0;
                foreach (var section in sections.EnumerateArray())
                {
                    order++;
                    if (ParseChapter(section, bookId, order) is { } chapter) chapters.Add(chapter);
                }
            }
            if (chapters.Count == 0) throw new ProviderException("This audiobook has no chapters.");
            return chapters.OrderBy(c => c.Number).ToArray();
        }, ct);
    }

    // The feed matches prefixes only, so both lookups use the caret form; author covers the other half of the shelf.
    public async Task<IReadOnlyList<AudioBook>> SearchAsync(string term, int limit, CancellationToken ct)
    {
        term = term.Trim();
        if (term.Length is < 2 or > 100) return [];
        limit = Math.Clamp(limit, 1, 50);
        return await CachedAsync<IReadOnlyList<AudioBook>>($"search/{term.ToLowerInvariant()}/{limit}", TimeSpan.FromMinutes(30), async token =>
        {
            var found = new Dictionary<string, AudioBook>(StringComparer.Ordinal);
            foreach (var filter in new[] { "title=^", "author=" })
            {
                using var document = await QueryAsync("audiobooks?format=json&extended=1&coverart=1&fields=" + Fields
                    + "&limit=" + limit + "&sort_order=desc&" + filter + Uri.EscapeDataString(term), token);
                foreach (var book in ReadBooks(document.RootElement)) found.TryAdd(book.Id, book);
                if (found.Count >= limit) break;
            }
            return found.Values.Take(limit).ToArray();
        }, ct);
    }

    private async Task<T> CachedAsync<T>(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> load, CancellationToken ct)
    {
        if (cache.TryGetValue<T>($"{Revision}/{key}", out var found)) return found!;
        try
        {
            // Seen before, it answers at once and refreshes behind, while LibriVox's budget has room.
            return (T)await fresh.GetAsync($"{Revision}/{key}", lifetime, async token =>
            {
                var value = await load(token);
                Store(key, value, lifetime);
                return (object)value!;
            }, ct, budget.Free(DateTime.UtcNow) > 1);
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or TaskCanceledException
            && cache.TryGetValue<T>("stale/" + key, out var stale)) { return stale!; }
    }

    private void Store<T>(string key, T value, TimeSpan lifetime)
    {
        cache.Set($"{Revision}/{key}", value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lifetime });
        cache.Set("stale/" + key, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
    }

    private async Task<JsonDocument> QueryAsync(string path, CancellationToken ct)
    {
        var wait = budget.Reserve(DateTime.UtcNow, TimeSpan.FromSeconds(5)) ?? throw new RateLimitedException("LibriVox is busy. Please try again shortly.", null);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        var text = await http.TextAsync(new Uri(Endpoint, path), ct, open: true);
        return JsonDocument.Parse(text);
    }

    private static IReadOnlyList<AudioBook> ReadBooks(JsonElement root)
    {
        // A filter that matches nothing answers {"error": "..."} instead of an empty list.
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("books", out var array) || array.ValueKind != JsonValueKind.Array) return [];
        var books = new List<AudioBook>();
        foreach (var book in array.EnumerateArray())
            if (Parse(book) is { } parsed) books.Add(parsed);
        return books;
    }

    private static AudioBook? Parse(JsonElement book)
    {
        var id = AudioJson.Text(book, "id");
        var title = AudioJson.Text(book, "title");
        if (id is null || string.IsNullOrWhiteSpace(title)) return null;
        var authors = new List<string>();
        if (book.TryGetProperty("authors", out var names) && names.ValueKind == JsonValueKind.Array)
            foreach (var author in names.EnumerateArray())
            {
                var name = AudioJson.Join(author, "first_name", "last_name");
                if (name.Length > 0) authors.Add(name);
            }
        return new AudioBook(id, SourceId, title.Trim(), authors,
            AudioJson.Link(book, "coverart_jpg") ?? AudioJson.Link(book, "coverart_thumbnail"),
            AudioJson.Clean(AudioJson.Text(book, "description")),
            AudioJson.Int(book, "copyright_year"),
            AudioJson.Int(book, "num_sections"),
            AudioJson.Double(book, "totaltimesecs"));
    }

    private static AudioChapter? ParseChapter(JsonElement section, string bookId, int fallback)
    {
        var id = AudioJson.Text(section, "id");
        var url = AudioJson.Text(section, "listen_url");
        if (id is null || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var location)) return null;
        try { SafeHttp.ValidateOpen(location); } catch (ProviderException) { return null; }
        var number = AudioJson.Int(section, "section_number") ?? fallback;
        var title = AudioJson.Text(section, "title");
        return new AudioChapter(id, bookId, SourceId,
            string.IsNullOrWhiteSpace(title) ? "Chapter " + number.ToString(CultureInfo.InvariantCulture) : title.Trim(),
            number, location.ToString(), AudioJson.Double(section, "playtime"));
    }

    public void Dispose()
    {
        fresh.Dispose();
        cache.Dispose();
    }
}

// Field readers shared by both audiobook feeds: JSON strings, HTML blobs, and optional links.
internal static class AudioJson
{
    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static string? Text(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    public static int? Int(JsonElement element, string property) =>
        int.TryParse(Text(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    public static double? Double(JsonElement element, string property) =>
        double.TryParse(Text(element, property), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    public static string? Link(JsonElement element, string property)
    {
        var raw = Text(element, property);
        if (raw is null || !Uri.TryCreate(raw, UriKind.Absolute, out var link)) return null;
        try { SafeHttp.ValidateOpen(link); return link.ToString(); } catch (ProviderException) { return null; }
    }

    public static string Join(JsonElement element, params string[] properties)
    {
        var parts = new List<string>();
        foreach (var property in properties)
            if (Text(element, property) is { Length: > 0 } value) parts.Add(value.Trim());
        return string.Join(' ', parts);
    }

    // Archive.org lists a creator as one string or several; either way the channel wants names.
    public static List<string> List(JsonElement element, string property)
    {
        var names = new List<string>();
        if (!element.TryGetProperty(property, out var value)) return names;
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } single) names.Add(single.Trim());
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var entry in value.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } name) names.Add(name.Trim());
        return names;
    }

    // Descriptions arrive as HTML with entity codes; folders want one readable line.
    public static string? Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Tags.Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return null;
        return text.Length > 800 ? text[..800].TrimEnd() + "…" : text;
    }
}
