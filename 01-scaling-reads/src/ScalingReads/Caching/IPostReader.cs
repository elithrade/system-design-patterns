namespace ScalingReads;

// Reads one post. PostRepository reads the database; CachedPostReader and HotKeyPostReader wrap it.
public interface IPostReader
{
    Task<Post?> GetPostAsync(long id, DbTarget target, CancellationToken ct);
}

// Told about writes that change what a reader may have cached.
public interface IPostChangeListener
{
    // Called after a new like on postId has committed (not for a repeated like).
    Task LikedAsync(long postId, CancellationToken ct);
}

public sealed class NoOpChangeListener : IPostChangeListener
{
    public Task LikedAsync(long postId, CancellationToken ct) => Task.CompletedTask;
}
