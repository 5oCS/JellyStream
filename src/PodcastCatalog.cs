using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellystream;

// The shelf of feeds: whatever the settings list, plus a handful of public shows when the setting allows them.
// Every feed is fetched through the same open-destination check as the parser's own links, in parallel but
// under one rate window, and kept for half an hour so opening the channel is one pass rather than one per show.
public sealed class PodcastCatalog(SafeHttp http, ILogger<PodcastCatalog> logger) : IDisposable
{
    public const int RecentLimit = 60;
    private const int ParallelFeeds = 6;
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(30);

    // Public feeds the channel ships with, so a server with empty settings still has something to play.
    private static readonly (string Name, string Url)[] Defaults =
    [
        ("This American Life", "https://www.thisamericanlife.org/podcast/rss.xml"),
        ("Planet Money", "https://feeds.npr.org/510289/podcast.xml"),
        ("Fresh Air", "https://feeds.npr.org/381444908/podcast.xml"),
        ("Lex Fridman Podcast", "https://lexfridman.com/feed/podcast"),
        ("Podnews", "https://podnews.net/rss"),
    ];

    private readonly FreshCache<PodcastFeed> fresh = new(feed => feed.Episodes.Count > 0 || feed.Title.Length > 0, 200);
    private readonly RateWindow budget = new(20, TimeSpan.FromSeconds(10));

    public static string ShowId(string url) => PodcastIds.Key(url);
    public static bool IsShowId(string id) => PodcastIds.IsShow(id);
    public static bool IsEpisodeId(string id) => PodcastIds.IsEpisode(id);

    // Configured feeds in order, without asking any of them. A feed that is not a public web address is dropped.
    public IReadOnlyList<PodcastSource> Sources()
    {
        var settings = Plugin.Instance?.Configuration;
        if (settings is null) return [];
        var wanted = new List<PodcastSource>();
        foreach (var saved in settings.PodcastFeeds ?? [])
        {
            var text = saved.Trim();
            if (text.Length == 0 || !Uri.TryCreate(text, UriKind.Absolute, out var uri)) continue;
            try { SafeHttp.ValidateOpen(uri); }
            catch (ProviderException) { continue; }
            var address = uri.ToString();
            if (!wanted.Any(source => source.Url == address)) wanted.Add(new PodcastSource(uri.IdnHost, address));
        }
        if (settings.PodcastCurated)
            foreach (var (name, url) in Defaults)
                if (!wanted.Any(source => source.Url == url)) wanted.Add(new PodcastSource(name, url));
        return wanted;
    }

    // The feed's own title once it has been read; the setting or the host's name until then.
    public PodcastFeed? Peek(PodcastSource source) => fresh.TryPeek(source.Url, out var feed) ? feed : null;

    public string DisplayName(PodcastSource source) => Peek(source) is { Title.Length: > 0 } feed ? feed.Title : source.Name;

    // Every feed, fetched a few at a time. One that fails is left out instead of failing the whole shelf.
    public async Task<IReadOnlyList<PodcastFeed>> ShowsAsync(CancellationToken ct)
    {
        var sources = Sources();
        var loaded = new PodcastFeed?[sources.Count];
        using var gate = new SemaphoreSlim(ParallelFeeds, ParallelFeeds);
        await Task.WhenAll(sources.Select(async (source, at) =>
        {
            await gate.WaitAsync(ct);
            try { loaded[at] = await LoadAsync(source, ct); }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or RegexMatchTimeoutException
                or UriFormatException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            { logger.LogWarning("Jellystream podcast feed failed ({ErrorType}) {Url}", ex.GetType().Name, source.Url); }
            finally { gate.Release(); }
        }));
        return loaded.OfType<PodcastFeed>().ToArray();
    }

    // One show by its id. Failures reach the caller, which turns them into a message on the folder.
    public async Task<PodcastFeed?> ShowAsync(string id, CancellationToken ct)
    {
        var source = Sources().FirstOrDefault(item => ShowId(item.Url) == id);
        return source is null ? null : await LoadAsync(source, ct);
    }

    public async Task<IReadOnlyList<PodcastItem>> RecentAsync(CancellationToken ct)
        => (await ShowsAsync(ct))
            .SelectMany(feed => feed.Episodes.Select(episode => new PodcastItem(feed, episode)))
            .OrderByDescending(item => item.Episode.Published ?? DateTimeOffset.MinValue)
            .Take(RecentLimit).ToArray();

    private Task<PodcastFeed> LoadAsync(PodcastSource source, CancellationToken ct)
        => fresh.GetAsync(source.Url, Fresh, token => FetchAsync(source, token), ct);

    private async Task<PodcastFeed> FetchAsync(PodcastSource source, CancellationToken ct)
    {
        // The budget spreads a shelf refresh over a few seconds; when it is full the load goes ahead anyway.
        if (budget.Reserve(DateTime.UtcNow, TimeSpan.FromSeconds(25)) is { } wait && wait > TimeSpan.Zero)
            await Task.Delay(wait, ct);
        using var response = await http.OpenAsync(new Uri(source.Url), false, ct, open: true);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            throw status is >= 400 and < 500
                ? new ProviderException(status is 404 or 410 ? "This podcast feed is no longer published." : "This podcast feed was refused.")
                : new SourceStatusException(status);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return PodcastFeedReader.Read(stream, source.Url, ShowId(source.Url));
    }

    public void Dispose() => fresh.Dispose();
}
