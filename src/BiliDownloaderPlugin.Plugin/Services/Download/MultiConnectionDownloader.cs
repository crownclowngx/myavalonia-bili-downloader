using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using BiliDownloader.Services.Infrastructure;

namespace BiliDownloader.Services.Download;

/// <summary>
/// 多连接分块下载器。所有可合并数据都必须先通过 Range 与长度验证。
/// </summary>
public sealed class MultiConnectionDownloader : IDisposable
{
    private const int MaxRetries = 3;
    private const int RangeBlockBytes = 8 * 1024 * 1024;

    private readonly int _chunkCount;
    private readonly HttpClient _client;
    private readonly IDownloadRuntime _runtime;
    private readonly IBandwidthLimiter _bandwidthLimiter;
    private readonly bool _ownsClient;

    public MultiConnectionDownloader(
        HttpClient client,
        IDownloadRuntime runtime,
        int chunkCount = 4,
        IBandwidthLimiter? bandwidthLimiter = null)
        : this(client, runtime, chunkCount,
            bandwidthLimiter ?? new UnlimitedBandwidthLimiter(), ownsClient: false)
    {
    }

    /// <summary>
    /// 兼容独立使用场景；生产 DI 路径使用显式依赖构造函数。
    /// </summary>
    public MultiConnectionDownloader(int chunkCount = 4)
        : this(
            new BiliHttpClientFactory().CreateMediaClient(),
            new SystemDownloadRuntime(),
            chunkCount,
            new UnlimitedBandwidthLimiter(),
            ownsClient: true)
    {
    }

    private MultiConnectionDownloader(
        HttpClient client,
        IDownloadRuntime runtime,
        int chunkCount,
        IBandwidthLimiter bandwidthLimiter,
        bool ownsClient)
    {
        _client = client;
        _runtime = runtime;
        _bandwidthLimiter = bandwidthLimiter;
        _chunkCount = Math.Max(1, chunkCount);
        _ownsClient = ownsClient;
    }

    public async Task<DownloadTransferResult> DownloadAsync(
        List<string> urls,
        string outputPath,
        string cookie,
        Action<long, long, string> onProgress,
        CancellationToken ct)
        => await DownloadAsync(urls, outputPath, cookie, "standalone", onProgress, ct);

    /// <summary>
    /// 下载属于指定任务的媒体流。taskId 只用于公平调度和诊断，不进入 HTTP 请求或文件名。
    /// </summary>
    public async Task<DownloadTransferResult> DownloadAsync(
        List<string> urls,
        string outputPath,
        string cookie,
        string taskId,
        Action<long, long, string> onProgress,
        CancellationToken ct,
        Action? onNodeSwitch = null)
    {
        if (urls is null || urls.Count == 0)
            throw new ArgumentException("至少需要一个下载 URL", nameof(urls));

        var preferRanges = urls.Count > 1 || File.Exists(outputPath) || File.Exists(outputPath + ".range-state");
        Exception? lastFailure = null;
        HttpRequestException? expiredAddress = null;
        for (var index = 0; index < urls.Count; index++)
        {
            if (index > 0) onNodeSwitch?.Invoke();
            var url = urls[index];
            if (!preferRanges)
            {
                try
                {
                    return await DownloadFullAsync(url, outputPath, cookie, taskId, onProgress, ct);
                }
                catch (Exception ex) when (IsRemoteFailure(ex, ct))
                {
                    lastFailure = ex;
                    expiredAddress = IsExpiredAddress(ex) ? (HttpRequestException)ex : expiredAddress;
                }
            }

            try
            {
                var source = await ProbeRangeSourceAsync(url, cookie, ct);
                if (source is not null)
                {
                    try
                    {
                        return await DownloadRangesAsync(url, source, outputPath, cookie, taskId, onProgress, ct);
                    }
                    catch (RangesNotSupportedException)
                    {
                        // Some nodes return a full response even when the request contains Range.
                    }
                }

                if (preferRanges)
                    return await DownloadFullAsync(url, outputPath, cookie, taskId, onProgress, ct);
            }
            catch (Exception ex) when (IsRemoteFailure(ex, ct))
            {
                lastFailure = ex;
                expiredAddress = IsExpiredAddress(ex) ? (HttpRequestException)ex : expiredAddress;
            }
        }

        if (expiredAddress is not null)
            throw new HttpRequestException("媒体下载地址被 CDN 拒绝。", expiredAddress, expiredAddress.StatusCode);
        if (lastFailure is DownloadProtocolException protocol)
            throw protocol;
        throw new HttpRequestException("所有下载节点均不可用。", lastFailure);
    }

