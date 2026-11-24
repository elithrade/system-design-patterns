using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace ScalingReads.Tests;

// The app under test, on its own cloned database(s), with a fake clock.
public sealed class App : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private App(Dictionary<string, string?> settings) => _settings = settings;

    // primary: the database writes go to. replica: where replica reads go; pass a different clone to
    // get a replica that never receives any write (an extreme of replication lag).
    public static App Create(
        InfraFixture infra,
        string primary,
        string? replica = null,
        RouterKind router = RouterKind.Primary,
        CacheKind cache = CacheKind.None,
        int stickySeconds = 5,
        int l1Seconds = 1) =>
        new(new()
        {
            ["ConnectionStrings:Primary"] = primary,
            ["ConnectionStrings:Replica"] = replica ?? primary,
            ["ConnectionStrings:Redis"] = infra.RedisConnectionString,
            ["Reads:Router"] = router.ToString(),
            ["Reads:Cache"] = cache.ToString(),
            ["Reads:StickySeconds"] = stickySeconds.ToString(),
            ["Reads:L1Seconds"] = l1Seconds.ToString(),
            // a test never sees another test's keys
            ["Reads:KeyPrefix"] = $"t{Guid.NewGuid():N}:",
            // the fixture migrated the template already
            ["Db:Migrate"] = "false",
            ["Logging:LogLevel:Default"] = "Warning",
        });

    public Stats Stats => Services.GetRequiredService<Stats>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // not Development: no developer exception page, so 500 failing requests stay cheap
        builder.UseEnvironment("Test");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time)));
    }
}

public static class ClientExtensions
{
    public static HttpClient As(this App app, long userId)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(Endpoints.UserHeader, userId.ToString());
        return client;
    }

    public static async Task<Post> GetPostAsync(this HttpClient client, long id)
    {
        var response = await client.GetAsync($"/posts/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Post>())!;
    }

    public static async Task<(List<Post> Posts, string Db)> GetUserPostsAsync(this HttpClient client, long userId, int limit = 20)
    {
        var response = await client.GetAsync($"/users/{userId}/posts?limit={limit}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var db = response.Headers.GetValues(Endpoints.DbHeader).Single();
        return ((await response.Content.ReadFromJsonAsync<List<Post>>())!, db);
    }

    public static async Task<CreatedPost> CreatePostAsync(this HttpClient client, string caption)
    {
        var response = await client.PostAsJsonAsync("/posts", new CreatePostRequest(caption));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedPost>())!;
    }

    public static async Task LikeAsync(this HttpClient client, long postId)
    {
        var response = await client.PostAsync($"/posts/{postId}/likes", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
