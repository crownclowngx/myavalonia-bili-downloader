using Avalonia.Controls;
using BiliDownloader.Constants;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;

namespace BiliDownloaderPlugin.Standalone;

/// <summary>消费真实 Module 的贡献，不维护第二份业务服务清单。</summary>
internal sealed class PreviewPluginRegistration : IPluginRegistration, IPluginIconRegistration
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


    public PluginId PluginId => BiliDownloaderContributionIds.Plugin;
    public IServiceCollection Services { get; } = new ServiceCollection();
    public Type LifecycleType { get; private set; } = null!;
    public Type DocumentType { get; private set; } = null!;
    public Type ToolType { get; private set; } = null!;
    public Func<Control> CreateDocumentView { get; private set; } = null!;
    public Func<Control> CreateToolView { get; private set; } = null!;
    public void UseLifecycle<T>() where T : class, IPluginLifecycle
    {
        if (LifecycleType is not null) throw new InvalidOperationException("预览只支持一个生命周期。");
        LifecycleType = typeof(T);
        Services.AddSingleton<T>();
    }
    public void AddDocument<T, V>(DocumentDescriptor descriptor)
        where T : class, IPluginDocument where V : Control, new()
    {
        if (DocumentType is not null) throw new InvalidOperationException("预览只支持一个下载文档。");
        DocumentType = typeof(T);
        CreateDocumentView = static () => new V();
        Services.AddScoped<T>();
    }
    public void AddPersistableDocument<T, V>(DocumentDescriptor descriptor)
        where T : class, IPersistablePluginDocument where V : Control, new() => AddDocument<T, V>(descriptor);
    public void AddTool<T, V>(ToolDescriptor descriptor) where T : class where V : Control, new()
    {
        if (ToolType is not null) throw new InvalidOperationException("预览只支持一个调度工具。");
        ToolType = typeof(T);
        CreateToolView = static () => new V();
        Services.AddSingleton<T>();
    }
}
