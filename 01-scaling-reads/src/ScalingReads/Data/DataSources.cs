using Npgsql;

namespace ScalingReads;

public enum DbTarget
{
    Primary,
    Replica,
}

// The two connection pools. Writes always use Primary.
public sealed class DataSources(NpgsqlDataSource primary, NpgsqlDataSource replica) : IAsyncDisposable
{
    public NpgsqlDataSource Primary { get; } = primary;
    public NpgsqlDataSource Replica { get; } = replica;

    public NpgsqlDataSource For(DbTarget target) => target == DbTarget.Primary ? Primary : Replica;

    public async ValueTask DisposeAsync()
    {
        await Primary.DisposeAsync();
        await Replica.DisposeAsync();
    }
}
