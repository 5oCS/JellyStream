using System.Net;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellystream;

// Path A's other half: a resolved book becomes a file in the Books library folder. The filename is
// the contract — Jellyfin's BookFileNameParser reads `Title (Year)` off it, so the server names the
// item from the name alone, and the EPUB's own metadata, cover and reading position are Jellyfin's
// to look after from there. Nothing else about a book needs writing here.
public static class BookPaths
{
    // The Internet Archive's readable formats mapped to an extension BookResolver recognises.
    // Anything outside this table would be written as a file no Books library can resolve.
    private static readonly IReadOnlyDictionary<string, string> Extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["EPUB"] = ".epub",
        ["PDF"] = ".pdf",
        ["Text PDF"] = ".pdf",
    };

    public static string? Extension(EbookFile file) => Extensions.TryGetValue(file.Format, out var extension) ? extension : null;

    // Catalog text becoming one path segment on the operator's disk, by the music channel's rule.
    public static string Part(string text) => MusicPaths.Part(text);

    // `Title (Year).ext` — BookFileNameParser's fourth pattern, which yields name and year. Without
    // a year the last-resort pattern still takes the whole string as the name, so a book with no
    // year is named correctly too, it simply carries none.
    public static string Name(EbookRecord book, string extension)
    {
        var title = Part(string.IsNullOrWhiteSpace(book.Title) ? book.Id : book.Title);
        return book.Year is { } year ? $"{title} ({year}){extension}" : title + extension;
    }

    // Where the book is written, or null when the folder is unset or not absolute, or the format is
    // one no Books library would resolve. The catalog chose this name, so it has to be proven to
    // stay inside the folder — the rule the music save folder follows too.
    public static string? File(string? folder, EbookRecord book, EbookFile file)
    {
        if (LocalFolderCatalog.Root(folder) is not { } root || Extension(file) is not { } extension) return null;
        var path = Path.GetFullPath(Path.Combine(root, Name(book, extension)));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? path : null;
    }
}

// The download half of Path A: an Archive book lands in the Books library folder whole or not at
// all, so Jellyfin never scans half a file, and a book already there is left exactly as it is.
public sealed class BookSaver(SafeHttp http, ILogger<BookSaver> logger)
{
    // The catalog already refuses anything past this when it lists files; it is checked again here
    // because the bound is the difference between a book and a scan of a shelf.
    public const long MaxBookBytes = 500L * 1024 * 1024;

    public static string? Folder => LocalFolderCatalog.Root(Plugin.Instance?.Configuration.BookFolder);

