using BiliDownloader.Models;
using BiliDownloader.Models.ContentSources;
using BiliDownloader.Services.Api;
using BiliDownloader.Services.Auth;
using BiliDownloader.Services.Download;
using BiliDownloader.ViewModels.BiliDownloader;

namespace BiliDownloader.Services.ContentSources;

/// <summary>
/// 将一个或多个来源解析结果合并为现有下载配置可消费的模型。
/// 设计意图：链接解析和个人来源浏览汇入同一出口，后续提交链路无需识别来源类型。
/// </summary>
public sealed class VideoParseResultFactory
{
    private readonly IBiliMediaProbe _mediaProbe;
    private readonly IBiliCredentialProvider _credentials;

    public VideoParseResultFactory(IBiliMediaProbe mediaProbe, IBiliCredentialProvider credentials)
    {
        _mediaProbe = mediaProbe;
        _credentials = credentials;
    }

    public async Task<VideoParseResult> CreateAsync(
        IReadOnlyList<BiliVideoCollection> collections,
        string aggregateTitle,
        CancellationToken cancellationToken)
    {
        if (collections.Count == 0)
            throw new ContentSourceException(ContentSourceErrorCode.InvalidInput, "请至少选择一个内容项。");

        var seen = new HashSet<MediaUnitKey>();
        var items = collections.SelectMany(collection => collection.Items)
            .Where(item => item.Aid > 0 && item.Cid > 0)
            .Where(item => seen.Add(item.MediaUnitKey ?? new MediaUnitKey(item.Aid, item.Cid)))
            .ToList();
        if (items.Count == 0)
            throw new ContentSourceException(ContentSourceErrorCode.ProtocolViolation, "所选内容没有可下载的视频单元。");

        for (var index = 0; index < items.Count; index++) items[index].Index = index + 1;
        var merged = new BiliVideoCollection
        {
            SeriesTitle = collections.Count == 1 ? collections[0].SeriesTitle : aggregateTitle,
            Cover = collections.FirstOrDefault(collection => !string.IsNullOrWhiteSpace(collection.Cover))?.Cover ?? "",
            UpName = collections.Select(collection => collection.UpName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "",
            PublishDate = collections.Select(collection => collection.PublishDate).FirstOrDefault(date => date.HasValue),
            Items = items,
        };

        return await ProbeAsync(merged, preferredItem: null, cancellationToken);
    }

    internal async Task<VideoParseResult> ProbeAsync(
        BiliVideoCollection collection,
        BiliVideoItem? preferredItem,
        CancellationToken cancellationToken)
    {
        var item = preferredItem ?? collection.Items.FirstOrDefault();
        BiliDashResult? dash = null;
        if (item is not null)
        {
            try
            {
                dash = await _mediaProbe.GetDashResultAsync(
                    item.Aid, item.Cid, 80, _credentials.GetCookieHeader(),
                    item.MediaType, item.EpId, item.SeasonId, cancellationToken);
            }
            catch (Exception ex) when (
                (preferredItem is not null || collection.Items.Count > 1) &&
                ex is MediaAuthorizationException or ResourceUnavailableException or Flurl.Http.FlurlHttpException)
            {
                item.StageText = ex is MediaAuthorizationException
                    ? "当前分集需要登录或播放权限"
                    : "当前分集暂时无法获取播放规格";
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var qualities = dash is null ? [] : GetVideoQualityOptions(dash);
        var audio = (dash?.AudioStreams ?? [])
            .Where(stream => stream.AudioFeature == BiliAudioFeature.Standard)
            .GroupBy(stream => stream.Id)
            .Select(group => group.OrderByDescending(stream => stream.Bandwidth).First())
            .OrderBy(stream => stream.Bandwidth)
            .Select(stream => new BiliQualityOption
            {
                QualityId = stream.Id,
                DisplayName = FormatAudioQualityName(stream.Id, stream.Bandwidth),
            }).ToList();

        return new VideoParseResult
        {
            Collection = collection,
            VideoItems = collection.Items.ToList(),
            PreferredItemId = preferredItem?.ItemId,
            QualityOptions = qualities,
            SelectedQuality = qualities.FirstOrDefault(option => option.IsAvailable == true),
            AudioQualityOptions = audio,
            SelectedAudioQuality = audio.LastOrDefault(),
            IsMultiVideo = collection.Items.Count > 1,
            TitlesText = string.Join(Environment.NewLine, collection.Items.Select(item => item.Title)),
        };
    }

    internal static List<BiliQualityOption> GetVideoQualityOptions(BiliDashResult dash)
    {
        var available = dash.VideoStreams
            .Where(stream => stream.Id > 0 && stream.Id is not (125 or 126)
                && (!string.IsNullOrWhiteSpace(stream.BaseUrl) || stream.BackupUrls.Count > 0))
            .Select(stream => (stream.Id, Codec: MediaStreamSelectionPolicy.DetectVideoCodec(stream)))
            .Where(stream => stream.Codec != VideoCodec.Unknown)
            .GroupBy(stream => stream.Id)
            .ToDictionary(group => group.Key,
                group => (IReadOnlySet<VideoCodec>)group.Select(stream => stream.Codec).ToHashSet());
        var advertised = dash.AcceptQualities
            .Where(option => option.QualityId > 0 && option.QualityId is not (125 or 126))
            .DistinctBy(option => option.QualityId)
            .ToDictionary(option => option.QualityId, option => option.DisplayName);

        return advertised.Keys.Union(available.Keys)
            .OrderDescending()
            .Select(id => new BiliQualityOption
            {
                QualityId = id,
                DisplayName = advertised.GetValueOrDefault(id) ?? $"画质 {id}",
                IsAvailable = available.ContainsKey(id) ? true : null,
                AvailableCodecs = available.GetValueOrDefault(id),
            })
            .ToList();
    }

    private static string FormatAudioQualityName(int audioId, long bandwidth)
    {
        var kbps = bandwidth / 1000;
        return audioId switch
        {
            30216 => $"{kbps}kbps (标准)",
            30232 => $"{kbps}kbps (高品质)",
            30280 => $"{kbps}kbps (无损)",
            _ when audioId >= 30250 => $"{kbps}kbps (Hi-Res)",
            _ => $"{kbps}kbps (ID:{audioId})",
        };
    }
}
