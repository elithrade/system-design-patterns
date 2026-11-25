using System.Net;

namespace VideoPlatform.Tests.Stage3;

// Stage 3 goal: a progress bar that never misses the end. These drive the video's state by hand.
[Collection("infra")]
public sealed class ProgressStreamTests(InfraFixture infra)
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<long> ProcessingVideoAsync(App app)
    {
        var video = await app.UploadedVideoAsync(bytes: 1000);
        await app.ChangeAsync(video, async v =>
        {
            await v.SetStatusAsync(video, [VideoStatus.Uploaded], VideoStatus.Processing, null, None);
            await v.CreateRenditionsAsync(video, App.Renditions, None);
        });
        return video;
    }

    private static Task SetPercentAsync(App app, long video, string name, int percent) =>
        app.ChangeAsync(video, v => v.SetRenditionAsync(video, name, "running", percent, null, null, None));

    private static Task PublishAsync(App app, long video) =>
        app.ChangeAsync(video, v => v.SetStatusAsync(video, [VideoStatus.Processing], VideoStatus.Published, null, None));

    [Fact]
    public async Task The_stream_starts_with_the_current_state()
    {
        await using var app = await App.StartAsync(infra);
        var video = await ProcessingVideoAsync(app);
        await SetPercentAsync(app, video, "720p", 30);
        var expected = await app.SnapshotAsync(video);

        await using var stream = await SseClient.ConnectAsync(app.Client(), $"/videos/{video}/progress");
        var first = await stream.NextEventAsync();

        Assert.Equal("text/event-stream", stream.Response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", stream.Response.Headers.CacheControl?.ToString());
        Assert.Equal(("progress", expected.Version.ToString()), (first.Event, first.Id));
        Assert.Equivalent(expected, first.Snapshot);
    }

    [Fact]
    public async Task Changes_arrive_in_order_and_the_stream_ends_when_the_video_is_published()
    {
        await using var app = await App.StartAsync(infra);
        var video = await ProcessingVideoAsync(app);
        await using var stream = await SseClient.ConnectAsync(app.Client(), $"/videos/{video}/progress");
        await stream.NextEventAsync();

        await SetPercentAsync(app, video, "360p", 50);
        await SetPercentAsync(app, video, "360p", 100);
        await PublishAsync(app, video);

        var events = await stream.NextEventsAsync(3);
        Assert.Equal([50, 100], events.Take(2).Select(e => e.Snapshot.Renditions.Single(r => r.Name == "360p").Percent));
        Assert.Equal(VideoStatus.Published, events[2].Snapshot.Status);
        Assert.True(events.Select(e => long.Parse(e.Id!)).SequenceEqual(events.Select(e => long.Parse(e.Id!)).Order()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.NextEventAsync());
    }

    [Fact]
    public async Task An_older_state_arriving_late_is_not_sent()
    {
        await using var app = await App.StartAsync(infra);
        var video = await ProcessingVideoAsync(app);
        await SetPercentAsync(app, video, "360p", 40);
        var old = await app.SnapshotAsync(video);
        await SetPercentAsync(app, video, "360p", 80);
        await using var stream = await SseClient.ConnectAsync(app.Client(), $"/videos/{video}/progress");
        var first = await stream.NextEventAsync();

        await app.Get<IProgressBus>().PublishAsync(old, None);
        await PublishAsync(app, video);

        var next = await stream.NextEventAsync();
        Assert.Equal(80, first.Snapshot.Renditions.Single(r => r.Name == "360p").Percent);
        Assert.Equal(VideoStatus.Published, next.Snapshot.Status);
    }

    [Fact]
    public async Task Publishing_between_the_read_and_the_bus_still_ends_the_stream()
    {
        await using var app = await App.StartAsync(infra);
        var video = await ProcessingVideoAsync(app);
        app.Get<ReadHooks>().AfterNextSnapshot(() => PublishAsync(app, video));

        await using var stream = await SseClient.ConnectAsync(app.Client(), $"/videos/{video}/progress");
        var events = new List<SseEvent> { await stream.NextEventAsync() };
        if (!events[0].Snapshot.IsFinal)
        {
            events.Add(await stream.NextEventAsync());
        }

        Assert.Equal(VideoStatus.Published, events[^1].Snapshot.Status);
    }

    [Fact]
    public async Task A_quiet_stream_sends_heartbeats()
    {
        await using var app = await App.StartAsync(infra);
        var video = await ProcessingVideoAsync(app);
        await using var stream = await SseClient.ConnectAsync(app.Client(), $"/videos/{video}/progress");
        await stream.NextEventAsync();
        await Task.Delay(200);

        app.Time.Advance(TimeSpan.FromSeconds(App.HeartbeatSeconds));

        Assert.Equal(": heartbeat", await stream.NextCommentAsync());
    }

    [Fact]
    public async Task An_unknown_video_is_404_and_a_client_leaving_ends_its_subscription()
    {
        await using var app = await App.StartAsync(infra);
        var video = await ProcessingVideoAsync(app);
        var response = await app.Client().GetAsync("/videos/999/progress");

        var stream = await SseClient.ConnectAsync(app.Client(), $"/videos/{video}/progress");
        await stream.NextEventAsync();
        Assert.Equal(1, app.Stats.OpenStreams);
        await stream.DisposeAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (app.Stats.OpenStreams > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        Assert.Equal(0, app.Stats.OpenStreams);
    }
}

// Stage 3, part 2: the Postgres bus between two instances on one database.
[Collection("infra")]
public sealed class PgBusTests(InfraFixture infra)
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static VideoSnapshot Snapshot(long video, long version) => new(video, version, VideoStatus.Processing, null, []);

    private static async Task<IProgressSubscription> SubscribeAsync(App app, long video) =>
        await app.Get<IProgressBus>().SubscribeAsync(video, None);

    private static async Task<VideoSnapshot> NextAsync(IProgressSubscription subscription)
    {
        using var timeout = new CancellationTokenSource(5000);
        return await subscription.Snapshots.ReadAsync(timeout.Token);
    }

    [Fact]
    public async Task A_snapshot_published_on_one_instance_reaches_subscribers_on_both_once()
    {
        await using var a = await App.StartAsync(infra, bus: BusKind.Postgres);
        await using var b = await App.StartAsync(infra, bus: BusKind.Postgres, sameBackendAs: a);
        await using var onA = await SubscribeAsync(a, 7);
        await using var onB = await SubscribeAsync(b, 7);
        await using var other = await SubscribeAsync(a, 8);

        await b.Get<IProgressBus>().PublishAsync(Snapshot(7, 42), None);
        await a.Get<IProgressBus>().PublishAsync(Snapshot(7, 43), None);

        var seenOnA = new List<long> { (await NextAsync(onA)).Version, (await NextAsync(onA)).Version };
        var seenOnB = new List<long> { (await NextAsync(onB)).Version, (await NextAsync(onB)).Version };
        Assert.Equal([42L, 43L], seenOnA);
        Assert.Equal([42L, 43L], seenOnB);
        await Task.Delay(300);
        Assert.False(onA.Snapshots.TryRead(out _));
        Assert.False(other.Snapshots.TryRead(out _));
    }

    [Fact]
    public async Task Many_subscribers_share_one_listening_connection()
    {
        await using var a = await App.StartAsync(infra, bus: BusKind.Postgres);
        var subscriptions = new List<IProgressSubscription>();
        for (var i = 0; i < 20; i++)
        {
            subscriptions.Add(await SubscribeAsync(a, i));
        }

        var listening = await a.ScalarAsync<long>(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND query ILIKE 'LISTEN%'");

        Assert.Equal(1, listening);
        foreach (var subscription in subscriptions)
        {
            await subscription.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_stream_on_one_instance_follows_work_done_on_the_other()
    {
        await using var a = await App.StartAsync(infra, bus: BusKind.Postgres);
        await using var b = await App.StartAsync(infra, bus: BusKind.Postgres, sameBackendAs: a);
        var video = await b.UploadedVideoAsync(bytes: 1000);
        await using var stream = await SseClient.ConnectAsync(a.Client(), $"/videos/{video}/progress");
        await stream.NextEventAsync();

        await b.Get<InlinePipeline>().OnUploadedAsync(video, None);

        var last = await stream.NextEventAsync();
        while (!last.Snapshot.IsFinal)
        {
            last = await stream.NextEventAsync();
        }
        Assert.Equal(VideoStatus.Published, last.Snapshot.Status);
    }
}
