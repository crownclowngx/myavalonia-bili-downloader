using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using BiliDownloader.Services.Infrastructure;
using BiliDownloader.ViewModels;
using BiliDownloaderPlugin.Standalone;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
namespace BiliDownloaderPlugin.UiTests;
public sealed class StandaloneWindowTests
{
    [AvaloniaFact]
    public async Task 独立窗口启动真实对象图并承载文档与调度工具且可关闭()
    {
        using var sandbox = new UiTestContext();
        await using var runtime = new PreviewRuntime(configure: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IBiliDataPaths>(new StandaloneDataPaths(sandbox.TempDirectory)));
            services.Replace(ServiceDescriptor.Singleton<IFfmpegRuntimeLocator>(new OfflineFfmpeg()));
        });
        var window = new MainWindow(runtime);
        var application = Application.Current!;
        var theme = application.RequestedThemeVariant;
        try
        {
            window.Show();
            await window.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
            var workspace = window.FindControl<Grid>("Workspace")!;
            Assert.True(workspace.IsEnabled, window.FindControl<TextBlock>("Status")!.Text);
            var document = Assert.IsAssignableFrom<Control>(window.FindControl<ContentControl>("DocumentContent")!.Content);
            var tool = Assert.IsAssignableFrom<Control>(window.FindControl<ContentControl>("ToolContent")!.Content);
            Assert.Same(runtime.Document, document.DataContext);
            Assert.IsType<BiliDownloaderViewModel>(document.DataContext);
            Assert.Same(runtime.Tool, tool.DataContext);
            Assert.True(Assert.IsType<BiliSchedulerToolViewModel>(tool.DataContext).IsPluginReady);
            foreach (var mode in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                application.RequestedThemeVariant = mode;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Assert.True(document.Bounds.Width > 600);
                Assert.True(tool.Bounds.Width > 400);
                var capture = Environment.GetEnvironmentVariable("BILIDOWNLOADER_UI_CAPTURE_ROOT");
                if (capture is not null)
                {
                    Directory.CreateDirectory(capture);
                    using var bitmap = window.CaptureRenderedFrame();
                    Assert.NotNull(bitmap);
                    bitmap.Save(Path.Combine(capture, "standalone-" + mode.Key + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
        }
        finally
        {
            await runtime.DisposeAsync();
            window.Close();
            application.RequestedThemeVariant = theme;
        }
        Assert.Null(runtime.Document);
        Assert.False(window.IsVisible);
    }
    [AvaloniaFact]
    public async Task 启动失败时窗口显示错误且不发布业务视图()
    {
        using var sandbox = new UiTestContext();
        await using var runtime = new PreviewRuntime(configure: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IBiliDataPaths>(new StandaloneDataPaths(sandbox.TempDirectory)));
            services.Replace(ServiceDescriptor.Singleton<IBiliLocalStateInitializer>(new FailingInitializer()));
        });
        var window = new MainWindow(runtime);
        try
        {
            window.Show();
            await window.Initialization.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(window.FindControl<Grid>("Workspace")!.IsEnabled);
            Assert.Contains("初始化失败", window.FindControl<TextBlock>("Status")!.Text);
            Assert.Null(window.FindControl<ContentControl>("DocumentContent")!.Content);
            Assert.Null(runtime.Document);
        }
        finally { window.Close(); }
        Assert.False(window.IsVisible);
    }
    private sealed class FailingInitializer : IBiliLocalStateInitializer
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.FromException(new IOException("测试初始化失败"));
    }
    private sealed class OfflineFfmpeg : IFfmpegRuntimeLocator
    {
        public string? CustomPath { get; set; }
        public string? ResolvedPath => null;
        public bool IsReady => false;
        public string? ResolveFfmpegPath() => null;
        public Task<bool> ValidatePathAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
        public Task<FfmpegRuntimeStatus> DetectAsync(CancellationToken ct = default) => Task.FromResult(new FfmpegRuntimeStatus(false, null, null, FfmpegRuntimeSource.None, "离线界面验收未配置 FFmpeg"));
    }
}
