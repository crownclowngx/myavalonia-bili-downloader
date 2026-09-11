using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using BiliDownloader.Plugin;

namespace BiliDownloaderPlugin.UiTests;

/// <summary>
/// Headless UI 测试使用的最小插件注册入口。它只复现贡献根类型的生命周期，
/// 不复制 Host 的注册所有权校验或 Provider 构建逻辑，避免测试替身演变成第二套容器实现。
/// </summary>
internal sealed class TestPluginRegistrationContext(
    PluginId pluginId,
    IServiceCollection services) : IPluginRegistration, IPluginIconRegistration
{
    // V6.1：预览/测试只保留本次组合的纯图标数据；不使用 Host 的全局注册表或缓存。
    // 对重复名称和非法名称直接报错，避免预览吞掉正式 Host 会拒绝的声明。
    private readonly Dictionary<string, VectorIconDefinition> _previewIcons = new(StringComparer.Ordinal);
    public string AddIcon(string localName, VectorIconDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (localName is null || !System.Text.RegularExpressions.Regex.IsMatch(localName, @"\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\z"))
            throw new ArgumentException("图标名称必须使用小写字母、数字及单个连字符分段。", nameof(localName));
        var reference = $"plugin:{PluginId.Value}/{localName}";
        _previewIcons.Add(reference, definition);
        return reference;
    }


    public PluginId PluginId { get; } = pluginId;

    public IServiceCollection Services { get; } = services;

    public void UseLifecycle<TLifecycle>() where TLifecycle : class, IPluginLifecycle =>
        Services.AddSingleton<TLifecycle>();

    public void AddDocument<TDocument, TView>(DocumentDescriptor descriptor)
        where TDocument : class, IPluginDocument
        where TView : Control, new() => Services.AddScoped<TDocument>();

    public void AddPersistableDocument<TDocument, TView>(DocumentDescriptor descriptor)
        where TDocument : class, IPersistablePluginDocument
        where TView : Control, new() => Services.AddScoped<TDocument>();

    public void AddTool<TTool, TView>(ToolDescriptor descriptor)
        where TTool : class
        where TView : Control, new() => Services.AddSingleton<TTool>();
}

internal sealed class UiTestDocumentLifetime : IDocumentLifetime
{
    public CancellationToken ClosingToken => CancellationToken.None;

    public bool IsClosing => false;
}

internal sealed class UiReadyBiliReadiness : IBiliDownloaderPluginReadiness
{
    public BiliDownloaderReadinessSnapshot Snapshot { get; } = new(
        BiliDownloaderReadinessStatus.Ready,
        true,
        "插件已就绪。");

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}
