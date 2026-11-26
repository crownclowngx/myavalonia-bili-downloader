namespace BiliDownloader.Models;

/// <summary>One authorized playback response. Signed media locations are runtime-only.</summary>
public sealed record BiliPlaybackResult(BiliDashResult? Dash, BiliSegmentedMedia? Segmented = null);

public sealed record BiliSegmentedMedia(
    int QualityId, string QualityName, int VideoCodecId, long DurationMilliseconds, IReadOnlyList<BiliMediaSegment> Segments);

public sealed record BiliMediaSegment(
    int Number, long Length, long DurationMilliseconds, string Url, List<string> BackupUrls);
