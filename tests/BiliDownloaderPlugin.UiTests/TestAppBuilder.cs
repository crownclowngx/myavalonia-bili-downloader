using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
[assembly: AvaloniaTestApplication(typeof(BiliDownloaderPlugin.UiTests.TestAppBuilder))]
namespace BiliDownloaderPlugin.UiTests;
public static class TestAppBuilder
{
 public static AppBuilder BuildAvaloniaApp()
 {
     var builder = AppBuilder.Configure<Standalone.App>();
     return Environment.GetEnvironmentVariable("BILIDOWNLOADER_UI_CAPTURE_ROOT") is null
         ? builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
         : builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
 }
}
internal sealed class UiTestContext : IDisposable
{
 public string TempDirectory { get; } = Path.Combine(Path.GetTempPath(), "BiliDownloader.UiTests", Guid.NewGuid().ToString("N"));
 public UiTestContext() => Directory.CreateDirectory(TempDirectory);
 public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(TempDirectory, true); }
}
