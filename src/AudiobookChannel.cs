using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellystream;

// Item IDs: recent, recent/<page>, genre/<slug>, genre/<slug>/<page>, book/<lv|ia>/<bookId>,
// chapter/<lv|ia>/<bookId>/<chapterId>. LibriVox is the shelf and the Internet Archive answers
// search; a chapter plays from the source's own address, with YouTube only when neither answers.
public sealed class AudiobookChannel(LibriVoxCatalog books, InternetArchiveCatalog archive, YtDlp tools, StreamRelay relay,
    IHttpContextAccessor contexts, IAuthorizationContext authorization, ILibraryManager library, ILogger<AudiobookChannel> logger)
    : IChannel, IHasCacheKey, IRequiresMediaInfoCallback
{
    public const string ChannelName = "Audiobooks";
    public static bool IsAvailable => Plugin.Instance?.Configuration is { Enabled: true, AudiobookEnabled: true };
    public string Name => ChannelName;
    public string Description => "Jellystream · Listen to free audiobooks from LibriVox and the Internet Archive.";
    public string DataVersion => "jellystream-audiobook-1-" + IsAvailable + "-" + (Plugin.Instance?.Configuration.CacheRevision ?? 0);
    public string HomePageUrl => "https://github.com/5oCS/JellyStream";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId) => IsAvailable && Guid.TryParse(userId, out var id) && Plugin.Allows(id);
    public string GetCacheKey(string? userId) => $"{userId}-{DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440)).Ticks}";
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        // Jellyfin's content types are Clip, Podcast, Trailer, Movie, Episode, Song, MovieExtra and TvExtra;
        // there is no Book, so chapters are filed as songs, as the music channel's audio is.
        MediaTypes = [ChannelMediaType.Audio], ContentTypes = [ChannelMediaContentType.Song],
        AutoRefreshLevels = 0, SupportsContentDownloading = false
    };
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) => Task.FromResult(new DynamicImageResponse
    {
        HasImage = type == ImageType.Primary, Format = ImageFormat.Png,
        Stream = typeof(AudiobookChannel).Assembly.GetManifestResourceStream("Jellystream.Assets.audiobook.png")
    });

    private static ChannelItemInfo Folder(string id, string name, string? overview = null) => new()
    {
        Id = id, Name = name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container,
        Overview = overview ?? "", Tags = ["Jellystream"]
    };
    private static ChannelItemResult Result(IEnumerable<ChannelItemInfo> items)
    {
        var array = items.ToArray();
        return new() { Items = array, TotalRecordCount = array.Length };
    }

    public static ChannelItemInfo Book(AudioBook book) => new()
    {
        Id = $"book/{book.Source}/{book.Id}", Name = book.Title, Type = ChannelItemType.Folder,
        FolderType = ChannelFolderType.Container, ImageUrl = book.Image, Overview = book.Description,
        ProductionYear = book.Year, Artists = book.Authors.ToList(), AlbumArtists = book.Authors.ToList(),
        Tags = ["Jellystream"], ProviderIds = new Dictionary<string, string> { ["Custom"] = $"jellystream-book-{book.Source}-{book.Id}" }
    };
    private static ChannelItemInfo Chapter(AudioBook book, AudioChapter chapter) => new()
    {
        Id = $"chapter/{chapter.Source}/{chapter.BookId}/{chapter.Id}", Name = chapter.Title, Type = ChannelItemType.Media,
        MediaType = ChannelMediaType.Audio, ContentType = ChannelMediaContentType.Song, ImageUrl = book.Image,
        IndexNumber = chapter.Number, RunTimeTicks = chapter.Seconds is > 0 ? TimeSpan.FromSeconds(chapter.Seconds.Value).Ticks : null,
        Artists = book.Authors.ToList(), AlbumArtists = book.Authors.ToList(), Tags = ["Jellystream"],
        ProviderIds = new Dictionary<string, string> { ["Custom"] = $"jellystream-chapter-{chapter.Source}-{chapter.BookId}-{chapter.Id}" }
    };

    // Book details and chapters, kept so a listing, opening the book and playing a chapter ask once.
    private sealed record CachedBook(AudioBook Book, IReadOnlyList<AudioChapter>? Chapters);
    private static readonly MemoryCache Titles = new(new MemoryCacheOptions { SizeLimit = 500 });

    private static string Key(string source, string bookId) => source + "/" + bookId;
    private static CachedBook? Held(string source, string bookId) => Titles.TryGetValue<CachedBook>(Key(source, bookId), out var found) ? found : null;
    // A listing that carries only the book must not replace a cached chapter list.
    private static void Remember(AudioBook book, IReadOnlyList<AudioChapter>? chapters)
    {
        if (chapters is null && Titles.TryGetValue<CachedBook>(Key(book.Source, book.Id), out var held) && held?.Chapters is not null) return;
        Titles.Set(Key(book.Source, book.Id), new CachedBook(book, chapters),
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
    }

    private static bool ValidBookId(string source, string bookId) => source == LibriVoxCatalog.SourceId
        ? LibriVoxCatalog.IsBookId(bookId) : source == InternetArchiveCatalog.SourceId && InternetArchiveCatalog.IsBookId(bookId);
    private async Task<AudioBook> BookAsync(string source, string bookId, CancellationToken ct)
        => source == LibriVoxCatalog.SourceId ? await books.GetAsync(bookId, ct) : await archive.GetAsync(bookId, ct);
    private async Task<IReadOnlyList<AudioChapter>> ChaptersAsync(string source, string bookId, CancellationToken ct)
        => source == LibriVoxCatalog.SourceId ? await books.ChaptersAsync(bookId, ct) : await archive.ChaptersAsync(bookId, ct);

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        if (query.UserId != Guid.Empty && !Plugin.Allows(query.UserId)) throw new UnauthorizedAccessException("Jellystream is not enabled for this account.");
        if (!IsAvailable) return Result([]);
        var id = query.FolderId ?? "";
        try { return await BrowseAsync(id, ct); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Jellystream audiobook request failed ({ErrorType})", ex.GetType().Name);
            var message = ex is ProviderException ? ex.Message : "The audiobook catalogs are temporarily unavailable. Please try again later.";
            return Result([Folder(id + "/status", message, message)]);
        }
    }

    private async Task<ChannelItemResult> BrowseAsync(string id, CancellationToken ct)
    {
        if (id.EndsWith("/status", StringComparison.Ordinal)) return Result([]);
        if (id.Length == 0) return Result(RootItems());
        if (id == "search")
        {
            var parent = library.GetNewItemId(BookSearch.FolderKey, typeof(Folder));
            return Result(library.GetItemList(new InternalItemsQuery { ParentId = parent, Recursive = false }).Select(Restore).OfType<ChannelItemInfo>());
        }
        var parts = id.Split('/');
        // Two LibriVox pages are shown together, so a shelf opens as fast as one call's worth of titles.
        if (parts[0] == "recent" && parts.Length <= 2)
        {
            if (parts.Length == 1) return await ListAsync("recent", 1, ct);
            return int.TryParse(parts[1], out var page) ? await ListAsync("recent", page, ct) : Result([]);
        }
        if (parts[0] == "genre" && parts.Length is 2 or 3 && LibriVoxCatalog.GenreName(parts[1]) is not null)
        {
            if (parts.Length == 2) return await ListAsync(parts[1], 1, ct);
            return int.TryParse(parts[2], out var page) ? await ListAsync(parts[1], page, ct) : Result([]);
        }
        if (parts is ["book", var source, var bookId])
        {
            if (!ValidBookId(source, bookId)) return Result([]);
            var held = Held(source, bookId);
            AudioBook book;
            IReadOnlyList<AudioChapter> chapters;
            if (held?.Chapters is { } known) (book, chapters) = (held.Book, known);
            else (book, chapters) = await FetchAsync(source, bookId, ct);
            return Result(chapters.Select(chapter => Chapter(book, chapter)));
        }
        return Result([]);
    }

    private static IEnumerable<ChannelItemInfo> RootItems()
    {
        yield return Folder("search", "Search results", "Use Jellyfin's search (magnifying glass) to find audiobooks online. What you find appears here.");
        yield return Folder("recent", "Recently added", "The newest recordings LibriVox has published.");
        foreach (var (slug, name) in LibriVoxCatalog.Genres) yield return Folder("genre/" + slug, name);
    }

    private async Task<ChannelItemResult> ListAsync(string category, int page, CancellationToken ct)
    {
        var loaded = await Pages.LoadAsync(page, 2, 500, number => books.BrowseAsync(category, number, ct),
            listed => listed.Items, listed => listed.HasNext, book => (book.Source, book.Id));
        foreach (var book in loaded.Items) Remember(book, null);
        var items = loaded.Items.Select(Book).ToList();
        if (loaded.Next is { } next) items.Add(Folder(category + "/" + next, $"More ({(next - 1) / 2 + 1})"));
        return Result(items);
    }

    // Search results are library albums made by BookSearch; describe them again so Jellyfin keeps them.
    private static ChannelItemInfo? Restore(BaseItem item) => item switch
    {
        MusicAlbum album => new ChannelItemInfo
        {
            Id = album.ExternalId, Name = album.Name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container,
            Overview = album.Overview, ProductionYear = album.ProductionYear, Artists = album.Artists.ToList(),
            AlbumArtists = album.AlbumArtists.ToList(), Tags = album.Tags.ToList(), ProviderIds = album.ProviderIds
        },
        _ => null
    };

    private static bool IsSourceFailure(Exception ex) => ex is ProviderException or HttpRequestException or System.Text.Json.JsonException
        or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or OperationCanceledException;

    private async Task<(AudioBook Book, IReadOnlyList<AudioChapter> Chapters)> FetchAsync(string source, string bookId, CancellationToken ct)
    {
        var book = Held(source, bookId)?.Book ?? await BookAsync(source, bookId, ct);
        var chapters = await ChaptersAsync(source, bookId, ct);
        Remember(book, chapters);
        return (book, chapters);
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        var context = contexts.HttpContext;
        if (context is null) return [];
        var auth = await authorization.GetAuthorizationInfo(context);
        if (!auth.IsAuthenticated || !Plugin.Allows(auth.UserId) || !IsAvailable) throw new UnauthorizedAccessException("Jellystream is not enabled for this account.");
        if (id.Split('/') is not ["chapter", var source, var bookId, var chapterId] || !ValidBookId(source, bookId))
            throw new ProviderException("Invalid Jellystream chapter.");
        var user = auth.UserId;
        var known = Held(source, bookId);
        if (known?.Chapters is { } held) return [Direct(Find(held, chapterId))];
        IReadOnlyList<AudioChapter> chapters;
        try { (_, chapters) = await FetchAsync(source, bookId, ct); }
        // Neither catalog answered: what a listing already found may still play, or YouTube has the recording.
        catch (Exception ex) when (!ct.IsCancellationRequested && IsSourceFailure(ex) && known?.Book is { } found && YouTubeReady)
        {
            try { return [await YouTubeAsync(user, found, source, bookId, chapterId, ct)]; }
            catch (Exception fallback) when (!ct.IsCancellationRequested)
            { logger.LogWarning("Jellystream audiobook YouTube fallback failed ({ErrorType})", fallback.GetType().Name); }
            throw;
        }
        return [Direct(Find(chapters, chapterId))];
    }

    private static AudioChapter Find(IReadOnlyList<AudioChapter> chapters, string chapterId)
        => chapters.FirstOrDefault(listed => listed.Id == chapterId) ?? throw new ProviderException("This chapter is unavailable.");

    // The recording's own address: Jellyfin fetches it itself, so nothing here is relayed or signed.
    private static MediaSourceInfo Direct(AudioChapter chapter)
    {
        if (!Uri.TryCreate(chapter.Url, UriKind.Absolute, out var location)) throw new ProviderException("This chapter is unavailable.");
        var extension = Path.GetExtension(location.AbsolutePath);
        var container = extension is ".mp3" or ".m4b" or ".m4a" or ".ogg" or ".opus" or ".flac" ? extension[1..].ToLowerInvariant() : "mp3";
        return new MediaSourceInfo
        {
            Path = location.ToString(), Protocol = MediaProtocol.Http, Container = container, IsRemote = true,
            Name = (chapter.Source == LibriVoxCatalog.SourceId ? "LibriVox" : "Internet Archive") + " · " + container.ToUpperInvariant(),
            SupportsDirectPlay = true, SupportsDirectStream = true, SupportsTranscoding = true,
            // Jellyfin needs a stream to play, or it refuses the request and playback stops at once.
            MediaStreams = [SourceStreams.Audio(container)],
            RunTimeTicks = chapter.Seconds is > 0 ? TimeSpan.FromSeconds(chapter.Seconds.Value).Ticks : null
        };
    }

    // yt-dlp and deno are the music channel's tools: Ready follows MusicEnabled, MusicYouTube and
    // MusicAllowLossy as well as this channel's own switch, so all of them must be on to fall back.
    private static bool YouTubeReady => Plugin.Instance?.Configuration is { AudiobookYouTube: true } && YtDlp.Ready;

    private async Task<MediaSourceInfo> YouTubeAsync(Guid user, AudioBook book, string source, string bookId, string chapterId, CancellationToken ct)
    {
        var (ticket, title) = await TicketAsync(book, source, bookId, chapterId, ct);
        static ResolvedVideo Video(AudioTicket t) => new(t.Upstream, "AAC", new Dictionary<string, string>(), false, Ticket: t);
        // A refresh after an expired link asks YouTube again: its addresses last a few hours only.
        return await relay.MediaSourceAsync(user, Video(ticket), "YouTube · " + title,
            async token => [Video((await TicketAsync(book, source, bookId, chapterId, token)).Ticket)],
            () => Plugin.Allows(user) && IsAvailable, ct);
    }

    private async Task<(AudioTicket Ticket, string Title)> TicketAsync(AudioBook book, string source, string bookId, string chapterId, CancellationToken ct)
    {
        var deno = YtDlp.DenoFile ?? throw new ProviderException("YouTube needs deno on this server.");
        var query = $"{book.Title} {string.Join(' ', book.Authors)} audiobook";
        var json = await tools.RunAsync(YtDlp.SearchArguments(deno, query), TimeSpan.FromSeconds(25), ct);
        var found = MusicAudio.ParseSearch(json);
        var hit = found.FirstOrDefault(f => f.Duration is > 60) ?? found.FirstOrDefault()
            ?? throw new ProviderException("No recording was found for this chapter.");
        var urls = await tools.RunAsync(YtDlp.AudioArguments(deno, hit.Id), TimeSpan.FromSeconds(30), ct);
        if (!Uri.TryCreate(urls.Trim().Split('\n')[0].Trim(), UriKind.Absolute, out var upstream) || !MusicAudio.IsAudioAddress(upstream))
            throw new ProviderException("This recording could not be played.");
        return (new AudioTicket($"audiobook-{source}-{bookId}-{chapterId}", "youtube", upstream, "m4a"), hit.Title);
    }
}
