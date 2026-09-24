using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Mutabakat.Core;
using Npgsql;

namespace Mutabakat.Api;

public static class DatasetEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapDatasets(this WebApplication app)
    {
        app.MapPost("/api/datasets", async (DatasetRequest request, HttpRequest http, ReconciliationDb db, CancellationToken ct) =>
            await Save(request.Source, RecordValidation.NormalizeAndValidate(request.Records), http, db, ct))
            .Produces<DatasetResponse>(201).Produces<DatasetResponse>().ProducesProblem(400).ProducesProblem(409);

        app.MapPost("/api/datasets/csv", async (HttpRequest http, ReconciliationDb db, CancellationToken ct) =>
        {
            if (!string.Equals(http.ContentType?.Split(';')[0].Trim(), "text/csv", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(statusCode: 415, title: "Expected text/csv.");
            }
            using var reader = new StreamReader(http.Body, new System.Text.UTF8Encoding(false, true));
            string csv;
            try { csv = await reader.ReadToEndAsync(ct); }
            catch (System.Text.DecoderFallbackException)
            {
                return Results.Problem(statusCode: 400, title: "CSV must be UTF-8.");
            }
            return await Save(http.Headers["X-Source"].ToString(), CsvRecords.Read(csv), http, db, ct);
        }).Accepts<string>("text/csv").Produces<DatasetResponse>(201).Produces<DatasetResponse>().ProducesProblem(400).ProducesProblem(409);

        app.MapGet("/api/datasets/{id:guid}", async (Guid id, ReconciliationDb db, CancellationToken ct) =>
        {
            var dataset = await db.Datasets.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new DatasetResponse(x.Id, x.Source, x.Records.Count)).SingleOrDefaultAsync(ct);
            return dataset is null ? Results.NotFound() : Results.Ok(dataset);
        }).Produces<DatasetResponse>().ProducesProblem(404);
    }

    private static async Task<IResult> Save(string? source, ValidationResult validation, HttpRequest http, ReconciliationDb db, CancellationToken ct)
    {
        string key = http.Headers["Idempotency-Key"].ToString();
        source = source?.Trim();
        var errors = validation.Errors.ToList();
        if (string.IsNullOrEmpty(source) || source.Length > 100 || source.Contains('\0'))
        {
            errors.Add(new(0, "source", "invalid_source", "Source must contain 1–100 characters and no NUL character."));
        }
        if (key.Length is < 1 or > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not ':' and not '-'))
        {
            errors.Add(new(0, "Idempotency-Key", "invalid_key", "Use 1–128 ASCII letters, digits, dots, underscores, colons or hyphens."));
        }
        if (errors.Count > 0)
        {
            return Results.Problem(statusCode: 400, title: "Dataset rejected.",
                detail: "No records were saved. Correct the reported rows and retry.",
                extensions: new Dictionary<string, object?> { ["errors"] = errors });
        }

        // Order does not affect identity; source is part of the content to prevent relabeling a retry.
        var records = validation.Records.OrderBy(x => x.SourceRecordId, StringComparer.Ordinal).ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { source, records }, Json)));
        var existing = await db.Datasets.AsNoTracking().Include(x => x.Records).SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (existing is not null) return Replay(existing, hash);

        var dataset = new Dataset
        {
            Id = Guid.NewGuid(),
            Source = source!,
            IdempotencyKey = key,
            ContentHash = hash,
            CreatedAt = DateTimeOffset.UtcNow,
            Records = records.Select(x => new StoredRecord
            {
                SourceRecordId = x.SourceRecordId,
                Reference = x.Reference,
                Currency = x.Currency,
                AmountMinor = x.AmountMinor
            }).ToList()
        };
        db.Datasets.Add(dataset);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // SaveChanges rolls back the entire graph. A racing request owns this key now.
            db.ChangeTracker.Clear();
            existing = await db.Datasets.AsNoTracking().Include(x => x.Records).SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
            if (existing is null) throw;
            return Replay(existing, hash);
        }
        return Results.Created($"/api/datasets/{dataset.Id}", Response(dataset));
    }

    private static DatasetResponse Response(Dataset dataset) => new(dataset.Id, dataset.Source, dataset.Records.Count);
    private static IResult Replay(Dataset dataset, string hash) => dataset.ContentHash == hash
        ? Results.Ok(Response(dataset))
        : Results.Problem(statusCode: 409, title: "Idempotency key already used.", detail: "This key belongs to different content. Reuse the original content or choose a new key.");
}
