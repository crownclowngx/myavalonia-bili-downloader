using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;

namespace BiliDownloader.Tests;

/// <summary>
/// 测试组合根使用的最小插件注册入口。它复现当前注册 API 规定的模型生命周期并记录不可变描述符，
/// 但不依赖 HostRuntime、Dock 或磁盘 manifest。
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

    internal List<TestContribution> Contributions { get; } = [];

    public void UseLifecycle<TLifecycle>() where TLifecycle : class, IPluginLifecycle
    {
        Services.AddSingleton<TLifecycle>();
        Contributions.Add(new("Lifecycle", typeof(TLifecycle), null, null));
    }

    public void AddDocument<TDocument, TView>(DocumentDescriptor descriptor)
        where TDocument : class, IPluginDocument
        where TView : Control, new()
    {
        Services.AddScoped<TDocument>();
        Contributions.Add(new("Document", typeof(TDocument), typeof(TView), descriptor));
    }

    public void AddPersistableDocument<TDocument, TView>(DocumentDescriptor descriptor)
        where TDocument : class, IPersistablePluginDocument
        where TView : Control, new()
    {
        Services.AddScoped<TDocument>();
        Contributions.Add(new("Document", typeof(TDocument), typeof(TView), descriptor));
    }

    public void AddTool<TTool, TView>(ToolDescriptor descriptor)
        where TTool : class
        where TView : Control, new()
    {
        Services.AddSingleton<TTool>();
        Contributions.Add(new("Tool", typeof(TTool), typeof(TView), descriptor));
    }
}

internal sealed record TestContribution(
    string Kind,
    Type ModelType,
    Type? ViewType,
    object? Descriptor);
