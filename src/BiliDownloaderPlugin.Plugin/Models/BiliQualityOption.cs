using CommunityToolkit.Mvvm.ComponentModel;

namespace BiliDownloader.Models;

/// <summary>
/// 清晰度选项模型
/// </summary>
public class BiliQualityOption : ObservableObject
{
    /// <summary>
    /// B站画质标识 ID（如 127=8K, 120=4K, 116=1080P60, 80=1080P, 64=720P, 32=480P, 16=360P）
    /// </summary>
    public int QualityId { get; set; }

    /// <summary>
    /// 显示名称（如 "1080P 高清"）
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    internal IReadOnlySet<VideoCodec>? AvailableCodecs { get; set; }
    private bool? _isAvailable = true;

    /// <summary>True when confirmed, false when the requested quality had no track, null when unconfirmed.</summary>
    public bool? IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (SetProperty(ref _isAvailable, value)) OnPropertyChanged(nameof(AvailabilityDisplayName));
        }
    }

    public string AvailabilityDisplayName => IsAvailable switch
    {
        null => $"{DisplayName}（待确认）",
        false => $"{DisplayName}（部分或全部所选项不可用）",
        true => DisplayName,
    };

    public override string ToString() => DisplayName;
}
