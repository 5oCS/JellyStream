using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellystream;

public sealed class Configuration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public string[] AllowedUserIds { get; set; } = [];
    // HiAnime. The name and the "zoko" source id are kept so saved settings and episode IDs survive.
    public bool ZokoEnabled { get; set; } = true;
    public bool AnimeGgEnabled { get; set; } = true;
    public bool AnimeHeavenEnabled { get; set; } = true;
    public bool AnimeProviderFallback { get; set; } = true;
    public bool PrepareNextEpisode { get; set; } = true;
    public bool HideAdultContent { get; set; } = true;
    public bool HideEcchiContent { get; set; } = true;
    public int MetadataCacheMinutes { get; set; } = 60;
    public bool FilmEnabled { get; set; } = true;
    public string TmdbApiKey { get; set; } = "";
    // Where films play from: the first switched-on source plays; when it fails or is slow the next
    // one is tried, in this order. Unknown names are dropped and missing ones go last, so a source
    // added in a later version joins at the end.
    public string[] FilmSourceOrder { get; set; } = [];
    // Sources switched off in the settings; the order and these switches together decide who plays.
    public string[] FilmSourceOff { get; set; } = [];
    // A self-hosted Dispatcharr to play films from. Empty keeps it out of the source list entirely.
    // The address is the one destination SafeHttp allows to be a private one — see SafeHttp.Origin.
    public string DispatcharrUrl { get; set; } = "";
    // X-API-Key, sent only to the address above and never to a redirect, a log or a status line.
    public string DispatcharrApiKey { get; set; } = "";
    public bool MusicEnabled { get; set; } = true;
    // Empty means MusicCatalog.DefaultApi.
    public string MusicApiUrl { get; set; } = "";
    public bool MusicPreferExplicit { get; set; } = true;
    // Off: no song plays from Monochrome's audio host; its catalog still answers searches, albums and artists.
    public bool MusicMonochrome { get; set; } = true;
    // Where songs play from and where searches go first: the first that works wins. Unknown names are dropped and
    // missing ones go last, so a source added in a later version joins at the end.
    public string[] MusicPlayOrder { get; set; } = ["monochrome", "youtube"];
    public string[] MusicSearchOrder { get; set; } = ["monochrome", "deezer"];
    // While a song plays, the next one is fetched into the song cache.
    public bool MusicPrefetch { get; set; } = true;
    // Deezer's public catalog answers searches, albums and artists while the music catalog cannot.
    public bool MusicDeezer { get; set; } = true;
    // Songs kept on disk so skips and replays do not go back to the catalog; 0 turns it off.
    public int MusicCacheMb { get; set; } = 4096;
    // When no FLAC source answers, a song may play from a lossy one (YouTube's AAC) instead of failing.
    public bool MusicAllowLossy { get; set; } = true;
    // YouTube needs yt-dlp and deno on the server; this setting lets the plugin fetch and update them.
    public bool MusicYouTube { get; set; }
    // Empty means the copies the plugin keeps in its own data folder.
    public string YtDlpPath { get; set; } = "";
    public string DenoPath { get; set; } = "";
    public string YouTubeProxy { get; set; } = "";
    // Audiobooks: LibriVox's shelf with the Internet Archive's search; a chapter plays from the source's own address.
    public bool AudiobookEnabled { get; set; } = true;
    // When neither catalog can answer, a chapter may play from YouTube instead. yt-dlp and deno are the music
    // YouTube tools above (YtDlp.Ready follows MusicEnabled, MusicYouTube and MusicAllowLossy), so those stay needed.
    public bool AudiobookYouTube { get; set; }
    // Podcasts: RSS feeds read as one shelf of shows, each episode playing from the feed's own file.
    public bool PodcastEnabled { get; set; } = true;
    // Feeds this server follows. Any public web address that serves an RSS or Atom feed.
    public string[] PodcastFeeds { get; set; } = [];
    // Also follow the public feeds the plugin ships with, so an empty list still has something to play.
    public bool PodcastCurated { get; set; } = true;
    // When a feed publishes no file of its own, an episode may play from YouTube instead. yt-dlp and deno are
    // the music YouTube tools above (YtDlp.Ready follows MusicEnabled, MusicYouTube and MusicAllowLossy).
    public bool PodcastYouTube { get; set; }
    // A folder of EPUBs and PDFs indexed as books; empty turns it off. This is the offline shelf:
    // nothing is fetched, and the folder is only ever read.
    public string BookFolder { get; set; } = "";

    // Which book catalogues are searched for a term, and in what order. The order is the whole
    // point: an operator who prefers one catalogue puts it first and gets its rows first.
    public string[] BookSourceOrder { get; set; } = [];
    // Starred albums are saved here as FLAC files for a normal music library; empty turns it off.
    public string MusicLibraryFolder { get; set; } = "";
    // Optional: Last.fm's similar artists when ListenBrainz has none.
    public string LastFmApiKey { get; set; } = "";
    public bool SubsonicEnabled { get; set; }
    public bool SubsonicPreferLossless { get; set; } = true;
    public SubsonicPassword[] SubsonicPasswords { get; set; } = [];
    // Incrementing this invalidates Jellyfin's own channel cache as well as ours.
    public int CacheRevision { get; set; }
}

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }
    public static readonly Guid PluginId = new("06423df8-a0cd-4ab9-a450-febb5ecb9133");
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override Guid Id => PluginId;
    public override string Name => "Jellystream";
    public override string Description => "Anime, movies, TV and lossless music from online sources, as Jellyfin channels.";
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "jellystream", EmbeddedResourcePath = "Jellystream.Configuration.html" }];
    public static string[] Ordered(string[]? saved, params string[] known) => (saved ?? []).Where(s => known.Contains(s)).Concat(known).Distinct().ToArray();
    public static bool Allows(Guid id) => id != Guid.Empty && Instance?.Configuration is { Enabled: true } c
        && c.AllowedUserIds.Any(value => Guid.TryParse(value, out var allowed) && allowed == id);
}
