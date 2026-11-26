using System.Collections.Concurrent;
using BiliDownloader.Models;
using BiliDownloader.Services.Api;
using BiliDownloader.Services.Auth;
using BiliDownloader.Services.ContentSources;

namespace BiliDownloader.Services.Download;

/// <summary>批量选择区域可以安全展示的高规格交集，不包含任何播放地址或身份信息。</summary>
public sealed record BatchMediaCapabilitySnapshot(
    int ItemCount,
    IReadOnlyDictionary<MediaFeatureFlags, MediaCapabilityAvailability> Availability,
    IReadOnlyDictionary<MediaFeatureFlags, int> AvailableCounts,
    int? RequestedQualityId = null,
    IReadOnlyList<BiliQualityOption>? VideoQualities = null)
{
    public MediaCapabilityAvailability GetAvailability(MediaFeatureFlags feature)
        => Availability.TryGetValue(feature, out var value) ? value : MediaCapabilityAvailability.Unknown;
}

/// <summary>
/// 当前 Document 会话中的批量能力探测边界。接口单独存在，使工作区只依赖“能力交集”而不是 B 站 API。
/// </summary>
public interface IMediaCapabilityInspectionService
{
    Task<BatchMediaCapabilitySnapshot> InspectAsync(
        IReadOnlyCollection<BiliVideoItem> items,
        int qualityId,
        CancellationToken cancellationToken = default);

    /// <summary>解析新来源或登录态变化后清空会话缓存，避免复用旧权限事实。</summary>
    void Clear();
}

/// <summary>
/// 最多四路并发的能力探测器。单项 DASH 结果只在方法栈内存在，缓存仅保存脱敏后的能力快照；
/// 因而既减少批量切换时的重复请求，也不会延长签名 URL 的生命周期。
/// </summary>
public sealed class MediaCapabilityInspectionService(
    IBiliMediaProbe mediaProbe,
    IBiliCredentialProvider credentials) : IMediaCapabilityInspectionService
{
    private static readonly VideoCodec[] StandardCodecs = [VideoCodec.Avc, VideoCodec.Hevc, VideoCodec.Av1];
    private static readonly MediaFeatureFlags[] Features =
    [
        MediaFeatureFlags.Hdr,
        MediaFeatureFlags.DolbyVision,
        MediaFeatureFlags.HiResAudio,
        MediaFeatureFlags.DolbyAtmos,
    ];

    private readonly ConcurrentDictionary<ProbeKey, Lazy<Task<ItemSnapshot>>> _cache = new();
    private readonly SemaphoreSlim _concurrency = new(4, 4);

    public async Task<BatchMediaCapabilitySnapshot> InspectAsync(
        IReadOnlyCollection<BiliVideoItem> items,
        int qualityId,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return new(0,
                Features.ToDictionary(feature => feature, _ => MediaCapabilityAvailability.Unknown),
                Features.ToDictionary(feature => feature, _ => 0));

        var tasks = items.Select(item => GetSnapshotAsync(item, qualityId, cancellationToken)).ToArray();
        var snapshots = await Task.WhenAll(tasks);
        var availability = new Dictionary<MediaFeatureFlags, MediaCapabilityAvailability>();
        var counts = new Dictionary<MediaFeatureFlags, int>();
        foreach (var feature in Features)
        {
            var states = snapshots.Select(snapshot => snapshot.Capabilities.GetAvailability(feature)).ToArray();
            counts[feature] = states.Count(state => state == MediaCapabilityAvailability.Available);
            availability[feature] = Aggregate(states);
        }
        var qualities = snapshots.SelectMany(snapshot => snapshot.Qualities)
            .GroupBy(option => option.QualityId)
            .OrderByDescending(group => group.Key)
            .Select(group => new BiliQualityOption
            {
                QualityId = group.Key,
                DisplayName = group.First().DisplayName,
                IsAvailable = group.Count(option => option.IsAvailable == true) == items.Count
                    ? true
                    : group.Key == qualityId ? false : null,
                AvailableCodecs = group.Count() == items.Count
                    ? StandardCodecs.Where(codec => group.All(option => option.AvailableCodecs?.Contains(codec) == true)).ToHashSet()
                    : null,
            })
            .ToList();
        if (qualityId is > 0 and not (125 or 126) && qualities.All(option => option.QualityId != qualityId))
            qualities.Add(new BiliQualityOption { QualityId = qualityId, DisplayName = $"画质 {qualityId}", IsAvailable = false });
        return new(items.Count, availability, counts, qualityId, qualities);
    }

    public void Clear() => _cache.Clear();

    private async Task<ItemSnapshot> GetSnapshotAsync(
        BiliVideoItem item,
        int qualityId,
        CancellationToken cancellationToken)
    {
        var key = new ProbeKey(item.Aid, item.Cid, item.MediaType, item.EpId, item.SeasonId, qualityId);
        var lazy = _cache.GetOrAdd(key, _ => new Lazy<Task<ItemSnapshot>>(
            () => ProbeCoreAsync(item, qualityId, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch
        {
            // 失败或取消不能污染当前会话缓存，否则后续重新登录也只能得到同一个失败 Task。
            _cache.TryRemove(new KeyValuePair<ProbeKey, Lazy<Task<ItemSnapshot>>>(key, lazy));
            throw;
        }
    }

    private async Task<ItemSnapshot> ProbeCoreAsync(
        BiliVideoItem item,
        int qualityId,
        CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            var playback = await mediaProbe.GetPlaybackAsync(
                item.Aid, item.Cid, qualityId, credentials.GetCookieHeader(),
                item.MediaType, item.EpId, item.SeasonId, cancellationToken);
            return new ItemSnapshot(playback.Dash?.Capabilities ?? MediaCapabilitySnapshot.Unknown,
                VideoParseResultFactory.GetVideoQualityOptions(playback));
        }
        catch (MediaAuthorizationException)
        {
            return new ItemSnapshot(MediaCapabilitySnapshot.Unknown, []);
        }
        catch (ResourceUnavailableException)
        {
            return new ItemSnapshot(MediaCapabilitySnapshot.Unknown, []);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private static MediaCapabilityAvailability Aggregate(MediaCapabilityAvailability[] states)
    {
        if (states.All(state => state == MediaCapabilityAvailability.Available))
            return MediaCapabilityAvailability.Available;
        if (states.Contains(MediaCapabilityAvailability.Unavailable))
            return MediaCapabilityAvailability.Unavailable;
        if (states.Contains(MediaCapabilityAvailability.RequiresPremium))
            return MediaCapabilityAvailability.RequiresPremium;
        if (states.Contains(MediaCapabilityAvailability.RequiresLogin))
            return MediaCapabilityAvailability.RequiresLogin;
        return MediaCapabilityAvailability.Unknown;
    }

    private sealed record ItemSnapshot(MediaCapabilitySnapshot Capabilities, IReadOnlyList<BiliQualityOption> Qualities);

    private readonly record struct ProbeKey(
        long Aid,
        long Cid,
        BiliMediaType MediaType,
        long EpId,
        long SeasonId,
        int QualityId);
}
