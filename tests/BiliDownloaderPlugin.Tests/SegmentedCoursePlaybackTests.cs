using System.Net;
using System.Text;
using BiliDownloader.Models;
using BiliDownloader.Models.ContentSources;
using BiliDownloader.Services.Api;
using BiliDownloader.Services.Download;
using BiliDownloader.Services.Infrastructure;
using Flurl.Http.Testing;

namespace BiliDownloader.Tests;

public sealed class SegmentedCoursePlaybackTests
{
    private const string First = "part-one";
    private const string Second = "part-two";
    private const string Resource = """
        {"quality":80,"video_codecid":7,"timelength":12000,
         "durl":[{"order":2,"length":6000,"size":8,"url":"https://media.test/two?sign=secret"},
                 {"order":1,"length":6000,"size":8,"url":"https://media.test/one?sign=secret"}]}
        """;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task 分段播放验证正片顺序和时长(bool preview, bool incomplete)
    {
        using var state = new StaticStateScope();
        using var http = new HttpTest();
        ConfigureNav(http);
        var resource = preview ? Resource.Insert(1, "\"is_preview\":1,")
            : incomplete ? Resource.Replace("\"order\":2", "\"order\":3", StringComparison.Ordinal) : Resource;
        http.ForCallsTo("*x/player/wbi/playurl*").RespondWith("{\"code\":0,\"data\":" + resource + "}");
        var api = new BiliApiService();

        if (preview)
        {
            var error = await Assert.ThrowsAsync<MediaAuthorizationException>(() => api.GetPlaybackAsync(1, 2, 80, ""));
            Assert.True(error.PreviewOnly);
        }
        else if (incomplete)
        {
            await Assert.ThrowsAsync<ResourceUnavailableException>(() => api.GetPlaybackAsync(1, 2, 80, ""));
        }
        else
        {
            var playback = await api.GetPlaybackAsync(1, 2, 80, "");
            Assert.Null(playback.Dash);
            Assert.Equal([1, 2], playback.Segmented!.Segments.Select(segment => segment.Number));
            Assert.Equal(12000, playback.Segmented.DurationMilliseconds);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 课程只为已购课时获取播放地址并在空轨道时回退(bool purchased)
    {
        using var http = new HttpTest();
        ConfigureCoursePurchase(http, purchased);
        http.ForCallsTo("*pugv/player/web/v2/playurl*").RespondWith("""{"code":0,"result":{"video_info":{}}}""");
        http.ForCallsTo("*pugv/player/web/playurl*").RespondWith("{\"code\":0,\"data\":" + Resource + "}");
        var api = new BiliApiService();

        if (!purchased)
        {
            await Assert.ThrowsAsync<MediaAuthorizationException>(() =>
                api.GetPlaybackAsync(1, 2, 80, "SESSDATA=test", BiliMediaType.Course, 3, 4));
            http.ShouldNotHaveCalled("*pugv/player/web/*");
            return;
        }

        var result = await api.GetPlaybackAsync(1, 2, 80, "SESSDATA=test", BiliMediaType.Course, 3, 4);
        Assert.NotNull(result.Segmented);
        http.ShouldHaveCalled("*pugv/player/web/v2/playurl*").WithQueryParam("ep_id", 3).Times(1);
        http.ShouldHaveCalled("*pugv/player/web/playurl*").WithQueryParam("cid", 2).Times(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 分段恢复复用有效分段且封装失败后允许仅重试封装(bool course)
    {
        using var state = new StaticStateScope();
        using var http = new HttpTest();
        if (course) ConfigureCoursePurchase(http, purchased: true);
        else ConfigureNav(http);
        var route = course ? "*pugv/player/web/v2/playurl*" : "*x/player/wbi/playurl*";
        http.ForCallsTo(route).RespondWith("{\"code\":0,\"data\":" + Resource + "}");
        var failSecond = true;
        var client = new StubBiliHttpClientFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/two" && failSecond)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var bytes = Encoding.UTF8.GetBytes(request.RequestUri.AbsolutePath == "/one" ? First : Second);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        using var paths = new TestDataPaths();
        var mux = new SegmentMuxer();
        using var downloader = new BiliDownloadService(paths, mux, client, new FakeDownloadRuntime(),
            chunkCount: 1, mediaOutputVerifier: mux);
        var task = new DownloadTaskRecord
        {
            TaskId = "segmented", Aid = 1, Cid = 2, DurationSeconds = 12, QualityId = 80,
            ItemTitle = "分段", OutputDirectory = paths.RootDirectory,
            MediaType = course ? "course" : "video", EpId = course ? 3 : 0, SeasonId = course ? 4 : 0,
        };
        Task<BiliDownloadItemResult> DownloadAsync() =>
            downloader.DownloadItemAsync(task, new BiliApiService(), course ? "SESSDATA=test" : "",
                _ => { }, null, CancellationToken.None);

        await Assert.ThrowsAsync<HttpRequestException>(DownloadAsync);
        var partial = SegmentedDownloadState.Read(task.TempDirectory);
        Assert.NotNull(partial);
        Assert.Equal(8, partial.DownloadedBytes(task.TempDirectory));
        Assert.False(partial.IsComplete);
        Assert.DoesNotContain("secret", string.Join("", Directory.EnumerateFiles(
            SegmentedDownloadState.DirectoryFor(task.TempDirectory), "*.json").Select(File.ReadAllText)));

        failSecond = false;
        await Assert.ThrowsAsync<MediaMergeException>(DownloadAsync);
        Assert.Single(client.Requests, request => request.Uri?.AbsolutePath == "/one");
        Assert.All(client.Requests, request => Assert.False(request.Headers.ContainsKey("Cookie")));
        Assert.Equal(16, task.ExpectedVideoBytes);
        Assert.True(task.VideoIntegrityPassed);

        mux.Fail = false;
        var output = await downloader.MergeDownloadedMediaAsync(task, _ => { }, CancellationToken.None);
        Assert.Equal(First + Second, await File.ReadAllTextAsync(output));
        Assert.False(Directory.Exists(task.TempDirectory));
        Assert.Equal(1, mux.Validations);
    }

    [Fact]
    public void 分段成品检查音频轨和完整时长()
    {
        const string output = """
            {"streams":[{"codec_type":"video","codec_name":"h264"},{"codec_type":"audio"}],
             "format":{"duration":"12.0"}}
            """;
        FfprobeMediaOutputVerifier.VerifySegmentedOutput(output, OutputMediaMode.AudioVideo, VideoCodec.Avc, 12000);
        Assert.Throws<MediaValidationException>(() =>
            FfprobeMediaOutputVerifier.VerifySegmentedOutput(output.Replace("12.0", "10.0", StringComparison.Ordinal),
                OutputMediaMode.AudioVideo, VideoCodec.Avc, 12000));
        Assert.Throws<MediaValidationException>(() =>
            FfprobeMediaOutputVerifier.VerifySegmentedOutput(output, OutputMediaMode.AudioVideo, VideoCodec.Avc, 24000));
        Assert.Throws<MediaValidationException>(() =>
            FfprobeMediaOutputVerifier.VerifySegmentedOutput(output, OutputMediaMode.VideoOnly, VideoCodec.Avc, 12000));
    }

    [Fact]
    public void 课程去重键区分课时且保持普通任务旧指纹()
    {
        var key = new MediaUnitKey(1, 2);
        var spec = new RenditionSpecification(80, 0, VideoCodecPreference.AutoCompatibility,
            OutputContainer.Mp4, OutputMediaMode.AudioVideo);
        Assert.Equal(RenditionFingerprint.Create(key, spec), RenditionFingerprint.Create(key, spec, BiliMediaType.Video, 3));
        Assert.NotEqual(RenditionFingerprint.Create(key, spec), RenditionFingerprint.Create(key, spec, BiliMediaType.Course, 3));
        Assert.NotEqual(RenditionFingerprint.Create(key, spec, BiliMediaType.Course, 3),
            RenditionFingerprint.Create(key, spec, BiliMediaType.Course, 4));
    }

    private static void ConfigureCoursePurchase(HttpTest http, bool purchased) =>
        http.ForCallsTo("*pugv/view/web/season*").RespondWithJson(new
        {
            code = 0,
            data = new
            {
                season_id = 4, user_status = new { payed = purchased },
                episodes = new[] { new { id = 3, aid = 1, cid = 2, playable = true } },
            },
        });

    private static void ConfigureNav(HttpTest http) =>
        http.ForCallsTo("*x/web-interface/nav*").RespondWith(
            """
            {"code":0,"data":{"wbi_img":{"img_url":"https://i.test/abcdefghijklmnopqrstuvwxyz123456.png",
                "sub_url":"https://i.test/654321zyxwvutsrqponmlkjihgfedcba.png"}}}
            """);

    private sealed class SegmentMuxer : IMediaMuxer, IMediaOutputVerifier
    {
        public bool Fail { get; set; } = true;
        public int Validations { get; private set; }

        public Task MergeAsync(string videoPath, string audioPath, string outputPath, CancellationToken ct = default)
            => throw new NotSupportedException();

        public async Task MuxAsync(MediaMuxRequest request, CancellationToken ct = default)
        {
            if (Fail) throw new MediaMergeException("stub mux failure");
            Assert.NotNull(request.Segments);
            await File.WriteAllTextAsync(request.OutputPath,
                string.Concat(request.Segments.Select(File.ReadAllText)), ct);
        }

        public Task<MediaFeatureFlags> VerifyAsync(string mediaPath, MediaFeatureFlags expectedFeatures,
            CancellationToken cancellationToken = default) => Task.FromResult(expectedFeatures);

        public Task VerifySegmentedAsync(string mediaPath, OutputMediaMode mode, VideoCodec videoCodec,
            long expectedMilliseconds, CancellationToken cancellationToken = default)
        {
            Assert.Equal(OutputMediaMode.AudioVideo, mode);
            Assert.Equal(VideoCodec.Avc, videoCodec);
            Assert.Equal(12000, expectedMilliseconds);
            Assert.Equal(First + Second, File.ReadAllText(mediaPath));
            Validations++;
            return Task.CompletedTask;
        }
    }
}
