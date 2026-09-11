using MyAvaloniaManagement.PluginSdk.UI;

namespace BiliDownloader.Plugin;

/// <summary>本插件专属矢量。保持纯不可变数据，不保存 Host 服务、控件或主题画刷。</summary>
internal static class PluginIcons
{
    /// <summary>用户与内容列表。公共八图标无法准确表达该语义，使用原创 20×20 填充路径。</summary>
    internal static VectorIconDefinition PersonalSource { get; } = new(
        "M3,5a3,3 0 1 0 6,0a3,3 0 1 0 -6,0Z M1,16v-3a5,5 0 0 1 10,0v3Z M13,4h6v2h-6Z M13,8h6v2h-6Z M13,12h6v2h-6Z", 20, 20);

    /// <summary>时钟与任务调度。公共八图标无法准确表达该语义，使用原创 20×20 填充路径。</summary>
    internal static VectorIconDefinition Scheduler { get; } = new(
        "M2,10a8,8 0 1 0 16,0a8,8 0 1 0 -16,0Z M4,10a6,6 0 1 0 12,0a6,6 0 1 0 -12,0Z M9,5h2v5l4,2l-1,2l-5,-3Z", 20, 20);

}
