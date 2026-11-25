using Npgsql;

namespace Contention;

// The sales database, and the payments database (null unless ConnectionStrings:Payments is set).
public sealed class Databases(NpgsqlDataSource sales, NpgsqlDataSource? payments) : IAsyncDisposable
{
    public NpgsqlDataSource Sales { get; } = sales;
    public NpgsqlDataSource? Payments { get; } = payments;

    public async ValueTask DisposeAsync()
    {
        await Sales.DisposeAsync();
        if (Payments is not null)
        {
            await Payments.DisposeAsync();
        }
    }
}
