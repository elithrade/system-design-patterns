using System.Net;
using System.Net.Http.Json;

namespace LargeBlobs.Tests.Baseline;

// Guards: the baseline works for a small file. These pass from the start.
[Collection("infra")]
public sealed class DirectUploadTests(InfraFixture infra)
{
    [Fact]
    public async Task A_file_goes_up_and_comes_back_through_the_api()
    {
        await using var app = await App.StartAsync(infra);
        var client = app.As(1);
        var bytes = Bytes.Make(1 * Bytes.MiB);

        var response = await client.PutAsync("/files/direct/photo.jpg", new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var file = (await response.Content.ReadFromJsonAsync<FileRecord>())!;

        Assert.Equal(("ready", (long)bytes.Length), (file.Status, file.Size));
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/files/{file.Id}/content"));
        // up through the API and back down through it
        Assert.Equal(2L * bytes.Length, app.Stats.BytesThroughApi);
    }
}
