using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using LedgerMatch.Core;
using Npgsql;

namespace LedgerMatch.Api;

public static class ReconciliationEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapReconciliations(this WebApplication app)
    {
        app.MapPost("/api/reconciliations", async (ReconciliationRequest request, ReconciliationDb db, CancellationToken ct) =>
        {
            if (request.LeftDatasetId == request.RightDatasetId)
                return Results.Problem(statusCode: 400, title: "Choose two different datasets.");

            var previous = await FindPair(db, request, ct);
            if (previous is not null) return Results.Ok(Read(previous));

            var left = await db.Datasets.AsNoTracking().Include(x => x.Records).SingleOrDefaultAsync(x => x.Id == request.LeftDatasetId, ct);
            var right = await db.Datasets.AsNoTracking().Include(x => x.Records).SingleOrDefaultAsync(x => x.Id == request.RightDatasetId, ct);
            if (left is null || right is null)
                return Results.Problem(statusCode: 404, title: "Dataset not found.");

            var leftRecords = ConvertRecords(left);
            var rightRecords = ConvertRecords(right);
            var groups = Reconciler.Compare(leftRecords, rightRecords);
            var totals = leftRecords.Concat(rightRecords).Select(x => x.Currency).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).Select(currency =>
                {
                    decimal l = leftRecords.Where(x => x.Currency == currency).Sum(x => (decimal)x.AmountMinor);
                    decimal r = rightRecords.Where(x => x.Currency == currency).Sum(x => (decimal)x.AmountMinor);
                    return new CurrencyTotals(currency, l, r, l - r);
                }).ToArray();
            var response = new ReconciliationResponse(Guid.NewGuid(), left.Id, right.Id, groups,
                new ReconciliationSummary(groups.GroupBy(x => x.Status).ToDictionary(x => x.Key, x => x.Count()),
                    leftRecords.Length, rightRecords.Length, totals));
            var run = new ReconciliationRun
            {
                Id = response.Id,
                LeftDatasetId = left.Id,
                RightDatasetId = right.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                ResultJson = JsonSerializer.Serialize(response, Json)
            };
            db.Runs.Add(run);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
                previous = await FindPair(db, request, ct);
                if (previous is null) throw;
                return Results.Ok(Read(previous));
            }
            return Results.Created($"/api/reconciliations/{run.Id}", response);
        }).Produces<ReconciliationResponse>(201).Produces<ReconciliationResponse>().ProducesProblem(400).ProducesProblem(404);

        app.MapGet("/api/reconciliations/{id:guid}", async (Guid id, ReconciliationDb db, CancellationToken ct) =>
        {
            var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return run is null ? Results.NotFound() : Results.Ok(Read(run));
        }).Produces<ReconciliationResponse>().ProducesProblem(404);

        app.MapGet("/api/reconciliations/{id:guid}/csv", async (Guid id, ReconciliationDb db, CancellationToken ct) =>
        {
            var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (run is null) return Results.NotFound();
            var output = new StringBuilder("status,reference,currency,side,sourceRecordId,amountMinor\r\n");
            foreach (var group in Read(run).Groups)
            {
                WriteRecords(output, group, "left", group.Left);
                WriteRecords(output, group, "right", group.Right);
            }
            return Results.File(Encoding.UTF8.GetBytes(output.ToString()), "text/csv; charset=utf-8", $"reconciliation-{id}.csv");
        }).Produces(200, contentType: "text/csv").ProducesProblem(404);
    }

    private static Task<ReconciliationRun?> FindPair(ReconciliationDb db, ReconciliationRequest request, CancellationToken ct) =>
        db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.LeftDatasetId == request.LeftDatasetId && x.RightDatasetId == request.RightDatasetId, ct);

    private static PaymentRecord[] ConvertRecords(Dataset dataset) => dataset.Records
        .Select(x => new PaymentRecord(x.SourceRecordId, x.Reference, x.Currency, x.AmountMinor)).ToArray();

    private static ReconciliationResponse Read(ReconciliationRun run) => JsonSerializer.Deserialize<ReconciliationResponse>(run.ResultJson, Json)!;

    private static void WriteRecords(StringBuilder output, MatchGroup group, string side, IReadOnlyList<PaymentRecord> records)
    {
        foreach (var record in records)
        {
            output.AppendJoin(',', CsvCell(group.Status), CsvCell(group.Reference), CsvCell(group.Currency), side,
                CsvCell(record.SourceRecordId), record.AmountMinor.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }
    }

    private static string CsvCell(string value)
    {
        // Spreadsheet-facing CSV is a view, not a lossless round-trip format. JSON retains original values.
        string visible = value.TrimStart();
        if (visible.Length > 0 && visible[0] is '=' or '+' or '-' or '@' || value.StartsWith('\t') || value.StartsWith('\r') || value.StartsWith('\n'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
