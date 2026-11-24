using Dapper;
using Npgsql;

namespace ScalingReads.Tests.Stage1;

// Stage 1 goal: the read queries in PostQueries stay cheap however big the tables get.
//   - loading a user's posts must not scan the whole posts table
//   - no read may touch the likes table: counting likes per read grows with every like
// These run EXPLAIN on your current queries against your current migrations.
[Collection("infra")]
public sealed class QueryPlanTests(InfraFixture infra)
{
    [Fact]
    public async Task User_posts_does_not_scan_the_posts_table()
    {
        var plan = await ExplainAsync(PostQueries.UserPosts, new { userId = await SomeAuthorAsync(), limit = 20 });

        Assert.DoesNotContain("Seq Scan on posts", plan);
    }

    [Fact]
    public async Task User_posts_does_not_read_likes()
    {
        var plan = await ExplainAsync(PostQueries.UserPosts, new { userId = await SomeAuthorAsync(), limit = 20 });

        Assert.DoesNotContain(" on likes", plan);
    }

    [Fact]
    public async Task Post_does_not_read_likes()
    {
        var plan = await ExplainAsync(PostQueries.Post, new { id = 1L });

        Assert.DoesNotContain(" on likes", plan);
    }

    [Fact]
    public async Task Like_count_does_not_read_likes()
    {
        var plan = await ExplainAsync(PostQueries.LikeCount, new { id = 1L });

        Assert.DoesNotContain(" on likes", plan);
    }

    private string? _db;

    private async Task<NpgsqlConnection> OpenAsync()
    {
        _db ??= await infra.CloneAsync();
        var conn = new NpgsqlConnection(_db);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<long> SomeAuthorAsync()
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<long>("SELECT author_id FROM posts WHERE id = 100");
    }

    // The plan as text, with the failure message showing it whole.
    private async Task<string> ExplainAsync(string sql, object args)
    {
        await using var conn = await OpenAsync();
        var lines = await conn.QueryAsync<string>("EXPLAIN " + sql, args);
        var plan = string.Join('\n', lines);
        TestContext.Current.SendDiagnosticMessage(plan);
        return "\n" + plan + "\n";
    }
}
