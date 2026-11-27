using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Jellystream;

// Internet Archive answers search while LibriVox is down for browsing, and mirrors the shelf's chapters.
public sealed class InternetArchiveCatalog(SafeHttp http) : IDisposable
{
    public const string SourceId = "ia";
    public static readonly Uri SearchEndpoint = new("https://archive.org/advancedsearch.php");
    private const string SearchFields = "fl%5B%5D=identifier&fl%5B%5D=title&fl%5B%5D=creator&fl%5B%5D=date&fl%5B%5D=description";
    // Identifiers stay URL-safe and short: the download links embed them verbatim.
    private static readonly Regex Identifier = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.None, TimeSpan.FromSeconds(1));

    public static bool IsBookId(string id) => Identifier.IsMatch(id);

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private readonly RateWindow budget = new(10, TimeSpan.FromSeconds(10));
    private readonly FreshCache<object> fresh = new(_ => true, 500);
    private int Revision => Plugin.Instance?.Configuration.CacheRevision ?? 0;

    private sealed record Detail(AudioBook Book, IReadOnlyList<AudioChapter> Chapters);

    public Task<IReadOnlyList<AudioBook>> SearchAsync(string term, int limit, CancellationToken ct)
    {
        term = Sanitize(term);
        if (term.Length == 0) return Task.FromResult<IReadOnlyList<AudioBook>>([]);
        limit = Math.Clamp(limit, 1, 50);
        return CachedAsync<IReadOnlyList<AudioBook>>($"search/{term.ToLowerInvariant()}/{limit}", TimeSpan.FromMinutes(30), async token =>
        {
            var query = "?q=" + Uri.EscapeDataString("collection:librivoxaudio AND " + term)
                + "&" + SearchFields + "&rows=" + limit + "&page=1&output=json";
            using var document = await QueryAsync(new Uri(SearchEndpoint, query), token);
            var root = document.RootElement;
            if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("docs", out var docs)
                || docs.ValueKind != JsonValueKind.Array) return [];
            var books = new List<AudioBook>();
            foreach (var doc in docs.EnumerateArray())
                if (Parse(doc, SourceId) is { } book) books.Add(book);
            return books;
        }, ct);
    }

    public async Task<AudioBook> GetAsync(string id, CancellationToken ct) =>
        (await DetailAsync(id, ct)).Book;

    public async Task<IReadOnlyList<AudioChapter>> ChaptersAsync(string id, CancellationToken ct) =>
        (await DetailAsync(id, ct)).Chapters;

    // One metadata fetch answers both the book folder and its chapters.
    private Task<Detail> DetailAsync(string id, CancellationToken ct)
    {
        if (!IsBookId(id)) throw new ProviderException("Invalid catalog page.");
        return CachedAsync($"detail/{id}", TimeSpan.FromHours(24), async token =>
        {
            using var document = await QueryAsync(new Uri("https://archive.org/metadata/" + Uri.EscapeDataString(id)), token);
            var root = document.RootElement;
            if (!root.TryGetProperty("metadata", out var metadata)) throw new ProviderException("This audiobook is unavailable.");
            var book = Parse(metadata, SourceId, id)
                ?? throw new ProviderException("This audiobook is unavailable.");
            var chapters = ReadChapters(root, id);
            if (chapters.Count == 0) throw new ProviderException("This audiobook has no chapters.");
            return new Detail(book, chapters);
        }, ct);
    }

    // The tracks feed carries no ordering key, so chapters keep their file order.
    private static IReadOnlyList<AudioChapter> ReadChapters(JsonElement root, string bookId)
    {
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return [];
        var perChapter = new List<(int Number, JsonElement File, int Rank)>();
        var audiobook = new List<(int Number, JsonElement File, int Rank)>();
        var order = 0;
        foreach (var file in files.EnumerateArray())
        {
            order++;
            var format = AudioJson.Text(file, "format");
            if (format is null) continue;
            var source = AudioJson.Text(file, "source");
            if (format.Contains("MP3", StringComparison.OrdinalIgnoreCase))
            {
                // Prefer the original encoding, then the better derivative, for each numbered track.
                var rank = format.StartsWith("VBR MP3", StringComparison.OrdinalIgnoreCase) && source == "original" ? 0
                    : format.StartsWith("128Kbps", StringComparison.OrdinalIgnoreCase) ? 1
                    : format.StartsWith("64Kbps", StringComparison.OrdinalIgnoreCase) ? 2 : 9;
                if (rank == 9) continue;
                var number = Track(file) ?? order;
                var index = perChapter.FindIndex(c => c.Number == number);
                if (index >= 0)
                {
                    if (rank < perChapter[index].Rank) perChapter[index] = (number, file, rank);
                }
                else perChapter.Add((number, file, rank));
            }
            else if (format.Equals("Audiobook", StringComparison.OrdinalIgnoreCase))
                audiobook.Add((audiobook.Count + 1, file, 0));
        }

        var picks = perChapter.Count > 0 ? perChapter : audiobook;
        var chapters = new List<AudioChapter>();
        foreach (var (number, file, _) in picks.OrderBy(c => c.Number))
        {
            var name = AudioJson.Text(file, "name");
            if (name is null || !Uri.TryCreate("https://archive.org/download/" + Uri.EscapeDataString(bookId) + "/" + Uri.EscapeDataString(name),
                UriKind.Absolute, out var location)) continue;
            var title = AudioJson.Text(file, "title");
            chapters.Add(new AudioChapter(
                number.ToString(CultureInfo.InvariantCulture), bookId, SourceId,
                string.IsNullOrWhiteSpace(title) ? "Chapter " + number.ToString(CultureInfo.InvariantCulture) : title.Trim(),
                number, location.ToString(), ParseLength(AudioJson.Text(file, "length"))));
        }
        return chapters;
    }

    private static int? Track(JsonElement file)
    {
        var raw = AudioJson.Text(file, "track");
        if (raw is null) return null;
        var head = raw.Split('/')[0];
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    }

    // Length arrives as seconds, "mm:ss", or "h:mm:ss" depending on the encoder that produced it.
    private static double? ParseLength(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return seconds;
        var parts = raw.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        var total = 0d;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var chunk)) return null;
            total = total * 60 + chunk;
        }
        return total;
    }

    private static AudioBook? Parse(JsonElement element, string source, string? fallbackId = null)
    {
        var id = fallbackId ?? AudioJson.Text(element, "identifier");
        var title = AudioJson.Text(element, "title");
        if (id is null || string.IsNullOrWhiteSpace(title) || !IsBookId(id)) return null;
        // Titles arrive as one string or a list; the folder takes the first.
        if (element.TryGetProperty("title", out var titles) && titles.ValueKind == JsonValueKind.Array && titles.GetArrayLength() > 0)
            title = titles[0].GetString();
        if (string.IsNullOrWhiteSpace(title)) return null;
        var date = AudioJson.Text(element, "date");
        int? year = null;
        if (date is { Length: >= 4 } && int.TryParse(date[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) year = parsed;
        return new AudioBook(id, source, title.Trim(), AudioJson.List(element, "creator"),
            "https://archive.org/services/img/" + id,
            AudioJson.Clean(AudioJson.Text(element, "description")), year, null, null);
    }

    // ---- Ebooks (Path A) ----------------------------------------------------------------

    private const string EbookFields = "fl%5B%5D=identifier&fl%5B%5D=title&fl%5B%5D=creator&fl%5B%5D=date"
        + "&fl%5B%5D=description&fl%5B%5D=access-restricted-item";
    // What a reader can open, best first. An encrypted file carries a different format name, so the
    // DRM half of the non-goals is met by this list rather than by a separate check.
    private static readonly string[] Readable = ["EPUB", "Text PDF", "PDF"];
    // A book is a book: past this the item is a scan of a shelf rather than a file to read.
    private const long MaxEbookBytes = 500L * 1024 * 1024;

    public Task<IReadOnlyList<EbookRecord>> SearchEbooksAsync(string term, int limit, CancellationToken ct)
    {
        term = Sanitize(term);
        if (term.Length == 0) return Task.FromResult<IReadOnlyList<EbookRecord>>([]);
        limit = Math.Clamp(limit, 1, 50);
        return CachedAsync<IReadOnlyList<EbookRecord>>($"ebooks/{term.ToLowerInvariant()}/{limit}", TimeSpan.FromMinutes(30), async token =>
        {
            var filter = "mediatype:texts AND (format:(\"EPUB\") OR format:(\"Text PDF\") OR format:(\"PDF\")) AND " + term;
            var query = "?q=" + Uri.EscapeDataString(filter) + "&" + EbookFields + "&rows=" + limit + "&page=1&output=json";
            using var document = await QueryAsync(new Uri(SearchEndpoint, query), token);
            var root = document.RootElement;
            if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("docs", out var docs)
                || docs.ValueKind != JsonValueKind.Array) return [];
            var books = new List<EbookRecord>();
            foreach (var doc in docs.EnumerateArray())
                if (ParseEbook(doc) is { } book) books.Add(book);
            return (IReadOnlyList<EbookRecord>)books;
        }, ct);
    }

    // One item's readable files. Lending items list theirs but will not serve them, so they offer
    // none rather than offering links that end at a login.
    public Task<IReadOnlyList<EbookFile>> FilesAsync(string id, CancellationToken ct)
    {
        if (!IsBookId(id)) throw new ProviderException("Invalid catalog page.");
        return CachedAsync<IReadOnlyList<EbookFile>>($"ebookfiles/{id}", TimeSpan.FromHours(24), async token =>
        {
            using var document = await QueryAsync(new Uri("https://archive.org/metadata/" + Uri.EscapeDataString(id)), token);
            var root = document.RootElement;
            if (!root.TryGetProperty("metadata", out var metadata) || Restricted(metadata)) return [];
            return ReadEbookFiles(root);
        }, ct);
    }

    // Lending items carry this flag in their metadata, and on a search row at its top level. The
    // Archive writes it as the string "true" rather than as a JSON true, so both are read.
    public static bool Restricted(JsonElement element)
    {
        if (!element.TryGetProperty("access-restricted-item", out var flag)) return false;
        return flag.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(flag.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public static EbookRecord? ParseEbook(JsonElement doc)
    {
        var id = AudioJson.Text(doc, "identifier");
        var title = AudioJson.Text(doc, "title");
        if (id is null || string.IsNullOrWhiteSpace(title) || !IsBookId(id)) return null;
        // Titles arrive as one string or a list; the folder takes the first.
        if (doc.TryGetProperty("title", out var titles) && titles.ValueKind == JsonValueKind.Array && titles.GetArrayLength() > 0)
            title = titles[0].GetString();
        if (string.IsNullOrWhiteSpace(title) || Restricted(doc)) return null;
        var date = AudioJson.Text(doc, "date");
        int? year = null;
        if (date is { Length: >= 4 } && int.TryParse(date[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) year = parsed;
        return new EbookRecord(id, SourceId, title.Trim(), AudioJson.List(doc, "creator"),
            "https://archive.org/services/img/" + id, AudioJson.Clean(AudioJson.Text(doc, "description")), year, [id], []);
    }

    // The readable files of an item, best format first and then the lightest of that format. The
    // node name comes from the response, so it is assembled as a URL and validated as one rather
    // than concatenated into a string that happens to look like a path.
    public static IReadOnlyList<EbookFile> ReadEbookFiles(JsonElement root)
    {
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return [];
        // Checked here as well as by the caller: a lending item lists the files it will not serve,
        // and a reader that trusts this list would hand out links that end at a login.
        if (root.TryGetProperty("metadata", out var metadata) && Restricted(metadata)) return [];
        var server = root.TryGetProperty("server", out var host) && host.ValueKind == JsonValueKind.String ? host.GetString() : null;
        var dir = root.TryGetProperty("dir", out var folder) && folder.ValueKind == JsonValueKind.String ? folder.GetString() : null;
        if (server is not { Length: > 0 } || dir is not { Length: > 0 } || !dir.StartsWith('/')) return [];
        var found = new List<EbookFile>();
        foreach (var file in files.EnumerateArray())
        {
            var format = AudioJson.Text(file, "format");
            if (format is null) continue;
            var rank = Array.IndexOf(Readable, format);
            if (rank < 0) continue;
            var name = AudioJson.Text(file, "name");
            if (name is null || name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal)) continue;
            long? size = long.TryParse(AudioJson.Text(file, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) ? bytes : null;
            if (size > MaxEbookBytes) continue;
            if (!Uri.TryCreate($"https://{server}{dir}/{Uri.EscapeDataString(name)}", UriKind.Absolute, out var url)) continue;
            try { SafeHttp.ValidateOpen(url); } catch (ProviderException) { continue; }
            found.Add(new EbookFile(format, url, size));
        }
        return found.OrderBy(f => Array.IndexOf(Readable, f.Format)).ThenBy(f => f.Size ?? long.MaxValue).ToArray();
    }

    // Free text only: quotes, parentheses, and AND-words would rewrite the query's meaning.
    private static string Sanitize(string term)
    {
        var kept = term.Where(c => char.IsLetterOrDigit(c) || c is '\'' or '-').Take(100).ToArray();
        return new string(kept).Trim('\'', '-').Trim();
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

    private async Task<JsonDocument> QueryAsync(Uri uri, CancellationToken ct)
    {
        var wait = budget.Reserve(DateTime.UtcNow, TimeSpan.FromSeconds(5)) ?? throw new RateLimitedException("The Internet Archive is busy. Please try again shortly.", null);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        var text = await http.TextAsync(uri, ct, open: true);
        return JsonDocument.Parse(text);
    }

    public void Dispose()
    {
        fresh.Dispose();
        cache.Dispose();
    }
}
