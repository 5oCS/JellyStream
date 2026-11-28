using System.Xml.Linq;
using System.Text.Json;

namespace Jellystream;

// An OPDS catalog is the one source type that scales past the handful of sites we name ourselves:
// any address an operator can reach can be added, which is the
// whole reason Path D exists. Two formats are in common use and neither is reliably declared by
// the server, so the body decides: OPDS 2.0 is a JSON manifest (Readium WebPub), OPDS 1.1 is Atom
// with acquisition links.
//
// The formats differ in where the bytes are. OPDS 2.0 as Open Library serves it carries no
// acquisition link at all, only an alternate that has an Archive identifier folded into its query
// string — that identifier is handed back in ArchiveIds so the existing Archive save path handles
// it. Atom carries a direct file URL in Files instead, which the save path accepts only when it is
// on the feed's own address.
public sealed class OpdsCatalog(SafeHttp http) : IDisposable
{
    public const string SourceId = "opds";

    // Acquisition links, in the order they are preferred. OPDS 1.1 spells these out, Atom feeds
    // outside OPDS use the plain enclosure relation, and both mean "here are the bytes".
    private static readonly string[] AcquisitionRels =
    [
        "http://opds-spec.org/acquisition",
        "http://opds-spec.org/acquisition/open-access",
        "enclosure",
    ];

    public void Dispose() => http.Dispose();

