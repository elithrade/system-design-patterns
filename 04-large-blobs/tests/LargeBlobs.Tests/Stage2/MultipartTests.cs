using System.Net;
using System.Net.Http.Json;

namespace LargeBlobs.Tests.Stage2;

// Stage 2 goal: a big file goes up in parts, and a client that stopped halfway can resume.
[Collection("infra")]
public sealed class MultipartTests(InfraFixture infra)
{
    // 12 MiB in 5 MiB parts: 5, 5, 2
    private static readonly byte[] File = Bytes.Make(12 * Bytes.MiB);

    private static byte[] Part(int number) =>
        File.Skip((number - 1) * 5 * Bytes.MiB).Take(5 * Bytes.MiB).ToArray();

    private static async Task<MultipartPlan> StartAsync(HttpClient client, long fileId)
    {
        var response = await client.PostAsync($"/files/{fileId}/multipart", null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"start: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<MultipartPlan>())!;
    }

    [Fact]
    public async Task A_file_in_three_parts_comes_out_whole()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var ticket = await client.CreateFileAsync("big.bin", File.Length);

        var plan = await StartAsync(client, ticket.Id);
        Assert.Equal([(1, 5L * Bytes.MiB), (2, 5L * Bytes.MiB), (3, 2L * Bytes.MiB)], plan.Parts.Select(p => (p.PartNumber, p.Size)));
        // parts can go up in any order, and in parallel
        await Task.WhenAll(plan.Parts.Reverse().Select(async p =>
            Assert.Equal(HttpStatusCode.OK, await ClientExtensions.PutToUrlAsync(p.Url, Part(p.PartNumber)))));
        var (status, body) = await client.PostAsync($"/files/{ticket.Id}/multipart/complete");

        Assert.True(status == HttpStatusCode.OK, $"complete: {(int)status} {body}");
        Assert.Equal("ready", (await client.FileAsync(ticket.Id)).Status);
        Assert.Equal(File, await infra.ReadObjectAsync(app.Bucket, Blobs.KeyFor(ticket.Id)));
        Assert.Equal(0, app.Stats.BytesThroughApi);
    }

    [Fact]
    public async Task A_client_that_stopped_halfway_resumes()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var ticket = await client.CreateFileAsync("resume.bin", File.Length);
        var plan = await StartAsync(client, ticket.Id);
        await ClientExtensions.PutToUrlAsync(plan.Parts[0].Url, Part(1));
        await ClientExtensions.PutToUrlAsync(plan.Parts[2].Url, Part(3));

        // the client restarts: same upload, and it asks what is already there
        var again = await StartAsync(client, ticket.Id);
        var done = (await client.GetFromJsonAsync<List<UploadedPart>>($"/files/{ticket.Id}/multipart/parts"))!;

        Assert.Equal(plan.UploadId, again.UploadId);
        Assert.Equal([(1, 5L * Bytes.MiB), (3, 2L * Bytes.MiB)], done.Select(p => (p.PartNumber, p.Size)));
        await ClientExtensions.PutToUrlAsync(again.Parts[1].Url, Part(2));
        var (status, _) = await client.PostAsync($"/files/{ticket.Id}/multipart/complete");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(File, await infra.ReadObjectAsync(app.Bucket, Blobs.KeyFor(ticket.Id)));
    }

    [Fact]
    public async Task Completing_with_a_part_missing_leaves_the_upload_open()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var ticket = await client.CreateFileAsync("gap.bin", File.Length);
        var plan = await StartAsync(client, ticket.Id);
        await ClientExtensions.PutToUrlAsync(plan.Parts[0].Url, Part(1));

        var (status, body) = await client.PostAsync($"/files/{ticket.Id}/multipart/complete");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("Missing", body);
        Assert.Equal("pending", (await client.FileAsync(ticket.Id)).Status);
        Assert.Single(await infra.OpenUploadsAsync(app.Bucket));
    }

    [Fact]
    public async Task Abort_leaves_nothing_behind()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var ticket = await client.CreateFileAsync("abandon.bin", File.Length);
        var plan = await StartAsync(client, ticket.Id);
        await ClientExtensions.PutToUrlAsync(plan.Parts[0].Url, Part(1));

        var response = await client.DeleteAsync($"/files/{ticket.Id}/multipart");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await infra.OpenUploadsAsync(app.Bucket));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/files/{ticket.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/files/{ticket.Id}/multipart/parts")).StatusCode);
    }

    [Fact]
    public async Task No_such_file_is_not_found()
    {
        await using var app = await App.StartAsync(infra);

        Assert.Equal(HttpStatusCode.NotFound, (await app.As(1).PostAsync("/files/999999/multipart", null)).StatusCode);
    }
}
