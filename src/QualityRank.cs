using System.Text.RegularExpressions;

namespace Jellystream;

// Site labels name quality in every format the sources use ("720p", "720p60", "up to 1080p",
// "4K", or a transport like "HLS" that names no height at all): the resolver and the quality
// selector both need one comparable number, so every label maps to a height in pixels and a
// label that claims no height ranks last rather than first.
public static class QualityRank
{
    private static readonly Regex Height = new(@"(\d{3,4})", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // The height the label claims, or 0 when it claims none — 0 ranks last, never first.
    public static int Score(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return 0;
        if (label.Contains("4K", StringComparison.OrdinalIgnoreCase)) return 2160;
        if (label.Contains("8K", StringComparison.OrdinalIgnoreCase)) return 4320;
        var match = Height.Match(label);
        if (match.Success && int.TryParse(match.Value, out var pixels)) return pixels;
        if (label.Contains("FHD", StringComparison.OrdinalIgnoreCase)) return 1080;
        if (label.Contains("HD", StringComparison.OrdinalIgnoreCase)) return 720;
        if (label.Contains("SD", StringComparison.OrdinalIgnoreCase)) return 480;
        return 0;
    }

    // The variant to play first: the tallest claim wins, and equal claims keep source order.
    public static ResolvedVideo Best(IReadOnlyList<ResolvedVideo> videos) =>
        videos.OrderByDescending(video => Score(video.Quality)).First();

    // Whether the name already carries a real height. A source that answered "HLS" or "MP4" has named
    // its transport, not its picture, and such a name is worth correcting from the probe.
    public static bool Rated(string name) => Score(Segment(name)) > 0;

    // The quality segment of a display name built as "{label} · {quality}[ · {extra}]": the middle
    // segment when there is one, the last when there are only two, and the whole name otherwise.
    private static string Segment(string name) => name.Split(" · ") switch
    {
        [_, var quality, ..] => quality,
        [var only] => only,
        _ => name
    };

    // Replaces the quality segment with the probed height and keeps any trailing segment, so
    // "Flix · HLS · 2 subtitle tracks" becomes "Flix · 720p · 2 subtitle tracks".
    public static string Relabel(string name, int height) => name.Split(" · ") switch
    {
        [var label, .. var rest] => string.Join(" · ", new[] { label, height + "p" }.Concat(rest.Skip(1))),
        _ => name
    };
}
