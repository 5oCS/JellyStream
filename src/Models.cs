namespace Jellystream;

using MediaBrowser.Model.Entities;

public sealed record Anime(int Id, string Title, string[] Aliases, int? Year, string Overview, string? Image, int? Duration, bool IsAdult, string[] Genres,
    int? MalId = null, int? AvailableEpisodes = null);
public sealed record CatalogPage(IReadOnlyList<Anime> Items, bool HasNext);
public sealed record SourceTitle(string Id, string Title, int? Year = null);
public sealed record SourceEpisode(string Id, decimal Number, string Title);
public sealed record Subtitle(string Label, string Language, Uri Url);
public sealed record ResolvedVideo(Uri Url, string Quality, IReadOnlyDictionary<string, string> Headers, bool HardcodedEnglishSubtitles,
    bool Hls = false, IReadOnlyList<Subtitle>? Subtitles = null, AudioTicket? Ticket = null)
{
    public bool Audio => Ticket is not null;
}

public interface IAnimeSource
{
    string Id { get; }
    string Name { get; }
    Task<IReadOnlyList<SourceTitle>> SearchAsync(string title, CancellationToken ct);
    Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string seriesId, Anime anime, CancellationToken ct);
    Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string episodeId, CancellationToken ct);
}

public interface IDirectAnimeSource : IAnimeSource
{
    SourceTitle? Direct(Anime anime);
}

public class ProviderException(string message) : Exception(message);
public sealed class RateLimitedException(string message, TimeSpan? retryAfter) : ProviderException(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
public sealed class CatalogDownException() : ProviderException("The music catalog is not responding right now. Songs played recently still work; try again in a few minutes.");
// A source that answered with an error status; 5xx means the site itself is down or overloaded.
public sealed class SourceStatusException(int status) : ProviderException($"Source returned HTTP {status}. Please try again later.")
{
    public int Status { get; } = status;
}

// The streams of a file a source publishes itself, which Jellyfin fetches in place. Jellyfin picks a
// stream to play and refuses the request when there is none, so such a source has to say what it
// carries; without this it answers "the audio stream is null" and playback stops at once.
public static class SourceStreams
{
    public static MediaStream Audio(string container) => new()
    {
        Type = MediaStreamType.Audio, Index = 0, Codec = AudioCodec(container),
        Channels = 2, ChannelLayout = "stereo", IsDefault = true
    };

    public static MediaStream Video(string container) => new()
    {
        Type = MediaStreamType.Video, Index = 0, Codec = VideoCodec(container), IsDefault = true
    };

    // The codec of a container as Jellyfin names it. A container we do not know is taken as the
    // common one for its kind, which is what a podcast feed most often publishes.
    private static string AudioCodec(string container) => container.ToLowerInvariant() switch
    {
        "m4a" or "m4b" or "aac" => "aac",
        "ogg" or "oga" => "vorbis",
        "opus" => "opus",
        "flac" => "flac",
        "wav" => "wav",
        _ => "mp3"
    };

    private static string VideoCodec(string container) => container.ToLowerInvariant() switch
    {
        "webm" => "vp9",
        _ => "h264"
    };
}
