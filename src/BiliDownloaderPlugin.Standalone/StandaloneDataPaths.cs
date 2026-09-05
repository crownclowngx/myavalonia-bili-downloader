using BiliDownloader.Services.Infrastructure;
namespace BiliDownloaderPlugin.Standalone;
/// <summary>所有可被存储纪元重置的目录均限定在调试专属叶子目录。</summary>
internal sealed class StandaloneDataPaths : IBiliDataPaths
{
    public StandaloneDataPaths(string? previewRoot = null)
    {
        DataDirectory = Path.GetFullPath(Path.Combine(previewRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BiliDownloaderPlugin", "Standalone"), "BiliDownloader"));
    }
    public string DataDirectory { get; }
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string TempDirectory => Path.Combine(DataDirectory, "temp");
    public string FfmpegDependencyDirectory => Path.Combine(DataDirectory, "dependencies", "ffmpeg");
    public string FfmpegCurrentPointerPath => Path.Combine(FfmpegDependencyDirectory, "current.json");
    public string DownloadTaskDatabasePath => Path.Combine(DataDirectory, "bili_download_tasks.db");
    public string CredentialDatabasePath => Path.Combine(DataDirectory, "credentials.db");
    public string CredentialKeyPath => Path.Combine(DataDirectory, "credential.key");
    public string StorageEpochMarkerPath => Path.Combine(DataDirectory, "storage_epoch_v2");
    public IReadOnlyList<string> ResetDirectories => [DataDirectory];
}
