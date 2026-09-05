using Avalonia.Controls;
using BiliDownloader.Services.Infrastructure;
using BiliDownloaderPlugin.Standalone;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
namespace BiliDownloader.Tests;

public sealed class StandaloneRuntimeTests
{
    [Fact]
    public async Task 调试纪元重置不触及正式目录且所有数据均位于专属目录()
    {
        var root = Path.Combine(Path.GetTempPath(), "bili-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var formal = Path.Combine(root, "BiliDownloader");
            Directory.CreateDirectory(formal);
            await File.WriteAllTextAsync(Path.Combine(formal, "keep.txt"), "formal");
            var paths = new StandaloneDataPaths(Path.Combine(root, "Standalone"));
            Assert.Equal("BiliDownloader", Path.GetFileName(paths.DataDirectory));
            Assert.Equal(new[] { paths.DataDirectory }, paths.ResetDirectories);
            await new BiliLocalStateInitializer(paths).InitializeAsync();
            Assert.True(File.Exists(Path.Combine(formal, "keep.txt")));
            Assert.True(File.Exists(paths.StorageEpochMarkerPath));
            foreach (var property in typeof(IBiliDataPaths).GetProperties().Where(p => p.PropertyType == typeof(string)))
                Assert.StartsWith(paths.DataDirectory, (string)property.GetValue(paths)!);
            Assert.NotEqual(new BiliDataPaths().DataDirectory, new StandaloneDataPaths().DataDirectory);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task 注册来自真实模块且生产对象图可解析()
    {
        await using var runtime = new PreviewRuntime();
        Assert.Equal(typeof(BiliDownloader.ViewModels.BiliDownloaderViewModel), runtime.Registration.DocumentType);
        Assert.Equal(typeof(BiliDownloader.ViewModels.BiliSchedulerToolViewModel), runtime.Registration.ToolType);
        using var provider = runtime.Registration.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Assert.IsType<StandaloneDataPaths>(provider.GetRequiredService<IBiliDataPaths>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService(runtime.Registration.DocumentType));
    }
    [Fact]
    public async Task 启动一次且关闭先取消文档再停止生命周期并释放所有对象()
    {
        var probe = new Probe();
        var runtime = Create(probe);
        await runtime.StartAsync();
        var document = runtime.Document;
        await runtime.StartAsync();
        Assert.Same(document, runtime.Document);
        Assert.NotNull(runtime.Tool);
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
        Assert.Equal(new[] { "start", "document", "cancel", "stop", "document-dispose", "tool-dispose" }, probe.Events);
        Assert.Null(runtime.Document);
        Assert.Null(runtime.Tool);
    }
    [Fact]
    public async Task 初始化失败保留原异常并停止已启动生命周期()
    {
        var probe = new Probe { Fail = true };
        await using var runtime = Create(probe);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(runtime.StartAsync);
        Assert.Equal("startup failed", error.Message);
        Assert.Equal(new[] { "start", "stop" }, probe.Events);
        Assert.Null(runtime.Document);
    }
    [Fact]
    public async Task 初始化期间关闭会取消启动并等待收尾()
    {
        var probe = new Probe { Wait = true };
        var runtime = Create(probe);
        var startup = runtime.StartAsync();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.Equal(new[] { "start", "stop" }, probe.Events);
    }
    private static PreviewRuntime Create(Probe probe) => new(new Module(), s => s.AddSingleton(probe));
    public sealed class Probe
    {
        public bool Fail { get; init; }
        public bool Wait { get; init; }
        public List<string> Events { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Module : IPluginModule
    {
        public void Configure(IPluginRegistration r)
        {
            r.UseLifecycle<Lifecycle>();
            r.AddDocument<Document, UserControl>(new DocumentDescriptor(new DocumentTypeId("preview.document"), "Preview", "Preview", "Preview"));
            r.AddTool<Tool, UserControl>(new ToolDescriptor(new ToolTypeId("preview.tool"), "Preview", "Preview", ToolDockSide.Right, ToolCloseBehavior.Hide, "Preview"));
        }
    }
    public sealed class Lifecycle(Probe probe) : IPluginLifecycle
    {
        public async Task InitializeAsync(CancellationToken token)
        {
            probe.Events.Add("start"); probe.Started.TrySetResult();
            if (probe.Fail) throw new InvalidOperationException("startup failed");
            if (probe.Wait) await Task.Delay(Timeout.Infinite, token);
        }
        public Task ShutdownAsync(CancellationToken token) { probe.Events.Add("stop"); return Task.CompletedTask; }
    }
    public sealed class Document(Probe probe, IDocumentLifetime lifetime) : IPluginDocument, IDisposable
    {
        private CancellationTokenRegistration _registration;
        public DocumentPresentationState Presentation => throw new NotSupportedException();
        public event EventHandler? PresentationChanged { add { } remove { } }
        public ValueTask InitializeAsync(DocumentActivation activation, CancellationToken token)
        {
            probe.Events.Add("document");
            _registration = lifetime.ClosingToken.Register(() => probe.Events.Add("cancel"));
            return ValueTask.CompletedTask;
        }
        public void Dispose() { _registration.Dispose(); probe.Events.Add("document-dispose"); }
    }
    public sealed class Tool(Probe probe) : IDisposable
    {
        public void Dispose() => probe.Events.Add("tool-dispose");
    }
}

