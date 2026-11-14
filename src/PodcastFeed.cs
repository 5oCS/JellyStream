using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Jellystream;

public sealed record PodcastMedia(string Url, string Type, long? Length)
{
    private string Extension => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() : "";
    private static bool VideoExtension(string extension) => extension is ".mp4" or ".m4v" or ".webm" or ".mov" or ".mkv";
    public bool Video => Type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
        || Type is "" or "application/octet-stream" && VideoExtension(Extension);
    public bool Audio => Type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || !Video;
    public string Container => Extension switch
    {
        ".mp3" => "mp3", ".m4a" or ".m4b" => "m4a", ".mp4" => "mp4", ".m4v" => "m4v", ".webm" => "webm",
        ".mov" => "mov", ".ogg" or ".oga" => "ogg", ".opus" => "opus", ".flac" => "flac", ".wav" => "wav",
        ".aac" => "aac", ".mkv" => "mkv", _ => Type.ToLowerInvariant() switch
        {
            "audio/mpeg" => "mp3", "audio/mp4" or "audio/m4a" or "audio/x-m4a" => "m4a", "audio/aac" => "aac",
            "audio/ogg" or "audio/opus" or "application/ogg" => "ogg", "audio/flac" or "audio/x-flac" => "flac",
            "audio/wav" or "audio/x-wav" or "audio/wave" => "wav", "video/mp4" => "mp4", "video/webm" => "webm",
            "video/quicktime" => "mov", _ => Video ? "mp4" : "mp3"
        }
    };
    public string ContentType => Type.Length > 0 ? Type : Container switch
    {
        "m4a" => "audio/mp4", "aac" => "audio/aac", "ogg" or "opus" => "audio/ogg", "flac" => "audio/flac",
        "wav" => "audio/wav", "webm" => "video/webm", "mov" => "video/quicktime", "mp4" => "video/mp4",
        _ => "audio/mpeg"
    };
}

public sealed record PodcastEpisode(string Id, string Title, string? Description, string? Link, string? Image,
    DateTimeOffset? Published, double? Seconds, PodcastMedia? Audio, PodcastMedia? Video);

public sealed record PodcastFeed(string Id, string Url, string Title, string? Description, string? Image,
    string? Author, IReadOnlyList<PodcastEpisode> Episodes);

public sealed record PodcastItem(PodcastFeed Feed, PodcastEpisode Episode);

public sealed record PodcastSource(string Name, string Url);

public static class PodcastIds
{
    private static readonly Regex Hex = new(@"\A[0-9a-f]{16}\z", RegexOptions.None, TimeSpan.FromSeconds(1));
    public static bool IsShow(string id) => Hex.IsMatch(id);
    public static bool IsEpisode(string id) => Hex.IsMatch(id);
    public static string Key(string value)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}