    internal static bool IsExpiredAddress(Exception exception) =>
        exception is HttpRequestException
        {
            StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone,
        };

    private static bool IsRemoteFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or HttpIOException or DownloadProtocolException
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private async Task<DownloadTransferResult> DownloadRangesAsync(
        string url,
        RangeSource source,
        string outputPath,
        string cookie,
        string taskId,
        Action<long, long, string> onProgress,
        CancellationToken ct)
    {
        var totalSize = source.TotalSize;
        var chunkCount = (int)Math.Min(_chunkCount, (totalSize - 1) / (1024 * 1024) + 1);
        var chunkSize = totalSize / chunkCount;
        var checkpointPath = outputPath + ".range-state";
        var checkpoint = GetCheckpointId(url, source, chunkCount);
        var previousCheckpoint = File.Exists(checkpointPath)
            ? await File.ReadAllTextAsync(checkpointPath, ct)
            : null;
        if (checkpoint is not null && previousCheckpoint == "complete:" + checkpoint
            && File.Exists(outputPath) && new FileInfo(outputPath).Length == totalSize)
        {
            onProgress(totalSize, totalSize, "");
            return new DownloadTransferResult(totalSize, totalSize, IntegrityPassed: true);
        }

        // Legacy or unvalidated pieces cannot be safely appended to a newly selected representation.
        if (checkpoint is null || previousCheckpoint != "partial:" + checkpoint)
        {
            File.Delete(checkpointPath);
            DeleteChunks(outputPath);
            if (checkpoint is not null)
                await SaveCheckpointAsync(checkpointPath, "partial:" + checkpoint, ct);
        }

        var downloadedTotal = 0L;
        var lastReportedBytes = 0L;
        var lastReportTime = _runtime.UtcNow;
        var progressLock = new object();
        void AddProgress(long delta)
        {
            var current = Interlocked.Add(ref downloadedTotal, delta);
            lock (progressLock)
            {
                var now = _runtime.UtcNow;
                var elapsed = (now - lastReportTime).TotalSeconds;
                if (elapsed < 0.5) return;
                var speed = (current - lastReportedBytes) / elapsed;
                lastReportedBytes = current;
                lastReportTime = now;
                onProgress(totalSize, current, FormatSpeed((long)speed));
            }
        }

        var tasks = new List<Task>(chunkCount);
        for (var index = 0; index < chunkCount; index++)
        {
            var start = index * chunkSize;
            var end = index == chunkCount - 1 ? totalSize - 1 : (index + 1) * chunkSize - 1;
            var chunkPath = $"{outputPath}.chunk{index}";
            var length = File.Exists(chunkPath) ? new FileInfo(chunkPath).Length : 0;
            if (length > end - start + 1)
            {
                File.Delete(chunkPath);
                length = 0;
            }

            Interlocked.Add(ref downloadedTotal, length);
            tasks.Add(DownloadChunkAsync(url, source, chunkPath, cookie, start, end, length, taskId, AddProgress, ct));
        }

        lastReportedBytes = Interlocked.Read(ref downloadedTotal);
        try
        {
            await Task.WhenAll(tasks);
        }
        catch when (tasks.Any(task => task.Exception?.InnerException is RangesNotSupportedException))
        {
            ct.ThrowIfCancellationRequested();
            throw new RangesNotSupportedException();
        }

        await MergeChunksAsync(outputPath, chunkCount, totalSize, ct);
        if (checkpoint is not null)
            await SaveCheckpointAsync(checkpointPath, "complete:" + checkpoint, ct);
        DeleteChunks(outputPath);
        onProgress(totalSize, totalSize, "");
        return new DownloadTransferResult(totalSize, totalSize, IntegrityPassed: true);
    }