    // Ask one catalogued address for a term. The address is the catalog root; OPDS says how to
    // search by advertising a template, so the root is read first and the template expanded. A
    // catalog that advertises no search cannot honour a term and returns nothing rather than
    // answering a different question than the one asked — Standard Ebooks' atom feed works this
    // way, returning the same new-releases rows whatever is passed.
    public async Task<IReadOnlyList<EbookRecord>> SearchAsync(string address, string term, int limit, CancellationToken ct)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var root)) return [];
        if (root.Scheme is not ("http" or "https")) return [];

        var search = await SearchTemplateAsync(root, ct) ?? root;
        var target = Expand(search, term, limit);

        var body = await FetchAsync(target, ct);
        if (string.IsNullOrWhiteSpace(body)) return [];

        return Parse(body, address, limit);
    }

    // The catalog root, read once per search, advertising where a term should go. OPDS 2.0 puts
    // this in links, OPDS 1.1 in a search relation, and OpenSearch is the fallback an Atom catalog
    // usually points at instead.
    private async Task<Uri?> SearchTemplateAsync(Uri root, CancellationToken ct)
    {
        var body = await FetchAsync(root, ct);
        if (string.IsNullOrWhiteSpace(body)) return null;

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var link in doc.RootElement.EnumerateArraySafe("links"))
                {
                    if (link.String("rel") != "search") continue;
                    if (Uri.TryCreate(root, link.String("href"), out var templated)) return templated;
                }
            }
            catch (JsonException) { }
            return null;
        }

        try
        {
            var xml = XDocument.Parse(body);
            var search = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "link" && e.Attribute("rel")?.Value == "search");
            var href = search?.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(href)) return null;

            // An OpenSearch description is a document in its own right, so it has to be read to
            // find the actual query template inside it.
            if (href.Contains("opensearch", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(root, href, out var descriptor))
            {
                var described = await FetchAsync(descriptor, ct);
                if (!string.IsNullOrWhiteSpace(described))
                {
                    try
                    {
                        var os = XDocument.Parse(described);
                        var template = os.Descendants().FirstOrDefault(e => e.Name.LocalName == "Url"
                            && (e.Attribute("type")?.Value ?? "").Contains("atom", StringComparison.OrdinalIgnoreCase))
                            ?.Attribute("template")?.Value;
                        if (!string.IsNullOrWhiteSpace(template)
                            && Uri.TryCreate(root, template, out var expanded)) return expanded;
                    }
                    catch (System.Xml.XmlException) { }
                }
                return null;
            }

            return Uri.TryCreate(root, href, out var direct) ? direct : null;
        }
        catch (System.Xml.XmlException) { }
        return null;
    }

    // Turn a templated address into one holding this term. OPDS 2.0 uses RFC 6570 braces, OpenSearch
    // uses its own names, and a catalog with neither takes the term as a plain query parameter.
    private static Uri Expand(Uri template, string term, int limit)
    {
        var href = template.ToString();
        if (href.Contains("{?"))
        {
            var start = href.IndexOf("{?", StringComparison.Ordinal);
            var end = href.IndexOf('}', start);
            if (end > start)
            {
                var names = href[(start + 2)..end].Split(',', StringSplitOptions.RemoveEmptyEntries);
                var kept = names.Where(n => n is not ("query" or "searchTerms" or "q" or "startPage" or "count"))
                    .Select(n => $"{n}={Uri.EscapeDataString(n == "limit" ? limit.ToString() : string.Empty)}");
                var query = string.Join("&", new[] { $"query={Uri.EscapeDataString(term)}", $"limit={limit}" }.Concat(kept));
                href = href[..start] + "?" + query + href[(end + 1)..];
            }
            else
            {
                href = href[..start] + $"?query={Uri.EscapeDataString(term)}";
            }
        }
        else if (href.Contains("{searchTerms}"))
        {
            href = href.Replace("{searchTerms}", Uri.EscapeDataString(term))
                .Replace("{count}", limit.ToString())
                .Replace("{startPage}", "1");
        }
        else
        {
            href += (href.Contains('?') ? "&" : "?") + $"query={Uri.EscapeDataString(term)}&limit={limit}";
        }

        return Uri.TryCreate(href, UriKind.Absolute, out var result) ? result : template;
    }

    private async Task<string?> FetchAsync(Uri target, CancellationToken ct)
    {
        // `open: true` because these addresses are the operator's own, not a list of sites we know:
        // Validate rejects anything outside a fixed host list, and a catalog is by definition one we
        // have not heard of. ValidateOpen is the check that still runs — it is the one that refuses
        // private and link-local addresses — and a destination it turns away simply yields no rows.
        try
        {
            var response = await SafeHttp.PatientAsync(() => http.OpenAsync(target, media: false, ct, open: true), ct);
            using (response)
            {
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadAsStringAsync(ct);
            }
        }
        catch (ProviderException) { return null; }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) { return null; }
    }

    // Parse whichever of the two formats the body actually is. Public because it is the pure half
    // of the catalog: given a body it returns rows, with nothing fetched and nothing trusted, so a
    // body captured from a live server is a real test rather than a fixture written to match code.
    public static IReadOnlyList<EbookRecord> Parse(string body, string address, int limit)
    {
        var trimmed = body.TrimStart();
        return trimmed.StartsWith('{') ? ParseJson(body, address, limit) : ParseAtom(body, address, limit);
    }

    private static IReadOnlyList<EbookRecord> ParseJson(string body, string address, int limit)
    {
        var found = new List<EbookRecord>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var publication in doc.RootElement.EnumerateArraySafe("publications"))
            {
                if (found.Count >= limit) break;
                var metadata = publication.Object("metadata");
                var title = metadata.String("title");
                if (string.IsNullOrWhiteSpace(title)) continue;

                var authors = metadata.EnumerateArraySafe("author")
                    .Select(a => a.String("name")).Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a!).ToArray();

                var links = publication.EnumerateArraySafe("links").ToList();
                var archive = links.Select(l => ArchiveIdentifier(l.String("href"))).FirstOrDefault(id => id is not null);
                var file = links.Select(l => Acquisition(l.String("href"), l.String("type"), l.String("rel")))
                    .FirstOrDefault(f => f is not null);

                found.Add(new EbookRecord(
                    Id: Link(links, "self") ?? title,
                    Source: SourceId,
                    Title: title.Trim(),
                    Authors: authors,
                    Image: null,
                    Description: metadata.String("description"),
                    Year: Year(metadata),
                    ArchiveIds: archive is null ? [] : [archive],
                    Files: file is null ? [] : [file],
                    Origin: address));
            }
        }
        catch (JsonException) { }
        return found;
    }

    private static IReadOnlyList<EbookRecord> ParseAtom(string body, string address, int limit)
    {
        var found = new List<EbookRecord>();
        try
        {
            var xml = XDocument.Parse(body);
            var atom = XNamespace.Get("http://www.w3.org/2005/Atom");
            foreach (var entry in xml.Descendants(atom + "entry"))
            {
                if (found.Count >= limit) break;
                var title = entry.Element(atom + "title")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(title)) continue;

                var authors = entry.Elements(atom + "author")
                    .Select(a => a.Element(atom + "name")?.Value?.Trim())
                    .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!).ToArray();

                var links = entry.Elements(atom + "link").ToList();
                var hrefs = links.Select(l => l.Attribute("href")?.Value).Where(h => !string.IsNullOrWhiteSpace(h))
                    .Select(h => h!).ToList();

                var archive = hrefs.Select(ArchiveIdentifier).FirstOrDefault(id => id is not null);
                var file = links.Select(l => Acquisition(l.Attribute("href")?.Value, l.Attribute("type")?.Value, l.Attribute("rel")?.Value))
                    .FirstOrDefault(f => f is not null);
                var image = links.FirstOrDefault(l => (l.Attribute("rel")?.Value == "http://opds-spec.org/image"
                        || l.Attribute("type")?.Value?.StartsWith("image/") == true))
                    ?.Attribute("href")?.Value;

                found.Add(new EbookRecord(
                    Id: entry.Element(atom + "id")?.Value?.Trim() ?? title,
                    Source: SourceId,
                    Title: title,
                    Authors: authors,
                    Image: image,
                    Description: null,
                    Year: AtomYear(entry),
                    ArchiveIds: archive is null ? [] : [archive],
                    Files: file is null ? [] : [file],
                    Origin: address));
            }
        }
        catch (System.Xml.XmlException) { }
        return found;
    }

    // Open Library folds the Archive identifier into an archive.org link it serves as an alternate.
    // Only a link that really is on the Archive counts, so a lookalike host names nothing.
    private static string? ArchiveIdentifier(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return null;
        if (uri.Host is not ("archive.org" or "www.archive.org") && !uri.Host.EndsWith(".archive.org")) return null;
        var identifier = Query(uri, "identifier");
        return string.IsNullOrWhiteSpace(identifier) ? null : identifier;
    }

    // One query parameter, read by hand: the framework's own parser lives in System.Web, which is
    // not part of what a plugin is guaranteed to have.
    private static string? Query(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index < 0) continue;
            if (!string.Equals(Uri.UnescapeDataString(pair[..index]), name, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair[(index + 1)..].Replace('+', ' '));
        }
        return null;
    }

    // The bytes themselves: an acquisition relation, or an enclosure when the catalog is plain Atom.
    private static EbookFile? Acquisition(string? href, string? type, string? rel)
    {
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(href, UriKind.Absolute, out var uri)) return null;
        if (rel is null || !AcquisitionRels.Any(r => rel.Contains(r, StringComparison.OrdinalIgnoreCase))) return null;

        var format = type?.ToLowerInvariant() switch
        {
            not null when type.Contains("epub") => "EPUB",
            not null when type.Contains("pdf") => "PDF",
            _ => Path.GetExtension(uri.AbsolutePath).Trim('.').ToUpperInvariant(),
        };
        if (format is not ("EPUB" or "PDF")) return null;
        return new EbookFile(format, uri, null);
    }

    private static string? Link(IEnumerable<JsonElement> links, string rel)
    {
        // By hand rather than FirstOrDefault: JsonElement is a struct, so there is no null to test.
        foreach (var link in links)
            if (link.String("rel") == rel) return link.String("href");
        return null;
    }

    private static int? Year(JsonElement metadata)
    {
        foreach (var name in new[] { "issued", "published", "modified" })
        {
            var value = metadata.String(name);
            if (value is { Length: >= 4 } && int.TryParse(value[..4], out var year)) return year;
        }
        return null;
    }

    private static int? AtomYear(XElement entry)
    {
        foreach (var name in new[] { "published", "updated" })
        {
            var value = entry.Element(XNamespace.Get("http://www.w3.org/2005/Atom") + name)?.Value;
            if (value is { Length: >= 4 } && int.TryParse(value[..4], out var year)) return year;
        }
        return null;
    }
}

// Small readers over System.Text.Json that keep the parsing above from drowning in TryGetProperty.
internal static class OpdsJson
{
    public static IEnumerable<JsonElement> EnumerateArraySafe(this JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in value.EnumerateArray()) yield return item;
    }

    public static JsonElement Object(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    public static string? String(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
