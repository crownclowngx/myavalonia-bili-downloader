using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiliDownloader.Models;
using BiliDownloader.Services.Infrastructure;

namespace BiliDownloader.Services.Download;

/// <summary>Task-local checkpoints for complete, verified segments. URLs and credentials stay in memory.</summary>
internal sealed record SegmentedDownloadState(
    int Version, string MediaIdentity, long DurationMilliseconds, VideoCodec VideoCodec, SegmentedDownloadState.SegmentFile[] Files)
{
    private const string StateFile = "segments-v1.json";

    public long TotalBytes => Files.Sum(file => file.Length);
    public bool IsComplete => Files.All(file => file.Sha256 is not null);

    public static string DirectoryFor(string tempDirectory) => Path.Combine(tempDirectory, "segments");
    public static string FileFor(string directory, int index) => Path.Combine(directory, $"part-{index + 1:D5}.media");

    public static SegmentedDownloadState Create(DownloadTaskRecord task, BiliSegmentedMedia media, VideoCodec videoCodec)
    {
        var identity = new StringBuilder();
        identity.Append(CultureInfo.InvariantCulture,
            $"{task.MediaType}|{task.Aid}|{task.Cid}|{task.EpId}|{task.SeasonId}|{media.QualityId}|{media.VideoCodecId}|{media.DurationMilliseconds}");
        foreach (var segment in media.Segments)
        {
            var path = new Uri(segment.Url).GetLeftPart(UriPartial.Path);
            identity.Append(CultureInfo.InvariantCulture, $"\n{segment.Number}|{segment.Length}|{segment.DurationMilliseconds}|{path}");
        }
        return new(1, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))),
            media.DurationMilliseconds, videoCodec, media.Segments.Select(segment => new SegmentFile(segment.Length, null)).ToArray());
    }

    public static SegmentedDownloadState? Read(string tempDirectory)
    {
        if (string.IsNullOrWhiteSpace(tempDirectory)) return null;
        var path = Path.Combine(DirectoryFor(tempDirectory), StateFile);
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
        try
        {
            var state = JsonSerializer.Deserialize<SegmentedDownloadState>(File.ReadAllBytes(path));
            if (state is not { Version: 1, MediaIdentity.Length: 64, Files.Length: > 0 and <= 10_000 }
                || state.DurationMilliseconds <= 0
                || state.Files.Any(file => file is null || file.Length is <= 0 or > long.MaxValue / 10_000
                    || file.Sha256 is { Length: not 64 }))
                return null;
            return state;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, StateFile);
        var staging = path + ".tmp";
        await File.WriteAllBytesAsync(staging, JsonSerializer.SerializeToUtf8Bytes(this), ct);
        File.Move(staging, path, overwrite: true);
    }

    public long DownloadedBytes(string tempDirectory)
    {
        var directory = DirectoryFor(tempDirectory);
        var total = 0L;
        for (var index = 0; index < Files.Length; index++)
        {
            var path = FileFor(directory, index);
            var length = File.Exists(path) ? new FileInfo(path).Length
                : Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".chunk*")
                    .Sum(file => new FileInfo(file).Length);
            total += Math.Min(length, Files[index].Length);
        }
        return total;
    }

    public bool HasCompleteInputs(string tempDirectory, long expectedLength)
    {
        if (!IsComplete || TotalBytes != expectedLength) return false;
        for (var index = 0; index < Files.Length; index++)
        {
            var path = FileFor(DirectoryFor(tempDirectory), index);
            if (!File.Exists(path) || new FileInfo(path).Length != Files[index].Length) return false;
        }
        return true;
    }

    public async Task<IReadOnlyList<string>> VerifyCompleteInputsAsync(string tempDirectory, CancellationToken ct)
    {
        var files = new List<string>(Files.Length);
        for (var index = 0; index < Files.Length; index++)
        {
            var path = FileFor(DirectoryFor(tempDirectory), index);
            if (!await MatchesAsync(path, Files[index], ct))
                throw new MediaValidationException("分段文件与下载检查点不一致，请完整重试。");
            files.Add(path);
        }
        return files;
    }

    public static async Task<bool> MatchesAsync(string path, SegmentFile file, CancellationToken ct)
        => file.Sha256 is not null && File.Exists(path) && new FileInfo(path).Length == file.Length
            && string.Equals(await HashAsync(path, ct), file.Sha256, StringComparison.Ordinal);

    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
    }

    public static void Reset(string tempDirectory)
    {
        var directory = DirectoryFor(tempDirectory);
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "part-*.media*"))
            File.Delete(path);
        File.Delete(Path.Combine(directory, StateFile));
        File.Delete(Path.Combine(directory, StateFile + ".tmp"));
        File.Delete(Path.Combine(directory, "concat.ffconcat"));
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    public sealed record SegmentFile(long Length, string? Sha256);
}
