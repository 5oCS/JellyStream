using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Jellystream;

// One downloadable file of a book: the format a reader wants, where the bytes are, and how big
// they are, so a "book" that is four gigabytes is not mistaken for one.
public sealed record EbookFile(string Format, Uri Url, long? Size);

// A book as a catalogue found it. ArchiveIds names the Internet Archive items that may carry the
// bytes — Open Library supplies the metadata and those identifiers but no files, while the Archive
// supplies both — and Files stays empty until someone asks, because a search row does not carry an
// item's file list.
public sealed record EbookRecord(string Id, string Source, string Title, IReadOnlyList<string> Authors,
    string? Image, string? Description, int? Year, IReadOnlyList<string> ArchiveIds, IReadOnlyList<EbookFile> Files,
    // Origin is the catalogued address this row came from. A direct file link is only ever fetched
    // from that address's own host, so a feed cannot name somewhere else for us to download from.
    string? Origin = null);

// Open Library answers by ISBN or by title and author, and is the metadata half of Path A: what the
// book is, its cover, and which Archive items hold it. The bytes come from InternetArchiveCatalog,
// which is how Anna's Archive splits the job too — catalogue upstream, files elsewhere.
public sealed class OpenLibraryCatalog(SafeHttp http) : IDisposable
{
    public const string SourceId = "ol";
    public static readonly Uri SearchEndpoint = new("https://openlibrary.org/search.json");
    // Only what a row shows. `ia` is the bridge to the Archive's own files.
    private const string Fields = "key,title,author_name,first_publish_year,cover_i,ia,description";
    // Open Library writes descriptions as Markdown, links and all; the row wants prose.
    private static readonly Regex MarkdownLink = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.None, TimeSpan.FromSeconds(1));

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private readonly RateWindow budget = new(10, TimeSpan.FromSeconds(10));
    private readonly FreshCache<object> fresh = new(_ => true, 500);
    private int Revision => Plugin.Instance?.Configuration.CacheRevision ?? 0;

    public static Uri? Cover(int? coverId) => coverId is > 0
        ? new Uri($"https://covers.openlibrary.org/b/id/{coverId}-L.jpg") : null;

    public Task<IReadOnlyList<EbookRecord>> SearchAsync(string term, int limit, CancellationToken ct) =>
        QueryAsync(Uri.EscapeDataString(term), limit, ct);

    // An ISBN is its own query: Open Library resolves the edition to the work, and it is the work
    // that carries the cover and the Archive identifiers.
    public Task<IReadOnlyList<EbookRecord>> IsbnAsync(string isbn, CancellationToken ct)
    {
        var digits = new string(isbn.Where(char.IsDigit).ToArray());
        if (digits.Length is not (10 or 13)) return Task.FromResult<IReadOnlyList<EbookRecord>>([]);
        return QueryAsync(Uri.EscapeDataString("isbn:" + digits), 1, ct);
    }

    private Task<IReadOnlyList<EbookRecord>> QueryAsync(string query, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 50);
        return CachedAsync($"search/{query}/{limit}", TimeSpan.FromHours(6), async token =>
        {
            var url = $"{SearchEndpoint}?q={query}&fields={Fields}&limit={limit}";
            using var document = await QueryDocumentAsync(new Uri(url), token);
            var root = document.RootElement;
            if (!root.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array) return [];
            var found = new List<EbookRecord>();
            foreach (var doc in docs.EnumerateArray())
                if (Parse(doc) is { } book) found.Add(book);
            return (IReadOnlyList<EbookRecord>)found;
        }, ct);
    }

    public static EbookRecord? Parse(JsonElement doc)
    {
        var id = AudioJson.Text(doc, "key");
        var title = First(doc, "title");
        if (id is null || string.IsNullOrWhiteSpace(title)) return null;
        return new EbookRecord(id, SourceId, title.Trim(), AudioJson.List(doc, "author_name"),
            Cover(AudioJson.Int(doc, "cover_i"))?.ToString(), Blurb(First(doc, "description")),
            AudioJson.Int(doc, "first_publish_year"), AudioJson.List(doc, "ia"), []);
    }

    // A title or a description arrives as one string or as a list; the row takes the first.
    private static string? First(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array when value.GetArrayLength() > 0 && value[0].ValueKind == JsonValueKind.String => value[0].GetString(),
            _ => null,
        };
    }

    private static string? Blurb(string? text) => text is null ? null : AudioJson.Clean(MarkdownLink.Replace(text, "$1"));

    private async Task<JsonDocument> QueryDocumentAsync(Uri uri, CancellationToken ct)
    {
        var wait = budget.Reserve(DateTime.UtcNow, TimeSpan.FromSeconds(5))
            ?? throw new RateLimitedException("Open Library is busy. Please try again shortly.", null);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        return JsonDocument.Parse(await http.TextAsync(uri, ct, open: true));
    }

    private async Task<T> CachedAsync<T>(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> load, CancellationToken ct)
    {
        if (cache.TryGetValue<T>($"{Revision}/{key}", out var found)) return found!;
        try
        {
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

    public void Dispose()
    {
        fresh.Dispose();
        cache.Dispose();
    }
}
