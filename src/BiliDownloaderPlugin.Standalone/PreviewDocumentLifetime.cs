using MyAvaloniaManagement.PluginSdk;
namespace BiliDownloaderPlugin.Standalone;
internal sealed class PreviewDocumentLifetime : IDocumentLifetime, IDisposable
{
    private readonly CancellationTokenSource _closing = new();
    public CancellationToken ClosingToken => _closing.Token;
    public bool IsClosing => _closing.IsCancellationRequested;
    public void Close() { if (!IsClosing) _closing.Cancel(); }
    public void Dispose() { Close(); _closing.Dispose(); }
}
