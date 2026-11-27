using System.IO.Compression;
using System.Xml.Linq;

namespace Jellystream;

// Path E: a folder of EPUBs, PDFs and their neighbours, indexed for browsing and search. The mirror
// image of the music channel writing to a music library folder — no network and no legal question,
// and the one way the shelf still works on a server that is entirely offline.
public static class LocalFolderCatalog
{
    public const string SourceId = "local";
    // What in the folder counts as a book. Mirrors Jellyfin's BookResolver exactly (Jellyfin 12.3,
    // Emby.Server.Implementations/Library/Resolvers/Books/BookResolver.cs), because this folder is
    // the same one the operator adds as a Books library: anything Jellyfin's resolver will not turn
    // into a Book item would be shown here and be permanently unopenable there. Note .djvu, .fb2 and
    // .txt are deliberately absent — Jellyfin does not recognise them.
    private static readonly string[] Extensions =
        [".azw", ".azw3", ".cb7", ".cbr", ".cbt", ".cbz", ".epub", ".mobi", ".pdf"];
    // The folder is the operator's and may be enormous, so the walk is bounded rather than trusted:
    // a shelf of ten thousand files is browsed, a whole disk is not.
    private const int MaxBooks = 2000;
    private const long MaxBytes = 500L * 1024 * 1024;
    private static readonly Uri FileRoot = new("file:///");

    // The configured folder, or null when it is empty or not an absolute path — the same rule the
    // music save folder uses, since both are paths this process will read from.
    public static string? Root(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Path.IsPathFullyQualified(text.Trim())) return null;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(text.Trim()));
    }

    public static bool IsBook(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // Every book under the folder, newest names last so a shelf keeps a stable order. A file that
    // cannot be read is skipped rather than failing the whole walk, and a symlink is not followed:
    // the operator chose this folder, not wherever it points.
    public static IReadOnlyList<EbookRecord> Index(string folder, CancellationToken ct = default)
    {
        if (Root(folder) is not { } root || !Directory.Exists(root)) return [];
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var found = new List<EbookRecord>();
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (found.Count >= MaxBooks) break;
            if (!IsBook(path)) continue;
            try
            {
                // A file past the bound is not a book, whatever its extension claims.
                if (new FileInfo(path).Length > MaxBytes) continue;
                found.Add(Record(path, root));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A file that vanished or cannot be stat'd is not worth stopping the shelf for.
            }
        }
        return found;
    }

    private static EbookRecord Record(string path, string root)
    {
        var title = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Trim();
        var authors = Array.Empty<string>();
        // An EPUB carries its own title and author inside the archive; a filename usually carries
        // neither, and "9780441478125" is not a title anyone should have to read.
        if (string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase)
            && Epub.Metadata(path) is { } meta)
        {
            if (!string.IsNullOrWhiteSpace(meta.Title)) title = meta.Title.Trim();
            authors = meta.Authors;
        }
        if (title.Length == 0) title = Path.GetFileName(path);
        var format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return new EbookRecord(Path.GetRelativePath(root, path), SourceId, title, authors,
            Image: null, Description: null, Year: null, ArchiveIds: [],
            Files: [new EbookFile(format, new Uri(FileRoot, path), new FileInfo(path).Length)]);
    }
}

// The two documents an EPUB is required to name: the container says where the package document is,
// and the package document says what the book is. Both are read as XML, and neither is ever written.
public static class Epub
{
    public sealed record EpubInfo(string? Title, string[] Authors);

    // null for anything that is not a readable EPUB — a half-downloaded file is a filename, not a
    // failure, and the shelf should still show it.
    public static EpubInfo? Metadata(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var container = zip.GetEntry("META-INF/container.xml");
            if (container is null) return null;
            string? package;
            using (var stream = container.Open())
                // The path inside the archive is only ever used as an entry name, so it cannot reach
                // outside the file no matter what a malformed archive puts there.
                package = XDocument.Load(stream).Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")
                    ?.Attribute("full-path")?.Value;
            if (string.IsNullOrWhiteSpace(package)) return null;
            var entry = zip.GetEntry(package);
            if (entry is null) return null;
            using var opf = entry.Open();
            var document = XDocument.Load(opf);
            var title = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
            var authors = document.Descendants().Where(e => e.Name.LocalName == "creator")
                .Select(e => e.Value).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            return string.IsNullOrWhiteSpace(title) && authors.Length == 0 ? null : new EpubInfo(title, authors);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Xml.XmlException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
