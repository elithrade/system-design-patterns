using Npgsql;

namespace ScalingWrites;

// The connection pools: the main database, and the partition databases (stage 3; empty unless
// ConnectionStrings:Partition0.. are set).
public sealed class Databases(NpgsqlDataSource main, IReadOnlyList<NpgsqlDataSource> partitions) : IAsyncDisposable
{
    public NpgsqlDataSource Main { get; } = main;
    public IReadOnlyList<NpgsqlDataSource> Partitions { get; } = partitions;

    public async ValueTask DisposeAsync()
    {
        await Main.DisposeAsync();
        foreach (var p in Partitions)
        {
            await p.DisposeAsync();
        }
    }
}
