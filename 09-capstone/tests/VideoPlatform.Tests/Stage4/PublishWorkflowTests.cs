using Temporalio.Client;

namespace VideoPlatform.Tests.Stage4;

// Stage 4 goal: one workflow per video, from upload to published, that survives what stages 1-3
// needed code for. Each test gets its own task queue on one Temporal dev server.
[Collection("temporal")]
public sealed class PublishWorkflowTests(TemporalFixture temporal)
{
    private static readonly CancellationToken None = CancellationToken.None;

    private Task<App> StartAsync(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Temporal:UploadWaitSeconds"] = "60",
            ["Temporal:HeartbeatTimeoutSeconds"] = "2",
            ["Transcode:BackoffSeconds"] = "1",
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }
        return App.StartAsync(temporal, Engine.Temporal, extra: settings);
    }

    private async Task<string> ResultAsync(App app, long video) =>
        await temporal.Client.GetWorkflowHandle(((TemporalPipeline)app.Get<IPipeline>()).WorkflowId(video)).GetResultAsync<string>(
            rpcOptions: new() { CancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token });

    private static Task ConfirmAsync(App app, long video) => app.Get<IPipeline>().OnUploadedAsync(video, None);

    [Fact]
    public async Task An_uploaded_video_is_published()
    {
        await using var app = await StartAsync();
        var video = await app.UploadedVideoAsync();

        await ConfirmAsync(app, video);

        Assert.Equal("published", await ResultAsync(app, video));
        var snapshot = await app.SnapshotAsync(video);
        Assert.Equal(VideoStatus.Published, snapshot.Status);
        Assert.All(snapshot.Renditions, r => Assert.Equal("done", r.Status));
        Assert.Equal(4, (await temporal.KeysAsync(app.Bucket)).Count);
    }

    [Fact]
    public async Task A_video_never_uploaded_is_abandoned()
    {
        await using var app = await StartAsync(new() { ["Temporal:UploadWaitSeconds"] = "2" });
        var created = await app.Videos.CreateAsync(1, "clip", "video/mp4", 1000, None);

        await app.Get<IPipeline>().OnCreatedAsync(created.Id, None);

        Assert.Equal("abandoned", await ResultAsync(app, created.Id));
        Assert.Equal(VideoStatus.Abandoned, (await app.SnapshotAsync(created.Id)).Status);
    }

    [Fact]
    public async Task A_transient_failure_is_retried()
    {
        await using var app = await StartAsync();
        app.Faults.FailNext("360p", 1);
        var video = await app.UploadedVideoAsync();

        await ConfirmAsync(app, video);

        Assert.Equal("published", await ResultAsync(app, video));
        Assert.Equal(App.Renditions.Length + 1, app.Stats.Transcodes);
    }

    [Fact]
    public async Task A_corrupt_source_fails_without_retries()
    {
        await using var app = await StartAsync();
        var video = await app.UploadedVideoAsync(corrupt: true);

        await ConfirmAsync(app, video);

        Assert.Equal("failed", await ResultAsync(app, video));
        var snapshot = await app.SnapshotAsync(video);
        Assert.Equal(VideoStatus.Failed, snapshot.Status);
        Assert.Contains("not a video", snapshot.Reason);
        Assert.Equal(App.Renditions.Length, app.Stats.Transcodes);
    }

    [Fact]
    public async Task A_rendition_failing_for_good_removes_the_renditions_already_made()
    {
        await using var app = await StartAsync(new() { ["Transcode:Renditions:720p"] = "6", ["Transcode:MaxAttempts"] = "2" });
        app.Faults.FailNext("720p", 99);
        var video = await app.UploadedVideoAsync();

        await ConfirmAsync(app, video);

        Assert.Equal("failed", await ResultAsync(app, video));
        Assert.Equal([Blobs.SourceKey(video)], await temporal.KeysAsync(app.Bucket));
        Assert.Contains("crashed at 50%", (await app.SnapshotAsync(video)).Reason);
    }

    [Fact]
    public async Task A_transcode_that_stops_heartbeating_is_retried_and_resumes()
    {
        await using var app = await StartAsync();
        app.Faults.HangAt("720p", 40);
        var video = await app.UploadedVideoAsync();

        await ConfirmAsync(app, video);

        Assert.Equal("published", await ResultAsync(app, video));
        Assert.Equal(1, app.Stats.ResumedTranscodes);
    }

    [Fact]
    public async Task Confirming_twice_publishes_once()
    {
        await using var app = await StartAsync();
        var video = await app.UploadedVideoAsync();

        await ConfirmAsync(app, video);
        await ConfirmAsync(app, video);

        Assert.Equal("published", await ResultAsync(app, video));
        Assert.Equal(1, app.Stats.PublishedCount);
        Assert.Equal(App.Renditions.Length, app.Stats.Transcodes);
    }
}
