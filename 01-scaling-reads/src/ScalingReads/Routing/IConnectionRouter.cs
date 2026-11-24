namespace ScalingReads;

// Decides which database serves a read. Writes always go to the primary and are not routed.
public interface IConnectionRouter
{
    // viewerId is the X-User-Id of the request, or null when the caller sent none.
    DbTarget ForRead(long? viewerId);

    // Called after userId's write (a post or a like) has committed on the primary.
    void RecordWrite(long userId);
}

// The baseline: the replica sits idle.
public sealed class PrimaryOnlyRouter : IConnectionRouter
{
    public DbTarget ForRead(long? viewerId) => DbTarget.Primary;
    public void RecordWrite(long userId) { }
}

// The obvious first step, and wrong: run it against the compose replica and post, then load your
// own profile straight away.
public sealed class ReplicaOnlyRouter : IConnectionRouter
{
    public DbTarget ForRead(long? viewerId) => DbTarget.Replica;
    public void RecordWrite(long userId) { }
}
