using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LongRunning;

public sealed class RenderException(string message) : Exception(message);

// Builds the reports. The slow part is simulated: a real year-in-review renders maps and charts.
// The waits are real time, not the TimeProvider, so a test's fake clock does not stop them.
public sealed class Renderer(NpgsqlDataSource db, IOptions<ReportsOptions> options, Stats stats, Faults faults)
{
    private readonly ReportsOptions _options = options.Value;

    public Task<byte[]> RenderAsync(string kind, long userId, int year, CancellationToken ct) => kind switch
    {
        JobKinds.YearInReview => YearInReviewAsync(userId, year, ct),
        JobKinds.Summary => SummaryAsync(userId, year, ct),
        _ => throw new RenderException($"unknown job kind {kind}"),
    };

    // A PDF of the user's year, after RenderMilliseconds of work.
    public async Task<byte[]> YearInReviewAsync(long userId, int year, CancellationToken ct)
    {
        stats.Render();
        var totals = await TotalsAsync(userId, year, ct);
        try
        {
            if (userId == _options.PoisonUserId)
            {
                throw new RenderException($"user {userId} has a route the map renderer cannot draw");
            }
            if (faults.ShouldFail())
            {
                throw new RenderException("the map tile server timed out");
            }
            await Task.Delay(_options.RenderMilliseconds, ct);
        }
        catch (RenderException)
        {
            stats.RenderFailure();
            throw;
        }
        var lines = new[] { $"Your {year} on Stride, runner {userId}" }
            .Concat(totals.Select(t => $"{t.Kind}: {t.Count} activities, {t.Km:0.0} km, {t.Hours:0.0} h"));
        return Pdf.Page(lines);
    }

    // A one-line card, after SummaryMilliseconds of work.
    public async Task<byte[]> SummaryAsync(long userId, int year, CancellationToken ct)
    {
        stats.Render();
        var totals = await TotalsAsync(userId, year, ct);
        await Task.Delay(_options.SummaryMilliseconds, ct);
        var km = totals.Sum(t => t.Km);
        return Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"runner {userId}, {year}: {totals.Sum(t => t.Count)} activities, {km:0.0} km"));
    }

    private sealed record Totals(string Kind, long Count, double Km, double Hours);

    private async Task<List<Totals>> TotalsAsync(long userId, int year, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Totals>(new CommandDefinition("""
            SELECT kind, count(*) AS count, (sum(distance_m) / 1000.0)::float8 AS km, (sum(seconds) / 3600.0)::float8 AS hours
            FROM activities
            WHERE user_id = @userId AND started_at >= make_date(@year, 1, 1) AND started_at < make_date(@year + 1, 1, 1)
            GROUP BY kind ORDER BY kind
            """, new { userId, year }, cancellationToken: ct));
        return rows.AsList();
    }
}

// The smallest valid one-page PDF: Helvetica text, one line per entry.
public static class Pdf
{
    public static byte[] Page(IEnumerable<string> lines)
    {
        var text = new StringBuilder("BT /F1 14 Tf 50 780 Td 18 TL\n");
        foreach (var line in lines)
        {
            text.Append('(').Append(line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj T*\n");
        }
        text.Append("ET");
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(text.ToString())} >>\nstream\n{text}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
