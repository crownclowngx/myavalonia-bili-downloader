using Avalonia.Controls;
using BiliDownloader.Services.Infrastructure;
namespace BiliDownloaderPlugin.Standalone;
public sealed partial class MainWindow : Window
{
    private readonly PreviewRuntime _runtime;
    internal Task Initialization { get; private set; } = Task.CompletedTask;
    private bool _closed;
    private bool _closing;
    public MainWindow() : this(new PreviewRuntime()) { }
    internal MainWindow(PreviewRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        Opened += (_, _) => Initialization = InitializeAsync();
        Closing += OnClosing;
    }
    private async Task InitializeAsync()
    {
        try
        {
            await _runtime.StartAsync();
            if (_closing) return;
            var document = _runtime.Registration.CreateDocumentView();
            document.DataContext = _runtime.Document;
            var tool = _runtime.Registration.CreateToolView();
            tool.DataContext = _runtime.Tool;
            DocumentContent.Content = document;
            ToolContent.Content = tool;
            Workspace.IsEnabled = true;
            Status.Text = "独立调试 · 下载文档 + 调度工具 · 数据与正式插件隔离";
        }
        catch (Exception ex)
        {
            Workspace.IsEnabled = false;
            Status.Text = "初始化失败：" + SensitiveDataSanitizer.Sanitize(ex.Message);
            try { await _runtime.DisposeAsync(); }
            catch (Exception cleanupError)
            {
                Status.Text += "；清理失败：" + SensitiveDataSanitizer.Sanitize(cleanupError.Message);
            }
        }
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closed) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        Workspace.IsEnabled = false;
        Status.Text = "正在停止任务并保存进度…";
        try { await _runtime.DisposeAsync(); }
        catch (Exception ex) { Status.Text = "关闭失败：" + SensitiveDataSanitizer.Sanitize(ex.Message); }
        finally { _closed = true; Close(); }
    }
}
