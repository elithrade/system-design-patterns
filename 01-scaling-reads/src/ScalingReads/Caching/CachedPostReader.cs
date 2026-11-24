using Microsoft.Extensions.Options;

namespace ScalingReads;

// Stage 3: cache-aside in Redis for GET /posts/{id}, and keeping it fresh when a like lands.
//
// The goal, which the tests check:
//   - a cached post is read from the database once, however often it is requested
//   - a new like shows up in the very next read
//   - a like does NOT force the next read to reload the post from the database
//     (a celebrity post gets thousands of likes a second; if each like evicts it, it is never cached)
//
// ICache has what you need (see its comments). Serialize with System.Text.Json.
// db.GetLikeCountAsync reads just the count, if you want it.
//
// Tests: dotnet test --filter Stage3
public sealed class CachedPostReader(PostRepository db, ICache cache, IOptions<ReadsOptions> options)
    : IPostReader, IPostChangeListener
{
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(options.Value.CacheTtlSeconds);

    public Task<Post?> GetPostAsync(long id, DbTarget target, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task LikedAsync(long postId, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
