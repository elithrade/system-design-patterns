using Microsoft.Extensions.Options;

namespace ScalingReads;

// Stage 2. Reads go to the replica, except that a user who wrote in the last StickySeconds reads
// from the primary, so they always see their own writes. Other users may see the write late.
//
// Use the injected TimeProvider for the clock (GetUtcNow or GetTimestamp), never DateTime.UtcNow:
// the tests move time forward with a fake one.
//
// Tests: dotnet test --filter Stage2
public sealed class ReadYourWritesRouter(IOptions<ReadsOptions> options, TimeProvider time) : IConnectionRouter
{
    private readonly TimeSpan _sticky = TimeSpan.FromSeconds(options.Value.StickySeconds);

    public DbTarget ForRead(long? viewerId)
    {
        throw new NotImplementedException();
    }

    public void RecordWrite(long userId)
    {
        throw new NotImplementedException();
    }
}