// RSS 2.0 and Atom the way podcast apps read them. One item may carry an audio file and a video file, from an
// enclosure, Media RSS or a Podcasting 2.0 alternateEnclosure. The reader stops at the episode limit, so a feed
// with years of back episodes costs one pass over the part of it that is ever shown.
public static class PodcastFeedReader
{
    public const int MaxEpisodes = 200;
    private const int MaxItems = 1000;
    private const int MaxTitle = 500;
    private const int MaxDescription = 4000;
    private const int MaxCharacters = 40_000_000;
    private const string MediaNs = "http://search.yahoo.com/mrss/";

    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.None, TimeSpan.FromSeconds(1));

    public static PodcastFeed Read(Stream stream, string url, string id)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = MaxCharacters,
            CloseInput = false
        };
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            var root = reader.LocalName.ToLowerInvariant();
            if (root is not ("rss" or "feed")) break;
            return ReadRoot(reader, url, id, root);
        }
        throw new ProviderException("This address is not a podcast feed.");
    }

    private static PodcastFeed ReadRoot(XmlReader reader, string url, string id, string root)
    {
        var depth = 0;
        if (root == "rss")
        {
            depth = -1;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0) break;
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.LocalName == "channel")
                { depth = 1; break; }
            }
            if (depth < 0) throw new ProviderException("This address is not a podcast feed.");
        }
        return ReadChannel(reader, url, id, depth);
    }

    private static PodcastFeed ReadChannel(XmlReader reader, string url, string id, int depth)
    {
        var title = "";
        string? description = null, image = null, author = null, link = null;
        DateTimeOffset? updated = null;
        var episodes = new List<PodcastEpisode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth <= depth) break;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1) continue;
            switch (reader.LocalName.ToLowerInvariant())
            {
                case "item" or "entry":
                    if (episodes.Count < MaxItems)
                    {
                        var episode = ReadItem(reader, depth + 1, url);
                        if (episode is not null && seen.Add(episode.Id)) episodes.Add(episode);
                    }
                    else Skip(reader, depth + 1);
                    break;
                case "title": if (title.Length == 0) title = Short(Plain(Text(reader, depth + 1))) ?? ""; break;
                case "description" or "subtitle" or "summary":
                    if (description is null) description = Clip(Plain(Text(reader, depth + 1)));
                    break;
                case "link": if (link is null) link = Short(Blank(Attr(reader, "href") ?? Text(reader, depth + 1))); break;
                case "author" or "creator":
                    if (author is null) author = Short(Plain(Name(reader, depth + 1)));
                    break;
                case "image":
                    if (image is null) image = Resolve(url, Attr(reader, "href")) ?? Resolve(url, Child(reader, depth + 1, "url"));
                    else Skip(reader, depth + 1);
                    break;
                case "logo" or "icon":
                    if (image is null) image = Resolve(url, Text(reader, depth + 1));
                    break;
                case "thumbnail":
                    if (image is null) image = Resolve(url, Attr(reader, "url"));
                    Skip(reader, depth + 1);
                    break;
                case "lastbuilddate" or "pubdate" or "updated" or "published" or "date":
                    if (updated is null) updated = Date(Text(reader, depth + 1));
                    break;
                default: Skip(reader, depth + 1); break;
            }
        }
        if (title.Length == 0 && episodes.Count == 0) throw new ProviderException("This address is not a podcast feed.");
        // Feeds disagree about which end is newest; sorting here keeps the shelf in one order either way.
        episodes.Sort((left, right) => (right.Published ?? DateTimeOffset.MinValue).CompareTo(left.Published ?? DateTimeOffset.MinValue));
        if (episodes.Count > MaxEpisodes) episodes.RemoveRange(MaxEpisodes, episodes.Count - MaxEpisodes);
        return new PodcastFeed(id, url, title.Length > 0 ? title : Host(url), description, image, author, episodes);
    }

    private static PodcastEpisode? ReadItem(XmlReader reader, int depth, string baseUrl)
    {
        if (reader.IsEmptyElement) return null;
        var title = "";
        string? guid = null, link = null, image = null, description = null;
        DateTimeOffset? published = null;
        double? seconds = null;
        PodcastMedia? enclosure = null;
        var contents = new List<PodcastMedia>();
        var alternates = new List<PodcastMedia>();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1) continue;
            switch (reader.LocalName.ToLowerInvariant())
            {
                case "title": if (title.Length == 0) title = Short(Plain(Text(reader, depth + 1))) ?? ""; break;
                case "guid" or "id": if (guid is null) guid = Blank(Text(reader, depth + 1)); break;
                case "link": if (link is null) link = Short(Blank(Attr(reader, "href") ?? Text(reader, depth + 1))); break;
                case "description" or "summary" or "subtitle" or "encoded":
                    if (description is null) description = Clip(Plain(Text(reader, depth + 1)));
                    break;
                case "content":
                    if (reader.NamespaceURI == MediaNs) Media(reader, depth + 1, baseUrl, contents, ref seconds);
                    else if (description is null) description = Clip(Plain(Text(reader, depth + 1)));
                    break;
                case "pubdate" or "published" or "updated" or "date":
                    if (published is null) published = Date(Text(reader, depth + 1));
                    break;
                case "duration": if (seconds is null) seconds = Duration(Text(reader, depth + 1)); break;
                case "enclosure":
                    enclosure ??= Make(baseUrl, Attr(reader, "url"), Attr(reader, "type"), Number(Attr(reader, "length")));
                    Skip(reader, depth + 1);
                    break;
                case "thumbnail":
                    if (image is null) image = Resolve(baseUrl, Attr(reader, "url"));
                    Skip(reader, depth + 1);
                    break;
                case "image":
                    if (image is null) image = Resolve(baseUrl, Attr(reader, "href")) ?? Resolve(baseUrl, Child(reader, depth + 1, "url"));
                    else Skip(reader, depth + 1);
                    break;
                case "alternateenclosure":
                    Source(reader, depth + 1, baseUrl, Attr(reader, "type"), Number(Attr(reader, "length")), alternates);
                    break;
                default: Skip(reader, depth + 1); break;
            }
        }
        if (title.Length == 0) return null;
        var candidates = new List<PodcastMedia>();
        if (enclosure is not null) candidates.Add(enclosure);
        candidates.AddRange(contents);
        candidates.AddRange(alternates);
        var identity = guid ?? enclosure?.Url ?? link ?? title + "\n" + (published?.ToString("O", CultureInfo.InvariantCulture) ?? "");
        return new PodcastEpisode(PodcastIds.Key(identity), title, description, link, image, published, seconds,
            candidates.Find(media => media.Audio), candidates.Find(media => media.Video));
    }

    private static void Media(XmlReader reader, int depth, string baseUrl, List<PodcastMedia> into, ref double? seconds)
    {
        var medium = Attr(reader, "medium");
        var type = Attr(reader, "type") ?? (medium == "video" ? "video/mp4" : medium == "audio" ? "audio/mpeg" : "");
        var made = Make(baseUrl, Attr(reader, "url"), type, Number(Attr(reader, "length")));
        if (made is not null) into.Add(made);
        seconds ??= Duration(Attr(reader, "duration"));
        Skip(reader, depth);
    }

    private static void Source(XmlReader reader, int depth, string baseUrl, string? type, long? length, List<PodcastMedia> into)
    {
        if (reader.IsEmptyElement) return;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) return;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != depth + 1) continue;
            if (reader.LocalName == "source")
            {
                var made = Make(baseUrl, Attr(reader, "uri") ?? Attr(reader, "href"), Attr(reader, "type") ?? type, Number(Attr(reader, "length")) ?? length);
                if (made is not null) into.Add(made);
            }
            else if (reader.LocalName == "sources") Source(reader, depth + 1, baseUrl, type, length, into);
            Skip(reader, depth + 1);
        }
    }

    private static PodcastMedia? Make(string baseUrl, string? address, string? type, long? length)
    {
        var url = Resolve(baseUrl, address);
        return url is null ? null : new PodcastMedia(url, type is { Length: > 64 } ? "" : type ?? "", length);
    }

    // A feed may hand out a relative or a plain-text address; only a public web address may be offered to a player.
    private static string? Resolve(string baseUrl, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || !Uri.TryCreate(baseUri, value.Trim(), out var uri)) return null;
        try { SafeHttp.ValidateOpen(uri); }
        catch (ProviderException) { return null; }
        return uri.ToString() is { Length: <= 4096 } address ? address : null;
    }

    private static void Skip(XmlReader reader, int depth)
    {
        if (reader.IsEmptyElement) return;
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) return;
    }

    private static string Text(XmlReader reader, int depth)
    {
        if (reader.IsEmptyElement) return "";
        var text = new StringBuilder();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace) text.Append(reader.Value);
            else if (reader.NodeType == XmlNodeType.Element) text.Append(' ');
        }
        return text.ToString();
    }

    private static string Child(XmlReader reader, int depth, string name)
    {
        if (reader.IsEmptyElement) return "";
        var value = "";
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1 && reader.LocalName == name && value.Length == 0)
                value = Text(reader, depth + 1);
        }
        return value;
    }

    // Atom nests the name inside <author>; an RSS feed puts the byline in the element itself.
    private static string Name(XmlReader reader, int depth)
    {
        if (reader.IsEmptyElement) return "";
        var named = "";
        var text = new StringBuilder();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "name" && named.Length == 0) named = Text(reader, depth + 1);
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) text.Append(reader.Value);
            else if (reader.NodeType == XmlNodeType.Element) text.Append(' ');
        }
        return named.Length > 0 ? named : text.ToString();
    }

    private static string? Attr(XmlReader reader, string name)
    {
        var value = reader.GetAttribute(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? Blank(string text) => text.Length == 0 ? null : text;
    private static string? Clip(string? text) => text is { Length: > MaxDescription } ? text[..MaxDescription] : text;
    // A title or a link comes straight from an untrusted document; the ones a player shows stay a sane length.
    private static string? Short(string? text) => text is { Length: > MaxTitle } ? text[..MaxTitle] : text;
    private static long? Number(string? text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    private static DateTimeOffset? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        // RFC 822 writes the zone as four digits with no colon, which DateTimeOffset will not take.
        if (value.Length > 5 && value[^5] is '+' or '-' && value[^3] != ':') value = value[..^2] + ":" + value[^2..];
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUniversalTime() : null;
    }

    private static double? Seconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var total)) return Whole(total);
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        var result = 0.0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var step) || step < 0) return null;
            result = result * 60 + step;
        }
        return Whole(result);
    }

    private static double? Whole(double seconds) => double.IsFinite(seconds) && seconds > 0 ? seconds : null;

    // Media RSS is meant to report seconds but several hosts write milliseconds; past a day there is no
    // reading of the number as seconds that describes a podcast episode. A length of time also has to fit a
    // time span, so nothing longer than a day is kept at all.
    private static double? Duration(string? text)
    {
        var seconds = Seconds(text);
        if (seconds is null) return null;
        var whole = seconds > 86_400 ? seconds.Value / 1000 : seconds.Value;
        return whole <= 86_400 ? whole : null;
    }

    private static string Plain(string text)
    {
        if (text.Length == 0) return "";
        return Spaces.Replace(WebUtility.HtmlDecode(Tags.Replace(text, " ")), " ").Trim();
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.IdnHost : url;
}
