using BiliDownloader.Plugin;
using BiliDownloader.Services.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;

namespace BiliDownloaderPlugin.Standalone;

/// <summary>独立窗口拥有的单插件对象图；启动失败和关闭均等待后台组件完成收尾。</summary>
internal sealed class PreviewRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private ServiceProvider? _provider;
    private IServiceScope? _scope;
    private IPluginLifecycle? _lifecycle;
    private PreviewDocumentLifetime? _documentLifetime;
    private Task? _startup;
    private Task? _shutdown;
    public PreviewPluginRegistration Registration { get; } = new();
    public IPluginDocument? Document { get; private set; }
    public object? Tool { get; private set; }
    public PreviewRuntime(IPluginModule? module = null, Action<IServiceCollection>? configure = null)
    {
        (module ?? new BiliDownloaderPluginModule()).Configure(Registration);
        Registration.Services.Replace(ServiceDescriptor.Singleton<IBiliDataPaths>(new StandaloneDataPaths()));
        Registration.Services.AddScoped<PreviewDocumentLifetime>();
        Registration.Services.AddScoped<IDocumentLifetime>(p => p.GetRequiredService<PreviewDocumentLifetime>());
        configure?.Invoke(Registration.Services);
    }
    public Task StartAsync() => _startup ??= StartCoreAsync();
    private async Task StartCoreAsync()
    {
        try
        {
            _stopping.Token.ThrowIfCancellationRequested();
            _provider = Registration.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            _lifecycle = (IPluginLifecycle)_provider.GetRequiredService(Registration.LifecycleType);
            await _lifecycle.InitializeAsync(_stopping.Token);
            _stopping.Token.ThrowIfCancellationRequested();
            _scope = _provider.CreateScope();
            _documentLifetime = _scope.ServiceProvider.GetRequiredService<PreviewDocumentLifetime>();
            Document = (IPluginDocument)_scope.ServiceProvider.GetRequiredService(Registration.DocumentType);
            Tool = _provider.GetRequiredService(Registration.ToolType);
            await Document.InitializeAsync(new NewDocumentActivation("Bilibili 下载 — 独立调试"), _stopping.Token);
        }
        catch
        {
            try { await ReleaseAsync(); } catch { /* 保留原始初始化异常。 */ }
            throw;
        }
    }
    public ValueTask DisposeAsync() => new(_shutdown ??= StopCoreAsync());
    private async Task StopCoreAsync()
    {
        _stopping.Cancel();
        _documentLifetime?.Close();
        if (_startup is not null) { try { await _startup; } catch { } }
        try { await ReleaseAsync(); } finally { _stopping.Dispose(); }
    }
    private async Task ReleaseAsync()
    {
        var lifecycle = _lifecycle;
        _lifecycle = null;
        _documentLifetime?.Close();
        try { if (lifecycle is not null) await lifecycle.ShutdownAsync(CancellationToken.None); }
        finally
        {
            _documentLifetime = null;
            try { _scope?.Dispose(); }
            finally
            {
                _scope = null;
                var provider = _provider;
                _provider = null;
                Document = null;
                Tool = null;
                if (provider is not null) await provider.DisposeAsync();
            }
        }
    }
}
