using BiliDownloader.Models;

namespace BiliDownloader.Services.Download;

internal static class BiliSegmentedMediaPolicy
{
    public static bool DurationsMatch(long expected, long actual, int roundingAllowance = 0)
        => expected > 0 && actual > 0
            && Math.Abs(expected - actual) <= Math.Clamp(expected / 100, 250, 2000) + roundingAllowance;

    public static List<BiliQualityOption> GetQualities(BiliSegmentedMedia media)
    {
        var codec = GetVideoCodec(media);
        return [new BiliQualityOption
        {
            QualityId = media.QualityId,
            DisplayName = media.QualityName,
            AvailableCodecs = codec == VideoCodec.Unknown ? new HashSet<VideoCodec>() : new HashSet<VideoCodec> { codec },
        }];
    }

    public static MediaSelectionResult Select(
        IMediaStreamSelectionPolicy dashPolicy, BiliPlaybackResult playback, MediaSelectionRequest request, int expectedDuration)
    {
        if (playback.Segmented is not { } media)
            return dashPolicy.Select(playback.Dash!, request);
        // Catalog duration is stored in whole seconds; playback and ffprobe durations have finer precision.
        if (expectedDuration > 0 && !DurationsMatch(expectedDuration * 1000L, media.DurationMilliseconds, roundingAllowance: 999))
            return MediaSelectionResult.Failed(MediaSelectionFailureCode.VideoStreamUnavailable, "分段流与完整媒体时长不一致。");
        if (request.OutputMediaMode == OutputMediaMode.AudioOnly
            || request.OutputContainer is not (OutputContainer.Mp4 or OutputContainer.Mkv)
            || (request.OutputMediaMode == OutputMediaMode.AudioVideo && request.AudioQualityId > 0))
            return MediaSelectionResult.Failed(MediaSelectionFailureCode.InvalidOutputCombination,
                "分段媒体支持 MP4 或 MKV 的音视频和仅视频输出。请将音频质量设为自动。");
        if (request.VideoDynamicRangePreference is not (VideoDynamicRangePreference.Auto or VideoDynamicRangePreference.Standard)
            || (request.OutputMediaMode == OutputMediaMode.AudioVideo
                && request.AudioFeaturePreference is not (AudioFeaturePreference.Auto or AudioFeaturePreference.Standard)))
            return MediaSelectionResult.Failed(MediaSelectionFailureCode.ExplicitMediaFeatureUnavailable,
                "分段媒体未提供可确认的 HDR、杜比或无损音轨。");
        if (request.VideoQualityId != media.QualityId)
            return MediaSelectionResult.Failed(MediaSelectionFailureCode.VideoStreamUnavailable,
                $"当前仅获取到 {media.QualityName} 的分段流。");
        var codec = GetVideoCodec(media);
        var requested = request.VideoCodecPreference switch
        {
            VideoCodecPreference.Avc => VideoCodec.Avc,
            VideoCodecPreference.Hevc => VideoCodec.Hevc,
            VideoCodecPreference.Av1 => VideoCodec.Av1,
            _ => VideoCodec.Unknown,
        };
        if (requested != VideoCodec.Unknown && requested != codec)
            return MediaSelectionResult.Failed(MediaSelectionFailureCode.ExplicitVideoCodecUnavailable,
                "分段媒体无法确认符合所选编码。请选择自动或已确认的编码。");

        var output = new MediaOutputPlan(codec, AudioCodec.Unknown, request.OutputContainer, request.OutputMediaMode,
            request.OutputContainer == OutputContainer.Mp4 ? ".mp4" : ".mkv", 0, 0,
            SegmentedInputBytes: media.Segments.Sum(segment => segment.Length));
        return new(true, null, null, output, MediaSelectionFailureCode.None, "",
            codec == VideoCodec.Unknown ? [] : [codec]);
    }

    private static VideoCodec GetVideoCodec(BiliSegmentedMedia media) => media.VideoCodecId switch
    {
        7 => VideoCodec.Avc,
        12 => VideoCodec.Hevc,
        13 => VideoCodec.Av1,
        _ => VideoCodec.Unknown,
    };
}