    // The address comes from a catalog response and this writes a file the operator's library will
    // then trust, so only the Archive's own hosts are fetched — never whatever a response names.
    public static bool ArchiveHost(Uri uri) => uri.Scheme == Uri.UriSchemeHttps
        && (uri.IdnHost == "archive.org" || uri.IdnHost.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase));

    public async Task<string> SaveAsync(EbookRecord book, EbookFile file, string folder, CancellationToken ct) =>
        await WriteAsync(book, file, folder, ArchiveHost, ct);

    // A direct file link from a catalog the operator configured themselves. The configured address
    // is what makes a host trustworthy — a feed may name any URL it likes, including one inside the
    // network — so the link is only followed while it stays on that address's own host, and that is
    // re-checked against the address it lands on after a redirect. Everything past the host check is
    // the Archive path, because the size bound, the magic-byte check and the name are all about the
    // bytes, which have no idea where they came from.
    public async Task<string> SaveUrlAsync(EbookRecord book, EbookFile file, string folder, string origin, CancellationToken ct)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var feed))
            throw new ProviderException("That book does not say which catalog it came from.");
        if (!SafeHttp.Trusted(file.Url, feed))
            throw new ProviderException("That file is not on the catalog's own address.");
        return await WriteAsync(book, file, folder, uri => SafeHttp.Trusted(uri, feed), ct);
    }

    private async Task<string> WriteAsync(EbookRecord book, EbookFile file, string folder, Func<Uri, bool> trusted, CancellationToken ct)
    {
        if (BookPaths.File(folder, book, file) is not { } target)
            throw new ProviderException("That book is in a format or folder no Books library would read.");
        // Checked before anything else, including the "already saved" shortcut: returning a path for
        // an address we would never have fetched hides the refusal behind a plausible answer.
        if (!trusted(file.Url)) throw new ProviderException("That book is not offered by the Archive.");
        if (file.Size is > MaxBookBytes) throw new ProviderException("That book is too large to save.");
        // Already there: whoever put it there owns it, and the library already has the item.
        if (File.Exists(target)) return target;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        try
        {
            await FetchAsync(file.Url, part, BookPaths.Extension(file)!, trusted, ct);
            // Never replace a file that is already there, whoever put it there.
            if (!File.Exists(target)) File.Move(part, target, overwrite: false);
            logger.LogInformation("Jellystream saved {Book} to the books folder", Path.GetFileName(target));
            return target;
        }
        finally
        {
            try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Moved into place only when it is the file it claims to be, so an error page never becomes a
    // book the library offers to read.
    private async Task FetchAsync(Uri url, string part, string extension, Func<Uri, bool> trusted, CancellationToken ct)
    {
        using var response = await http.OpenAsync(url, false, ct, open: true);
        // A redirect may leave the address that named the file, and the final address is the one
        // being read from — so it is the final address that has to still be a host we trust.
        var final = response.RequestMessage?.RequestUri ?? url;
        if (!trusted(final)) throw new ProviderException("That book is not offered by the Archive.");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new ProviderException("The Archive answered " + (int)response.StatusCode + " for that book.");
        if (response.Content.Headers.ContentLength is > MaxBookBytes) throw new ProviderException("That book is too large to save.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            for (var written = 0L; ; )
            {
                var read = await input.ReadAsync(buffer, ct);
                if (read == 0) break;
                if ((written += read) > MaxBookBytes) throw new ProviderException("That book is too large to save.");
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        if (!await MatchesAsync(part, extension, ct)) throw new ProviderException("That book did not arrive as a readable file.");
    }

    // A zip header for an EPUB, a PDF header for a PDF: the bytes that say the download is the
    // document it claims to be rather than a login page or a rate-limit notice.
    private static async Task<bool> MatchesAsync(string part, string extension, CancellationToken ct)
    {
        byte[] magic = extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "%PDF"u8.ToArray() : [0x50, 0x4B];
        var head = new byte[magic.Length];
        await using var check = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await check.ReadAsync(head, ct) == magic.Length && head.AsSpan().SequenceEqual(magic);
    }
}

public sealed record EbookSaveResponse(string File, string Format, long Bytes, int Available);

// Saving a book by its Archive identifier. The catalog supplies the title and year, so the name on
// disk never comes from whoever asked for it, and a scan is queued so the Books library picks it up.
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("StreamFin/Ebooks")]
public sealed class EbooksController(BookSaver saver, InternetArchiveCatalog archive, OpenLibraryCatalog openLibrary, OpdsCatalog opds, ILibraryManager library) : ControllerBase
{
    // Search the configured book sources in order, so the settings' source list decides which
    // catalogues are asked and in what order. A source that fails is left out rather than failing
    // the search — the rule the podcast shelf follows, because one broken API must not empty it.
    // Every row keeps its own Source, so a result from Open Library and one from the Archive are
    // told apart without a second round trip.
    //
    // The rows are interleaved rather than concatenated: asking each catalogue for the page and
    // then taking one row at a time in order means the order decides which row comes first without
    // one catalogue filling the page before the next is asked at all. Concatenating would leave
    // every source after the first unreachable in practice, since Open Library alone will return
    // twenty-four Dunes.
    [HttpGet("Search")]
    public async Task<ActionResult<IReadOnlyList<EbookRecord>>> Search([FromQuery] string q, CancellationToken ct, [FromQuery] int limit = 24)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest("A search term is required.");
        var settings = Plugin.Instance?.Configuration;
        var wanted = Plugin.Ordered(settings?.BookSourceOrder, OpenLibraryCatalog.SourceId, InternetArchiveCatalog.SourceId, OpdsCatalog.SourceId);
        var perSource = new List<IReadOnlyList<EbookRecord>>();
        foreach (var id in wanted)
        {
            if (id == OpdsCatalog.SourceId)
            {
                // Every configured catalog is a group of its own, so two addresses interleave with
                // each other exactly as two sources do instead of the first filling the page.
                foreach (var address in settings?.OpdsAddresses ?? [])
                    perSource.Add(await AskAsync(() => opds.SearchAsync(address, q, limit, ct)));
                continue;
            }
            perSource.Add(await AskAsync(() => id switch
            {
                OpenLibraryCatalog.SourceId => openLibrary.SearchAsync(q, limit, ct),
                InternetArchiveCatalog.SourceId => archive.SearchEbooksAsync(q, limit, ct),
                _ => Task.FromResult<IReadOnlyList<EbookRecord>>([]),
            }));
        }
        var found = new List<EbookRecord>();
        // Terminates when no catalogue has a row at this position any more, which is the only way
        // out once every source is exhausted.
        for (var index = 0; found.Count < limit; index++)
        {
            var any = false;
            foreach (var hits in perSource)
            {
                if (index >= hits.Count) continue;
                any = true;
                var hit = hits[index];
                if (found.Count >= limit) break;
                // The catalogues can return the same work; one row per id.
                if (!found.Any(other => other.Id == hit.Id)) found.Add(hit);
            }
            if (!any) break;
        }
        return Ok(found);
    }

    // One catalogue being unreachable is not a failed search: the rest still answer, which is the
    // only reason a broken feed can be left in the list without emptying every result.
    private static async Task<IReadOnlyList<EbookRecord>> AskAsync(Func<Task<IReadOnlyList<EbookRecord>>> ask)
    {
        try { return await ask(); }
        catch (ProviderException) { return []; }
        catch (HttpRequestException) { return []; }
        catch (OperationCanceledException) { return []; }
    }

    [HttpPost("Save")]
    public async Task<ActionResult<EbookSaveResponse>> Save([FromQuery] string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return BadRequest("An Archive identifier is required.");
        if (BookSaver.Folder is not { } folder) return BadRequest("No ebook folder is set.");
        try
        {
            var (book, files) = await archive.BookAsync(id.Trim(), ct);
            if (book is null) return BadRequest("The Archive has no readable copy of that item.");
            if (files.Count == 0) return BadRequest("That item has no file a Books library could read.");
            var path = await saver.SaveAsync(book, files[0], folder, ct);
            if (!library.IsScanRunning) library.QueueLibraryScan();
            return Ok(new EbookSaveResponse(Path.GetFileName(path), files[0].Format, new FileInfo(path).Length, files.Count));
        }
        catch (ProviderException ex) { return BadRequest(ex.Message); }
    }

    // Saving a book whose bytes sit on the catalog's own address instead of the Archive. The origin
    // travels with the request because it is what makes a host trustworthy: it is the address the
    // operator wrote into the settings, and the file is only followed while it stays on that
    // address's own host — a feed cannot name somewhere else and have us fetch it. The name comes
    // from the title the catalog reported, run through the same path rule as every other book.
    [HttpPost("SaveUrl")]
    public async Task<ActionResult<EbookSaveResponse>> SaveUrl([FromQuery] string url, [FromQuery] string origin, [FromQuery] string title, [FromQuery] int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(origin))
            return BadRequest("A file address and the catalog it came from are required.");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var target)) return BadRequest("That is not a file address.");
        if (!Uri.TryCreate(origin, UriKind.Absolute, out _)) return BadRequest("That catalog address is not a URL.");
        var format = Path.GetExtension(target.AbsolutePath).ToLowerInvariant() switch
        {
            ".epub" => "EPUB",
            ".pdf" => "PDF",
            _ => null,
        };
        if (format is null) return BadRequest("That link is not a book file a Books library could read.");
        if (BookSaver.Folder is not { } folder) return BadRequest("No ebook folder is set.");

        // The record exists only to name the file. The bytes come from the address, never the
        // caller, and the caller cannot choose where they land.
        var book = new EbookRecord(Id: target.ToString(), Source: OpdsCatalog.SourceId,
            Title: string.IsNullOrWhiteSpace(title) ? "Book" : title, Authors: [], Image: null,
            Description: null, Year: year, ArchiveIds: [], Files: [], Origin: origin);
        try
        {
            var path = await saver.SaveUrlAsync(book, new EbookFile(format, target, null), folder, origin, ct);
            if (!library.IsScanRunning) library.QueueLibraryScan();
            return Ok(new EbookSaveResponse(Path.GetFileName(path), format, new FileInfo(path).Length, 1));
        }
        catch (ProviderException ex) { return BadRequest(ex.Message); }
    }
}
