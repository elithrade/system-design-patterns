using System.Net.Http.Json;

namespace Realtime.Tests.Stage1;

// Stage 1 goal: a poll that waits for news instead of answering "nothing yet" every second.
[Collection("infra")]
public sealed class LongPollTests(InfraFixture infra)
{
    private static Task<List<BidEvent>?> PollAsync(HttpClient client, long auctionId, long after, int waitSeconds = 25, CancellationToken ct = default) =>
        client.GetFromJsonAsync<List<BidEvent>>($"/auctions/{auctionId}/bids?after={after}&waitSeconds={waitSeconds}", Json.Options, ct);

    [Fact]
    public async Task Bids_already_there_come_back_at_once()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        var bid = await client.PlaceBidAsync(1, 110);

        var bids = await PollAsync(client, 1, 0).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal([bid.Id], bids!.Select(b => b.Id));
    }

    [Fact]
    public async Task A_waiting_poll_answers_as_soon_as_a_bid_arrives()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();

        var poll = PollAsync(client, 1, 0);
        await Task.Delay(300);
        Assert.False(poll.IsCompleted, "the poll answered before there was anything to say");
        var bid = await client.PlaceBidAsync(1, 110);

        var bids = await poll.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal([bid.Id], bids!.Select(b => b.Id));
    }

    [Fact]
    public async Task With_no_bids_the_poll_ends_empty_after_the_wait()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();

        var poll = PollAsync(client, 1, 0, waitSeconds: 10);
        await Task.Delay(300);
        app.Time.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(300);
        Assert.False(poll.IsCompleted, "the poll ended before its wait");
        app.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty((await poll.WaitAsync(TimeSpan.FromSeconds(3)))!);
    }

    [Fact]
    public async Task The_wait_is_capped()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();

        var poll = PollAsync(client, 1, 0, waitSeconds: 3600);
        await Task.Delay(300);
        app.Time.Advance(TimeSpan.FromSeconds(App.MaxWaitSeconds));

        Assert.Empty((await poll.WaitAsync(TimeSpan.FromSeconds(3)))!);
    }

    [Fact]
    public async Task A_bid_committed_right_after_the_read_is_not_missed()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        BidEvent? sneaky = null;
        app.Hooks.AfterNextRead(async () => sneaky = await app.BidAsync(1, 110));

        var bids = await PollAsync(client, 1, 0).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal([sneaky!.Id], bids!.Select(b => b.Id));
    }

    [Fact]
    public async Task Another_auctions_bid_does_not_answer_the_poll()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();

        var poll = PollAsync(client, 1, 0);
        await Task.Delay(300);
        await client.PlaceBidAsync(2, 110);
        await Task.Delay(300);
        Assert.False(poll.IsCompleted, "a bid on auction 2 answered a poll on auction 1");
        var bid = await client.PlaceBidAsync(1, 110);

        Assert.Equal([bid.Id], (await poll.WaitAsync(TimeSpan.FromSeconds(3)))!.Select(b => b.Id));
    }

    [Fact]
    public async Task Subscriptions_are_released_when_polls_end_or_clients_leave()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.CreateClient();
        using var leave = new CancellationTokenSource();

        var answered = PollAsync(client, 1, 0);
        var abandoned = PollAsync(client, 2, 0, ct: leave.Token);
        var timedOut = PollAsync(client, 3, 0, waitSeconds: 5);
        await Task.Delay(300);
        await client.PlaceBidAsync(1, 110);
        await answered.WaitAsync(TimeSpan.FromSeconds(3));
        await leave.CancelAsync();
        app.Time.Advance(TimeSpan.FromSeconds(5));
        await timedOut.WaitAsync(TimeSpan.FromSeconds(3));

        await Eventually.AssertAsync(() => Assert.Equal(0, app.Stats.OpenSubscriptions));
        // the test server reports a request the client cancelled as an IOException
        await Assert.ThrowsAnyAsync<Exception>(() => abandoned);
    }
}