    private async Task DownloadChunkAsync(
        string url,
        RangeSource source,
        string chunkPath,
        string cookie,
        long start,
        long end,
        long length,
        string taskId,
        Action<long> onBytesDelta,
        CancellationToken ct)
    {
        while (length < end - start + 1)
        {
            var blockEnd = Math.Min(end, start + length + RangeBlockBytes - 1);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadChunkOnceAsync(url, chunkPath, cookie, start, blockEnd,
                        source, taskId, onBytesDelta, ct);
                    break;
                }
                catch (Exception ex) when (attempt < MaxRetries && IsRemoteFailure(ex, ct)
                    && !IsExpiredAddress(ex)
                    && ex is not HttpRequestException
                        { StatusCode: HttpStatusCode.PreconditionFailed or HttpStatusCode.RequestedRangeNotSatisfiable })
                {
                    await _runtime.DelayForRetryAsync(attempt - 1, ct);
                }
            }
            length = blockEnd - start + 1;
        }
    }

    private async Task DownloadChunkOnceAsync(
        string url,
        string chunkPath,
        string cookie,
        long rangeStart,
        long rangeEnd,
        RangeSource source,
        string taskId,
        Action<long> onBytesDelta,
        CancellationToken ct)
    {
        var expectedChunkLength = rangeEnd - rangeStart + 1;
        var existingBytes = File.Exists(chunkPath) ? new FileInfo(chunkPath).Length : 0;
        if (existingBytes > expectedChunkLength)
        {
            DeleteIfExists(chunkPath);
            existingBytes = 0;
        }
        if (existingBytes == expectedChunkLength)
        {
            return;
        }

        var actualStart = rangeStart + existingBytes;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddCookie(request, cookie);
        request.Headers.Range = new RangeHeaderValue(actualStart, rangeEnd);
        if (source.ETag is not null) request.Headers.IfMatch.Add(source.ETag);

        long writtenThisAttempt = 0;
        var rangeValidated = false;
        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (response.StatusCode == HttpStatusCode.OK)
                throw new RangesNotSupportedException();
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.PartialContent)
                throw new DownloadProtocolException($"CDN 返回 {(int)response.StatusCode} 而非 206 Partial Content");
            if (source.ETag is not null && response.Headers.ETag is { } received && !source.ETag.Equals(received))
                throw new DownloadProtocolException("CDN 返回的媒体版本已变化。");

            var expectedResponseLength = rangeEnd - actualStart + 1;
            ValidateContentRange(
                response.Content.Headers.ContentRange,
                actualStart,
                rangeEnd,
                source.TotalSize);
            ValidateDeclaredContentLength(
                response.Content.Headers.ContentLength,
                expectedResponseLength);
            rangeValidated = true;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(
                chunkPath,
                existingBytes > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                8192,
                useAsync: true);
            var buffer = new byte[8192];
            while (true)
            {
                var remaining = expectedResponseLength - writtenThisAttempt;
                if (remaining == 0) break;
                var plannedRead = (int)Math.Min(buffer.Length, remaining);
                await _bandwidthLimiter.AcquireAsync(plannedRead, taskId, ct);
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, plannedRead), ct);
                if (bytesRead == 0)
                {
                    break;
                }

                if (writtenThisAttempt + bytesRead > expectedResponseLength)
                {
                    throw new DownloadProtocolException(
                        $"Range 响应体超过声明区间，期望 {expectedResponseLength} 字节");
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                writtenThisAttempt += bytesRead;
                onBytesDelta(bytesRead);
            }

            if (writtenThisAttempt != expectedResponseLength)
            {
                throw new DownloadProtocolException(
                    $"Range 响应体长度错误，期望 {expectedResponseLength}，实际 {writtenThisAttempt}");
            }
            if (response.Content.Headers.ContentLength is null
                && await ReadLimitedAsync(stream, buffer.AsMemory(0, 1), taskId, ct) != 0)
                throw new DownloadProtocolException("Range 响应体超过声明区间。");
        }
        catch (Exception ex)
        {
            RollbackFile(chunkPath, existingBytes);
            if (writtenThisAttempt != 0)
            {
                onBytesDelta(-writtenThisAttempt);
            }
            if (rangeValidated && ex is HttpIOException)
            {
                throw new DownloadProtocolException(
                    $"Range 响应在达到预期长度前中断，已读取 {writtenThisAttempt} 字节",
                    ex);
            }
            throw;
        }
    }

    private async Task<RangeSource?> ProbeRangeSourceAsync(string url, string cookie, CancellationToken ct)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            AddCookie(head, cookie);
            using var response = await _client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is > 0 and var length)
                return new RangeSource(length, ReadStrongETag(response));
        }
        catch (Exception ex) when (IsRemoteFailure(ex, ct))
        {
            // HEAD is optional; a bounded GET is authoritative for media range downloads.
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddCookie(request, cookie);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var probe = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (probe.StatusCode == HttpStatusCode.OK) return null;
        probe.EnsureSuccessStatusCode();
        if (probe.StatusCode != HttpStatusCode.PartialContent ||
            probe.Content.Headers.ContentRange?.Length is not > 0)
            throw new DownloadProtocolException("CDN 无法确认媒体总长度。");

        var total = probe.Content.Headers.ContentRange.Length.Value;
        ValidateContentRange(probe.Content.Headers.ContentRange, 0, 0, total);
        ValidateDeclaredContentLength(probe.Content.Headers.ContentLength, 1);
        return new RangeSource(total, ReadStrongETag(probe));
    }

    private async Task<DownloadTransferResult> DownloadFullAsync(
        string url,
        string outputPath,
        string cookie,
        string taskId,
        Action<long, long, string> onProgress,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddCookie(request, cookie);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var expected = response.Content.Headers.ContentLength ?? 0;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var total = response.Content.Headers.ContentRange?.Length;
            if (total is not > 0)
                throw new DownloadProtocolException("206 响应缺少媒体总长度。");
            ValidateContentRange(response.Content.Headers.ContentRange, 0, total.Value - 1, total.Value);
            ValidateDeclaredContentLength(response.Content.Headers.ContentLength, total.Value);
            expected = total.Value;
        }
        else if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new DownloadProtocolException($"CDN 返回意外的媒体响应 {(int)response.StatusCode}。");
        }

        var stagingPath = outputPath + ".merging";
        var totalWritten = 0L;
        var lastReportedBytes = 0L;
        var lastReportedAt = _runtime.UtcNow;
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(
                stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                var buffer = new byte[8192];
                int read;
                while ((read = await ReadLimitedAsync(input, buffer, taskId, ct)) > 0)
                {
                    totalWritten += read;
                    if (expected > 0 && totalWritten > expected)
                        throw new DownloadProtocolException("完整媒体响应超过声明长度。");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    var now = _runtime.UtcNow;
                    var elapsed = (now - lastReportedAt).TotalSeconds;
                    if (elapsed >= 0.5)
                    {
                        onProgress(expected > 0 ? expected : -1, totalWritten,
                            FormatSpeed((long)((totalWritten - lastReportedBytes) / elapsed)));
                        lastReportedBytes = totalWritten;
                        lastReportedAt = now;
                    }
                }
            }

            if (totalWritten == 0 || (expected > 0 && totalWritten != expected))
                throw new DownloadProtocolException($"完整媒体响应长度错误，期望 {expected}，实际 {totalWritten}。");
            File.Delete(outputPath + ".range-state");
            File.Move(stagingPath, outputPath, overwrite: true);
            DeleteChunks(outputPath);
        }
        finally
        {
            DeleteIfExists(stagingPath);
        }

        onProgress(expected > 0 ? expected : -1, totalWritten, "");
        return new DownloadTransferResult(expected, totalWritten, IntegrityPassed: expected > 0);
    }

    private async ValueTask<int> ReadLimitedAsync(Stream input, Memory<byte> buffer, string taskId, CancellationToken ct)
    {
        await _bandwidthLimiter.AcquireAsync(buffer.Length, taskId, ct);
        return await input.ReadAsync(buffer, ct);
    }

    private static EntityTagHeaderValue? ReadStrongETag(HttpResponseMessage response)
        => response.Headers.ETag is { IsWeak: false } etag ? etag : null;

    private static string? GetCheckpointId(string url, RangeSource source, int chunkCount)
    {
        if (source.ETag is null) return null;
        var resource = new Uri(url).GetLeftPart(UriPartial.Path);
        var bytes = Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"{resource}|{source.ETag}|{source.TotalSize}|{chunkCount}"));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static async Task SaveCheckpointAsync(string path, string value, CancellationToken ct)
    {
        var stagingPath = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(stagingPath, value, ct);
            File.Move(stagingPath, path, overwrite: true);
        }
        finally
        {
            DeleteIfExists(stagingPath);
        }
    }

    private static void DeleteChunks(string outputPath)
    {
        var path = Path.GetFullPath(outputPath);
        var prefix = Path.GetFileName(path) + ".chunk";
        foreach (var chunk in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, prefix + "*"))
        {
            if (int.TryParse(Path.GetFileName(chunk).AsSpan(prefix.Length), out _))
                File.Delete(chunk);
        }
    }

    private sealed record RangeSource(long TotalSize, EntityTagHeaderValue? ETag);
    private sealed class RangesNotSupportedException : Exception;

    private static async Task MergeChunksAsync(
        string outputPath,
        int chunkCount,
        long expectedTotalLength,
        CancellationToken ct)
    {
        long sum = 0;
        for (var i = 0; i < chunkCount; i++)
        {
            var chunkPath = $"{outputPath}.chunk{i}";
            if (!File.Exists(chunkPath))
            {
                throw new DownloadProtocolException($"Chunk 文件缺失: {chunkPath}");
            }
            sum += new FileInfo(chunkPath).Length;
        }
        if (sum != expectedTotalLength)
        {
            throw new DownloadProtocolException(
                $"Chunk 总长度错误，期望 {expectedTotalLength}，实际 {sum}");
        }

        var stagingPath = outputPath + ".merging";
        DeleteIfExists(stagingPath);
        try
        {
            await using (var outputStream = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                8192,
                useAsync: true))
            {
                var buffer = new byte[8192];
                for (var i = 0; i < chunkCount; i++)
                {
                    await using var chunkStream = new FileStream(
                        $"{outputPath}.chunk{i}",
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        8192,
                        useAsync: true);
                    while (true)
                    {
                        var bytesRead = await chunkStream.ReadAsync(buffer, ct);
                        if (bytesRead == 0)
                        {
                            break;
                        }
                        await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    }
                }
            }

            if (new FileInfo(stagingPath).Length != expectedTotalLength)
            {
                throw new DownloadProtocolException("合并后的文件长度与预期总长度不一致");
            }
            File.Move(stagingPath, outputPath, overwrite: true);
        }
        catch
        {
            DeleteIfExists(stagingPath);
            throw;
        }
    }

    private static void ValidateContentRange(
        ContentRangeHeaderValue? contentRange,
        long expectedFrom,
        long expectedTo,
        long expectedTotalLength)
    {
        if (contentRange is null
            || !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || contentRange.From != expectedFrom
            || contentRange.To != expectedTo
            || contentRange.Length != expectedTotalLength)
        {
            throw new DownloadProtocolException(
                $"Content-Range 不匹配，期望 bytes {expectedFrom}-{expectedTo}/{expectedTotalLength}，"
                + $"实际 {contentRange?.ToString() ?? "<missing>"}");
        }
    }

    private static void ValidateDeclaredContentLength(long? declared, long expected)
    {
        if (declared is long actual && actual != expected)
        {
            throw new DownloadProtocolException(
                $"Content-Length 不匹配，期望 {expected}，实际 {actual}");
        }
    }

    private static void AddCookie(HttpRequestMessage request, string cookie)
    {
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.Add("Cookie", cookie);
        }
    }

    private static void RollbackFile(string path, long length)
    {
        if (length == 0)
        {
            File.Delete(path);
            return;
        }
        if (!File.Exists(path))
        {
            return;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件清理失败不覆盖原始协议异常。
        }
    }

    private static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "";
        if (bytesPerSecond < 1024) return $"{bytesPerSecond} B/s";
        if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024.0:F1} KB/s";
        return $"{bytesPerSecond / (1024.0 * 1024):F1} MB/s";
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
