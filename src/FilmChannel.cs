using Microsoft.Extensions.Caching.Memory;
using System.Globalization;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellystream;

// Item IDs: movie/<scope>/<tmdb>, tv/<scope>/<tmdb>, season/<scope>/<tmdb>/<n>, ep/<scope>/<tmdb>/<s>/<e>.
// The scope (listing name) keeps one title from being moved between folders by Jellyfin;
// the TMDB provider ID lets watch progress follow the title across listings.
public sealed class FilmChannel(TmdbCatalog catalog, FlixSource flix, EmbedSources embeds, StreamRelay relay, IHttpContextAccessor contexts,
    IAuthorizationContext authorization, ILibraryManager library, ILogger<FilmChannel> logger, VideoHealth health)
    : IChannel, IHasCacheKey, IRequiresMediaInfoCallback
{
    public const string ChannelName = "Movies & TV";
    public static bool IsAvailable => Plugin.Instance?.Configuration is { Enabled: true, FilmEnabled: true } && TmdbCatalog.HasKey;
    // Whether the settings let this film source play: the film channel is on, the name is one of
    // the shipped sources and its switch in the settings is ticked.
    public static bool IsSourceEnabled(string source) => IsAvailable && Plugin.Instance?.Configuration is { } config
        && EmbedSources.IsKnown(source) && !config.FilmSourceOff.Contains(source);
    public string Name => ChannelName;
    public string Description => "Jellystream · Browse movies and TV and stream them online.";
    public string DataVersion => "jellystream-film-2-" + IsAvailable + "-" + (Plugin.Instance?.Configuration.CacheRevision ?? 0);
    public string HomePageUrl => "https://github.com/5oCS/JellyStream";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId) => IsAvailable && Guid.TryParse(userId, out var id) && Plugin.Allows(id);
    public string GetCacheKey(string? userId) => $"{userId}-{DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440)).Ticks}";
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video], ContentTypes = [ChannelMediaContentType.Movie, ChannelMediaContentType.Episode],
        AutoRefreshLevels = 0, SupportsContentDownloading = false
    };
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) => Task.FromResult(new DynamicImageResponse
    {
        HasImage = type == ImageType.Primary, Format = ImageFormat.Png,
        Stream = typeof(FilmChannel).Assembly.GetManifestResourceStream("Jellystream.Assets.popcorn.png")
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
    public static Dictionary<string, string> Ids(Film film) => new() { ["Tmdb"] = film.Id.ToString(CultureInfo.InvariantCulture), ["Custom"] = $"jellystream-{film.Kind.ToString().ToLowerInvariant()}-{film.Id}" };

    public static ChannelItemInfo Item(Film film, string scope)
    {
        if (film.Kind == FilmKind.Movie)
            return new ChannelItemInfo
            {
                Id = $"movie/{scope}/{film.Id}", Name = film.Title, Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Movie, ImageUrl = film.Image, Overview = film.Overview, ProductionYear = film.Year,
                RunTimeTicks = film.Runtime is > 0 ? TimeSpan.FromMinutes(film.Runtime.Value).Ticks : null, Tags = ["Jellystream"], ProviderIds = Ids(film)
            };
        var item = Folder($"tv/{scope}/{film.Id}", film.Title, film.Overview);
        item.FolderType = ChannelFolderType.Series;
        item.ImageUrl = film.Image;
        item.ProductionYear = film.Year;
        item.ProviderIds = Ids(film);
        return item;
    }

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        if (query.UserId != Guid.Empty && !Plugin.Allows(query.UserId)) throw new UnauthorizedAccessException("Jellystream is not enabled for this account.");
        if (!IsAvailable) return Result([]);
        var id = query.FolderId ?? "";
        try { return await BrowseAsync(id, ct); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Jellystream film catalog request failed ({ErrorType})", ex.GetType().Name);
            var message = ex is ProviderException ? ex.Message : "The catalog is temporarily unavailable. Please try again later.";
            return Result([Folder(id + "/status", message, message)]);
        }
    }

    private async Task<ChannelItemResult> BrowseAsync(string id, CancellationToken ct)
    {
        if (id.EndsWith("/status", StringComparison.Ordinal)) return Result([]);
        if (id.Length == 0) return Result([
            Folder("search", "Search results", "Use Jellyfin's search (magnifying glass) to find movies and shows online. Your discovered titles appear here."),
            Folder("list/movies/1", "Popular movies"), Folder("list/tv/1", "Popular TV"), Folder("list/trending/1", "Trending this week")]);
        var parts = id.Split('/');
        if (id == "search")
        {
            var parent = library.GetNewItemId(FilmSearch.FolderKey, typeof(Folder));
            return Result(library.GetItemList(new InternalItemsQuery { ParentId = parent, Recursive = false }).Select(item => item switch
            {
                Movie movie => new ChannelItemInfo
                {
                    Id = movie.ExternalId, Name = movie.Name, Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video,
                    ContentType = ChannelMediaContentType.Movie, Overview = movie.Overview, ProductionYear = movie.ProductionYear,
                    RunTimeTicks = movie.RunTimeTicks, Tags = movie.Tags.ToList(), ProviderIds = movie.ProviderIds
                },
                _ => new ChannelItemInfo
                {
                    Id = item.ExternalId, Name = item.Name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Series,
                    Overview = item.Overview, ProductionYear = item.ProductionYear, Tags = item.Tags.ToList(), ProviderIds = item.ProviderIds
                }
            }));
        }
        if (parts is ["list", var category, var pageText] && int.TryParse(pageText, out var page))
        {
            // TMDB hands out 20 titles a call; a folder shows five calls' worth, loaded together.
            var data = await Pages.LoadAsync(page, 5, 500, number => catalog.BrowseAsync(category, number, ct), d => d.Items, d => d.HasNext, f => (f.Kind, f.Id));
            var items = data.Items.Select((film, index) => { var item = Item(film, category); item.IndexNumber = index + 1; return item; }).ToList();
            if (data.Next is { } next) items.Add(Folder($"list/{category}/{next}", $"More ({(next - 1) / 5 + 1})"));
            return Result(items);
        }
        if (parts is ["tv", var scope, var showText] && int.TryParse(showText, out var showId))
        {
            var show = await catalog.GetAsync(FilmKind.Tv, showId, ct);
            return Result((await catalog.SeasonsAsync(showId, ct)).Select(season =>
            {
                var item = Folder($"season/{scope}/{showId}/{season.Number}", season.Name, $"{season.Episodes} episodes");
                item.FolderType = ChannelFolderType.Season;
                item.IndexNumber = season.Number;
                item.ImageUrl = season.Image ?? show.Image;
                return item;
            }));
        }
        if (parts is ["season", var seasonScope, var seriesText, var seasonText] && int.TryParse(seriesText, out var seriesId) && int.TryParse(seasonText, out var seasonNumber))
        {
            var show = await catalog.GetAsync(FilmKind.Tv, seriesId, ct);
            return Result((await catalog.EpisodesAsync(seriesId, seasonNumber, ct)).Select(episode => new ChannelItemInfo
            {
                Id = $"ep/{seasonScope}/{seriesId}/{seasonNumber}/{episode.Number}",
                Name = episode.Name, SeriesName = show.Title, Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Episode, ImageUrl = episode.Image ?? show.Image, Overview = episode.Overview,
                IndexNumber = episode.Number, ParentIndexNumber = seasonNumber, ProductionYear = episode.Aired?.Year, PremiereDate = episode.Aired,
                RunTimeTicks = (episode.Runtime ?? show.Runtime) is int minutes && minutes > 0 ? TimeSpan.FromMinutes(minutes).Ticks : null,
                Tags = ["Jellystream"], ProviderIds = new Dictionary<string, string> { ["Custom"] = $"jellystream-ep-{seriesId}-{seasonNumber}-{episode.Number}" }
            }));
        }
        return Result([]);
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        var context = contexts.HttpContext;
        if (context is null) return [];
        var auth = await authorization.GetAuthorizationInfo(context);
        if (!auth.IsAuthenticated || !Plugin.Allows(auth.UserId) || !IsAvailable) throw new UnauthorizedAccessException("Jellystream is not enabled for this account.");
        (Film Film, int? Season, int? Episode) target = id.Split('/') switch
        {
            ["movie", _, var movie] when int.TryParse(movie, out var movieId) => (await catalog.GetAsync(FilmKind.Movie, movieId, ct), null, null),
            ["ep", _, var show, var s, var e] when int.TryParse(show, out var showId) && int.TryParse(s, out var season) && int.TryParse(e, out var episode)
                && season is > 0 and < 1000 && episode is > 0 and < 10000 => (await catalog.GetAsync(FilmKind.Tv, showId, ct), season, episode),
            _ => throw new ProviderException("Invalid Jellystream title.")
        };
        var user = auth.UserId;
        var key = user.ToString("N") + id;
        // Only a real start prepares the next episode, not opening an episode's page.
        var starting = context.Request.Path.Value?.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) == true;
        if (AnimeChannel.PreparesNext && Ready.TryGetValue<MediaSourceInfo>(key, out var ready) && relay.Holds(ready!.Path))
        {
            if (starting) PrepareNext(user, id, target.Film, target.Season, target.Episode);
            return [ready];
        }
        var source = await PlayAsync(user, target.Film, target.Season, target.Episode, ct);
        if (starting) PrepareNext(user, id, target.Film, target.Season, target.Episode);
        return [source];
    }

    // The next episode of a show, found and probed while one plays, as for anime: per user, kept half an hour.
    private static readonly MemoryCache Ready = new(new MemoryCacheOptions { SizeLimit = 200 });
    private static readonly SemaphoreSlim Preparing = new(1, 1);

    // The sources that may play this film, best first: the saved order picks the first source
    // health has not taken down, the rest are tried fastest first, as the anime channel does.
    private string[] Candidates()
    {
        var enabled = EmbedSources.Ordered(Plugin.Instance?.Configuration).Where(IsSourceEnabled).ToArray();
        if (enabled.Length == 0) throw new ProviderException("No movie source is switched on in the Jellystream settings.");
        var first = enabled.FirstOrDefault(health.Usable) ?? enabled[0];
        return [first, .. health.Fastest(enabled.Where(id => id != first), id => id)];
    }

    private Task<IReadOnlyList<ResolvedVideo>> ResolveSourceAsync(string source, Film film, int? season, int? episode, CancellationToken ct)
        => source == "flix" ? flix.ResolveAsync(film, season, episode, ct) : embeds.ResolveAsync(source, film, season, episode, ct);

    // A source that never answers, errors or hands back nothing playable can be fallen back from;
    // the caller giving up is not such a failure.
    private static bool IsSiteFailure(Exception ex) => ex is HttpRequestException or IOException or OperationCanceledException or ProviderException;

    private async Task<MediaSourceInfo> PlayAsync(Guid user, Film film, int? season, int? episode, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var candidates = Candidates();
        var failures = new List<string>();
        string? chosen = null;
        IReadOnlyList<ResolvedVideo> videos = [];
        if (candidates.Length > 1)
        {
            // One source hanging must not hold the film: a minute for all of them together,
            // thirty seconds for each one in turn.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(60));
            foreach (var candidate in candidates)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    var found = await ResolveSourceAsync(candidate, film, season, episode, attempt.Token);
                    if (found.Count == 0)
                    {
                        failures.Add(VideoHealth.Label(candidate) + ": no playable video was returned.");
                        continue;
                    }
                    videos = found;
                    chosen = candidate;
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && IsSiteFailure(ex))
                {
                    health.Failed(candidate, ex, budget.Token);
                    failures.Add(VideoHealth.Label(candidate) + ": " + (ex is ProviderException ? ex.Message : "not answering"));
                }
            }
            ct.ThrowIfCancellationRequested();
        }
        else
        {
            chosen = candidates[0];
            try
            {
                videos = await ResolveSourceAsync(chosen, film, season, episode, ct);
                if (videos.Count == 0) throw new ProviderException("No playable video was returned.");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsSiteFailure(ex))
            {
                health.Failed(chosen, ex, ct);
                throw;
            }
        }
        if (chosen is null || videos.Count == 0)
            throw new ProviderException("No source could play this title right now ("
                + string.Join(", ", failures.Distinct().Take(6)) + "). Please try again later.");
        var played = chosen;
        var first = videos[0];
        var subtitles = first.Subtitles?.Count ?? 0;
        var source = await relay.MediaSourceAsync(user, first, $"{VideoHealth.Label(played)} · {first.Quality}" + (subtitles > 0 ? $" · {subtitles} subtitle tracks" : ""),
            token => ResolveSourceAsync(played, film, season, episode, token), () => Plugin.Allows(user) && IsAvailable, ct);
        health.Started(played, clock.Elapsed);
        return source;
    }

    private void PrepareNext(Guid user, string id, Film film, int? season, int? episode)
    {
        if (!AnimeChannel.PreparesNext || season is not { } s || episode is not { } e || id.Split('/') is not ["ep", var scope, var show, _, _]) return;
        _ = Task.Run(async () =>
        {
            if (!await Preparing.WaitAsync(0)) return;
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                // The next in this season, else the first of the next season.
                var (nextSeason, nextEpisode) = (await catalog.EpisodesAsync(film.Id, s, limit.Token)).Any(x => x.Number == e + 1) ? (s, e + 1)
                    : (await catalog.SeasonsAsync(film.Id, limit.Token)).Any(x => x.Number == s + 1) ? (s + 1, 1) : (0, 0);
                if (nextSeason == 0) return;
                var key = user.ToString("N") + $"ep/{scope}/{show}/{nextSeason}/{nextEpisode}";
                if (Ready.TryGetValue(key, out _)) return;
                var prepared = await PlayAsync(user, film, nextSeason, nextEpisode, limit.Token);
                Ready.Set(key, prepared, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) });
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or OperationCanceledException or IOException
                or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException)
            {
                logger.LogInformation("Jellystream films: the next episode could not be got ready ({ErrorType})", ex.GetType().Name);
            }
            finally { Preparing.Release(); }
        }, CancellationToken.None);
    }
}
