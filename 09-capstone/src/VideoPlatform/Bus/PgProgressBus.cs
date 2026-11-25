using Npgsql;

namespace VideoPlatform;

// Stage 3, part 2: progress across instances with Postgres LISTEN/NOTIFY.
//
// The worker making a rendition and the watcher's stream can be on different instances. Both
// already talk to the same Postgres, which can carry messages too:
//   - PublishAsync: NOTIFY the channel "video_progress" with the snapshot as JSON (Json.Options).
//     pg_notify(channel, payload) takes them as parameters. A notification is delivered when the
//     sending transaction commits, to every session listening at that moment, and dropped for
//     everyone else
//   - SubscribeAsync: add a local subscriber (LocalSubscribers) and make sure this instance is
//     listening. One connection per instance does all the listening, however many videos are
//     watched: open it from the data source the first time anyone subscribes, run
//     LISTEN video_progress, and keep a loop running that waits for notifications
//     (NpgsqlConnection.WaitAsync, with the Notification event) and hands each snapshot to the
//     local subscribers of its video. SubscribeAsync must not return before the LISTEN has run
//   - a snapshot published on this instance reaches this instance's subscribers once, through
//     Postgres like everyone else's
// The tests run two app instances on one database: a snapshot published on one must reach a
// subscriber on the other.
//
// Tests: dotnet test --filter-namespace "*Stage3"
public sealed class PgProgressBus(NpgsqlDataSource db, ILogger<PgProgressBus> logger) : IProgressBus, IAsyncDisposable
{
    public const string Channel = "video_progress";

    private readonly LocalSubscribers _local = new();

    public Task PublishAsync(VideoSnapshot snapshot, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<IProgressSubscription> SubscribeAsync(long videoId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        // stop the listening loop and close its connection, once you have them
        return ValueTask.CompletedTask;
    }
}
