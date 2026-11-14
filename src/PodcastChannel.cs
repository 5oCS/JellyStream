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
using Microsoft.Extensions.Logging;

namespace Jellystream;

// Item IDs: search, recent, show/<showId>, episode/<showId>/<episodeId>. A show's own address plays the
// episode directly when it publishes a file; a feed that only points at YouTube plays its audio through the relay.
public sealed class PodcastChannel(PodcastCatalog catalog, YtDlp tools, StreamRelay relay, IHttpContextAccessor contexts,
    IAuthorizationContext authorization, ILibraryManager library, ILogger<PodcastChannel> logger)
    : IChannel, IHasCacheKey, IRequiresMediaInfoCallback
{
    public const string ChannelName = "Podcasts";
    public static bool IsAvailable => Plugin.Instance?.Configuration is { Enabled: true, PodcastEnabled: true };
    public string Name => ChannelName;
    public string Description => "Jellystream · Listen to and watch podcasts from RSS feeds.";
    public string DataVersion => "jellystream-podcast-1-" + IsAvailable + "-" + (Plugin.Instance?.Configuration.CacheRevision ?? 0);
    public string HomePageUrl => "https://github.com/5oCS/JellyStream";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId) => IsAvailable && Guid.TryParse(userId, out var id) && Plugin.Allows(id);
    public string GetCacheKey(string? userId) => $"{userId}-{DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440)).Ticks}";
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        // A feed that publishes a file is a video when it publishes video; the badge follows what it published.
        MediaTypes = [ChannelMediaType.Audio, ChannelMediaType.Video],
        ContentTypes = [ChannelMediaContentType.Podcast, ChannelMediaContentType.Episode],
        AutoRefreshLevels = 0, SupportsContentDownloading = false
    };
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) => Task.FromResult(new DynamicImageResponse
    {
        HasImage = type == ImageType.Primary, Format = ImageFormat.Png,
        Stream = typeof(PodcastChannel).Assembly.GetManifestResourceStream("Jellystream.Assets.podcast.png")
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

    private ChannelItemInfo SourceFolder(PodcastSource source)
    {
        var known = catalog.Peek(source);
        var info = Folder("show/" + PodcastCatalog.ShowId(source.Url), known?.Title ?? source.Name, known?.Description);
        info.ImageUrl = known?.Image;
        return info;
    }

    // A show as search finds it: the same folder id the channel opens, so a result leads to the episode list.
    public static ChannelItemInfo Show(PodcastFeed feed) => new()
    {
        Id = "show/" + feed.Id, Name = feed.Title, Type = ChannelItemType.Folder,
        FolderType = ChannelFolderType.Container, ImageUrl = feed.Image, Overview = feed.Description ?? "",
        Tags = ["Jellystream"], ProviderIds = new Dictionary<string, string> { ["Custom"] = $"jellystream-show-{feed.Id}" }
    };

    private static ChannelItemInfo Episode(PodcastFeed feed, PodcastEpisode episode)
    {
        // Video only when the feed published a video file for this episode.
        var video = episode.Video is not null;
        return new ChannelItemInfo
        {
            Id = $"episode/{feed.Id}/{episode.Id}", Name = episode.Title, Type = ChannelItemType.Media,
            MediaType = video ? ChannelMediaType.Video : ChannelMediaType.Audio,
            ContentType = video ? ChannelMediaContentType.Episode : ChannelMediaContentType.Podcast,
            ImageUrl = episode.Image ?? feed.Image, Overview = episode.Description ?? "",
            SeriesName = feed.Title, PremiereDate = episode.Published?.UtcDateTime,
            ProductionYear = episode.Published?.Year,
            RunTimeTicks = episode.Seconds is > 0 ? TimeSpan.FromSeconds(episode.Seconds.Value).Ticks : null,
            Artists = feed.Author is { Length: > 0 } ? [feed.Author] : [],
            AlbumArtists = feed.Author is { Length: > 0 } ? [feed.Author] : [], Tags = ["Jellystream"],
            ProviderIds = new Dictionary<string, string> { ["Custom"] = $"jellystream-podcast-{episode.Id}" }
        };
    }

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        if (query.UserId != Guid.Empty && !Plugin.Allows(query.UserId)) throw new UnauthorizedAccessException("Jellystream is not enabled for this account.");
        if (!IsAvailable) return Result([]);
        var id = query.FolderId ?? "";
        try { return await BrowseAsync(id, ct); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Jellystream podcast request failed ({ErrorType})", ex.GetType().Name);
            var message = ex is ProviderException ? ex.Message : "The podcast feeds are temporarily unavailable. Please try again later.";
            return Result([Folder(id + "/status", message, message)]);
        }
    }

    private async Task<ChannelItemResult> BrowseAsync(string id, CancellationToken ct)
    {
        if (id.EndsWith("/status", StringComparison.Ordinal)) return Result([]);
        if (id.Length == 0) return Result(RootItems());
        if (id == "search")
        {
            var parent = library.GetNewItemId(PodcastSearch.FolderKey, typeof(Folder));
            return Result(library.GetItemList(new InternalItemsQuery { ParentId = parent, Recursive = false }).Select(Restore).OfType<ChannelItemInfo>());
        }
        if (id == "recent")
            return Result((await catalog.RecentAsync(ct)).Select(item => Episode(item.Feed, item.Episode)));
        if (id.Split('/') is ["show", var show] && PodcastCatalog.IsShowId(show))
        {
            var feed = await catalog.ShowAsync(show, ct);
            return feed is null ? Result([]) : Result(feed.Episodes.Select(episode => Episode(feed, episode)));
        }
        return Result([]);
    }

    private IEnumerable<ChannelItemInfo> RootItems()
    {
        yield return Folder("search", "Search results", "Use Jellyfin's search (magnifying glass) to find podcasts online. What you find appears here.");
        yield return Folder("recent", "Recently added", "The newest episodes from the feeds this server follows.");
        foreach (var source in catalog.Sources()) yield return SourceFolder(source);
    }

    // Search results are library albums made by PodcastSearch; describe them again so Jellyfin keeps them.
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

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        var context = contexts.HttpContext;
        if (context is null) return [];
        var auth = await authorization.GetAuthorizationInfo(context);
        if (!auth.IsAuthenticated || !Plugin.Allows(auth.UserId) || !IsAvailable) throw new UnauthorizedAccessException("Jellystream is not enabled for this account.");
        if (id.Split('/') is not ["episode", var show, var episode] || !PodcastCatalog.IsShowId(show) || !PodcastCatalog.IsEpisodeId(episode))
            throw new ProviderException("Invalid Jellystream episode.");
        var feed = await catalog.ShowAsync(show, ct) ?? throw new ProviderException("This episode is unavailable.");
        var found = feed.Episodes.FirstOrDefault(item => item.Id == episode) ?? throw new ProviderException("This episode is unavailable.");
        var sources = new List<MediaSourceInfo>();
        if (found.Video is { } video) sources.Add(Direct(found, feed, video));
        if (found.Audio is { } audio) sources.Add(Direct(found, feed, audio));
        if (sources.Count > 0) return sources;
        // The feed published a pointer rather than a file: YouTube has it, as audio.
        try { return [await YouTubeAsync(auth.UserId, feed, found, ct)]; }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is ProviderException or HttpRequestException
            or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Jellystream podcast YouTube fallback failed ({ErrorType})", ex.GetType().Name);
            throw new ProviderException("This episode is unavailable.");
        }
    }

    // The feed's own address: Jellyfin fetches it itself, so nothing here is relayed or signed.
    private static MediaSourceInfo Direct(PodcastEpisode episode, PodcastFeed feed, PodcastMedia media)
    {
        if (!Uri.TryCreate(media.Url, UriKind.Absolute, out var location)) throw new ProviderException("This episode is unavailable.");
        return new MediaSourceInfo
        {
            Path = location.ToString(), Protocol = MediaProtocol.Http, Container = media.Container, IsRemote = true,
            Name = feed.Title + " · " + media.Container.ToUpperInvariant(),
            SupportsDirectPlay = true, SupportsDirectStream = true, SupportsTranscoding = true,
            RunTimeTicks = episode.Seconds is > 0 ? TimeSpan.FromSeconds(episode.Seconds.Value).Ticks : null
        };
    }

    // yt-dlp and deno are the music channel's tools: Ready follows MusicEnabled, MusicYouTube and
    // MusicAllowLossy as well as this channel's own switch, so all of them must be on to fall back.
    private static bool YouTubeReady => Plugin.Instance?.Configuration is { PodcastYouTube: true } && YtDlp.Ready;

    private async Task<MediaSourceInfo> YouTubeAsync(Guid user, PodcastFeed feed, PodcastEpisode episode, CancellationToken ct)
    {
        if (!YouTubeReady) throw new ProviderException("This episode is unavailable.");
        var (ticket, title) = await TicketAsync(feed, episode, ct);
        static ResolvedVideo Video(AudioTicket t) => new(t.Upstream, "AAC", new Dictionary<string, string>(), false, Ticket: t);
        // A refresh after an expired link asks YouTube again: its addresses last a few hours only.
        return await relay.MediaSourceAsync(user, Video(ticket), "YouTube · " + title,
            async token => [Video((await TicketAsync(feed, episode, token)).Ticket)],
            () => Plugin.Allows(user) && IsAvailable, ct);
    }

    private async Task<(AudioTicket Ticket, string Title)> TicketAsync(PodcastFeed feed, PodcastEpisode episode, CancellationToken ct)
    {
        var deno = YtDlp.DenoFile ?? throw new ProviderException("YouTube needs deno on this server.");
        var query = $"{feed.Title} {episode.Title} podcast";
        var json = await tools.RunAsync(YtDlp.SearchArguments(deno, query), TimeSpan.FromSeconds(25), ct);
        var found = MusicAudio.ParseSearch(json);
        var hit = found.FirstOrDefault(f => f.Duration is > 60) ?? found.FirstOrDefault()
            ?? throw new ProviderException("No recording was found for this episode.");
        var urls = await tools.RunAsync(YtDlp.AudioArguments(deno, hit.Id), TimeSpan.FromSeconds(30), ct);
        if (!Uri.TryCreate(urls.Trim().Split('\n')[0].Trim(), UriKind.Absolute, out var upstream) || !MusicAudio.IsAudioAddress(upstream))
            throw new ProviderException("This episode could not be played.");
        return (new AudioTicket($"podcast-{feed.Id}-{episode.Id}", "youtube", upstream, "m4a"), hit.Title);
    }
}
