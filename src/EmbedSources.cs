using System.Globalization;

namespace Jellystream;

// One TMDB-keyed embed site: the movie and episode address templates, filled per film.
// Formats come from anikolabs/tmdb-embed-providers (providers.ts); {0} is the TMDB id,
// {1} the season and {2} the episode.
public sealed record EmbedProvider(string Id, string Label, string Movie, string Television)
{
    public Uri Url(Film film, int? season, int? episode)
    {
        if (film.Kind == FilmKind.Tv)
        {
            if (season is not { } s || episode is not { } e)
                throw new ProviderException($"{Label} needs a season and episode number.");
            return new Uri(string.Format(CultureInfo.InvariantCulture, Television, film.Id, s, e));
        }
        return new Uri(string.Format(CultureInfo.InvariantCulture, Movie, film.Id));
    }
}

// Films play from FlixHQ first and, when it fails, from any TMDB-keyed embed site switched on in
// the settings. Every embed page is read as text by FlixSource's player walker; nothing runs.
public sealed class EmbedSources(FlixSource flix)
{
    public static readonly EmbedProvider[] Providers =
    [
        new("vidfast", "VidFast", "https://vidfast.vc/movie/{0}?autoPlay=true", "https://vidfast.vc/tv/{0}/{1}/{2}?autoPlay=true"),
        new("vidlink", "VidLink", "https://vidlink.pro/movie/{0}", "https://vidlink.pro/tv/{0}/{1}/{2}"),
        new("2embed-skin", "2embed.skin", "https://www.2embed.skin/embed/{0}", "https://www.2embed.skin/embedtv/{0}&s={1}&e={2}"),
        new("vidsrc-to", "vidsrc.to", "https://vidsrc.to/embed/movie/{0}", "https://vidsrc.to/embed/tv/{0}/{1}/{2}"),
        new("2embed-cc", "2embed.cc", "https://www.2embed.cc/embed/{0}", "https://www.2embed.cc/embedtv/{0}&s={1}&e={2}"),
        new("nontongo", "nontongo.win", "https://www.nontongo.win/embed/movie/{0}", "https://www.nontongo.win/embed/tv/{0}/{1}/{2}"),
        new("autoembed-co", "autoembed.co", "https://autoembed.co/movie/tmdb/{0}", "https://autoembed.co/tv/tmdb/{0}-{1}-{2}"),
        new("moviesapi-to", "moviesapi.to", "https://moviesapi.to/movie/{0}", "https://moviesapi.to/tv/{0}-{1}-{2}"),
        new("smashystream", "smashystream", "https://player.smashystream.com/playere.php?tmdb={0}",
            "https://player.smashystream.com/playere.php?tmdb={0}&season={1}&episode={2}")
    ];
    public static readonly string[] Ids = [.. Providers.Select(p => p.Id)];
    public static readonly string[] AllIds = ["flix", .. Ids];

    public static bool IsKnown(string source) => source == "flix" || Ids.Contains(source);
    public static string LabelOf(string source) => source == "flix" ? "FlixHQ"
        : Providers.FirstOrDefault(p => p.Id == source)?.Label ?? source;
    public static string[] Ordered(Configuration? config) => Plugin.Ordered(config?.FilmSourceOrder, AllIds);

    // The embed page itself is fetched and read like any FlixHQ player page, with the embed URL as
    // its own referer; the site's subtitles index is merged in when the player linked one.
    public async Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string source, Film film, int? season, int? episode, CancellationToken ct)
    {
        var provider = Providers.FirstOrDefault(p => p.Id == source)
            ?? throw new ProviderException("Unknown movie source " + source + ".");
        var url = provider.Url(film, season, episode);
        var (video, problems) = await flix.PlayerAsync(url, url, 0, ct);
        if (video is null)
        {
            // Say why the walk found nothing, so a source that stops working names its own cause
            // instead of leaving the viewer with a bare complaint. The deepest failure comes first.
            var reason = problems.Count == 0 ? "" : " (" + problems[0] + ")";
            throw new ProviderException(provider.Label + " returned no playable video" + reason + ".");
        }
        var known = video.Subtitles!.Select(s => s.Url).ToHashSet();
        var extra = (await flix.SubtitleIndexAsync(url, url, ct)).Where(s => !known.Contains(s.Url));
        return [video with { Subtitles = Languages.Arrange(video.Subtitles!.Concat(extra)) }];
    }
}
