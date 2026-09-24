using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualBasic.FileIO;

namespace Mutabakat.Api.Tests;

public sealed class ReconciliationApiTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    private sealed record Row(string SourceRecordId, string Reference, string Currency, long AmountMinor);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidDatasetIsRejectedAsAWholeAndDoesNotConsumeKey(bool csv)
    {
        string key = Key();
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ReconciliationDb database = scope.ServiceProvider.GetRequiredService<ReconciliationDb>();
        int originalDatasets = await database.Datasets.CountAsync();
        int originalRecords = await database.Records.CountAsync();
        using HttpResponseMessage rejected = csv
            ? await UploadCsv(key, "sourceRecordId,reference,currency,amountMinor\na,R,TRY,100\nb,R,TRY,-1\n")
            : await Upload(key, [new("a", "R", "TRY", 100), new("b", "R", "TRY", -1)]);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        JsonElement problem = await Body(rejected);
        Assert.True(problem.GetProperty("errors").GetArrayLength() > 0);
        Assert.Equal(originalDatasets, await database.Datasets.CountAsync());
        Assert.Equal(originalRecords, await database.Records.CountAsync());

        using HttpResponseMessage accepted = await Upload(key, [new("a", "R", "TRY", 100)]);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(1, (await Body(accepted)).GetProperty("recordCount").GetInt32());
    }

    [Fact]
    public async Task CsvAndJsonShareNormalizedIdempotencyRegardlessOfRecordOrder()
    {
        string key = Key();
        using HttpResponseMessage first = await Upload(key,
            [new(" b ", "B", "eur", 200), new("a", " A ", "try", 100)], " settlement ");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Guid id = Id(await Body(first));
        using HttpResponseMessage replay = await UploadCsv(key,
            "sourceRecordId,reference,currency,amountMinor\na,A,TRY,100\nb,B,EUR,200\n", "settlement");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(id, Id(await Body(replay)));
        using HttpResponseMessage changed = await Upload(key,
            [new("a", "A", "TRY", 101), new("b", "B", "EUR", 200)], "settlement");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
    }

    [Fact]
    public async Task ConcurrentDatasetRetriesReturnOnePersistedIdentity()
    {
        string key = Key();
        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Upload(key, [new("a", "R", "TRY", 123)])));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK));
            JsonElement[] bodies = await Task.WhenAll(responses.Select(Body));
            Assert.Single(bodies.Select(Id).Distinct());
            using HttpResponseMessage get = await fixture.Client.GetAsync($"/api/datasets/{Id(bodies[0])}");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal(1, (await Body(get)).GetProperty("recordCount").GetInt32());
        }
        finally { foreach (HttpResponseMessage response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task ReconciliationPreservesDuplicatesMissingRowsAndPerCurrencyTotals()
    {
        Guid left = await Dataset([
            new("l1", "A", "TRY", 100), new("l2", "B", "TRY", 200),
            new("l3", "D", "EUR", 30), new("l4", "D", "EUR", 40), new("l5", "L", "TRY", 50)]);
        Guid right = await Dataset([
            new("r1", "A", "TRY", 100), new("r2", "B", "TRY", 250),
            new("r3", "D", "EUR", 70), new("r4", "R", "EUR", 90)]);
        using HttpResponseMessage response = await Compare(left, right);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        JsonElement result = await Body(response);
        JsonElement[] groups = result.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Equal(["A", "B", "D", "L", "R"], groups.Select(g => g.GetProperty("reference").GetString()!).ToArray());
        Assert.Equal(["matched", "amount_mismatch", "duplicate", "missing_right", "missing_left"],
            groups.Select(g => g.GetProperty("status").GetString()!).ToArray());
        Assert.Equal(2, groups[2].GetProperty("left").GetArrayLength());
        Assert.Equal(1, groups[2].GetProperty("right").GetArrayLength());
        Assert.Equal(5, groups.Sum(g => g.GetProperty("left").GetArrayLength()));
        Assert.Equal(4, groups.Sum(g => g.GetProperty("right").GetArrayLength()));
        JsonElement summary = result.GetProperty("summary");
        Assert.Equal(5, summary.GetProperty("leftRecordCount").GetInt32());
        Assert.Equal(4, summary.GetProperty("rightRecordCount").GetInt32());
        foreach (string status in new[] { "matched", "amount_mismatch", "duplicate", "missing_right", "missing_left" })
            Assert.Equal(1, summary.GetProperty("groupsByStatus").GetProperty(status).GetInt32());
        Dictionary<string, JsonElement> totals = summary.GetProperty("totalsByCurrency").EnumerateArray()
            .ToDictionary(t => t.GetProperty("currency").GetString()!);
        AssertTotals(totals["TRY"], 350, 350, 0);
        AssertTotals(totals["EUR"], 70, 160, -90);

        using HttpResponseMessage persisted = await fixture.Client.GetAsync($"/api/reconciliations/{Id(result)}");
        Assert.Equal(HttpStatusCode.OK, persisted.StatusCode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(result.GetRawText()),
            JsonNode.Parse((await Body(persisted)).GetRawText())));
        using HttpResponseMessage replay = await Compare(left, right);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(Id(result), Id(await Body(replay)));
    }

    [Fact]
    public async Task ConcurrentReconciliationsReuseOneId()
    {
        Guid left = await Dataset([new("l", "R", "TRY", 1)]);
        Guid right = await Dataset([new("r", "R", "TRY", 1)]);
        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Compare(left, right)));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK));
            Assert.Single((await Task.WhenAll(responses.Select(Body))).Select(Id).Distinct());
        }
        finally { foreach (HttpResponseMessage response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task CsvReportEscapesMultilineTextAndNeutralizesFormulaPrefixes()
    {
        const string reference = "=SUM(1,2)\n\"quoted\"";
        Guid left = await Dataset([new("+left", reference, "TRY", 123)]);
        Guid right = await Dataset([new("@right", reference, "TRY", 123)]);
        using HttpResponseMessage compared = await Compare(left, right);
        Guid id = Id(await Body(compared));
        using HttpResponseMessage response = await fixture.Client.GetAsync($"/api/reconciliations/{id}/csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        using TextFieldParser parser = new(new StringReader(await response.Content.ReadAsStringAsync()));
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        Assert.Equal(["status", "reference", "currency", "side", "sourceRecordId", "amountMinor"], parser.ReadFields()!);
        List<string[]> rows = [];
        while (!parser.EndOfData) rows.Add(parser.ReadFields()!);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(6, row.Length);
            Assert.Equal("matched", row[0]);
            Assert.Equal("'" + reference, row[1]);
            Assert.Equal("TRY", row[2]);
            Assert.Equal("123", row[5]);
        });
        Assert.Contains(rows, r => r[3] == "left" && r[4] == "'+left");
        Assert.Contains(rows, r => r[3] == "right" && r[4] == "'@right");
    }

    [Fact]
    public async Task MissingDatasetsAndSelfComparisonReturnExplicitErrors()
    {
        Guid id = await Dataset([new("a", "R", "TRY", 1)]);
        using HttpResponseMessage missing = await Compare(id, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using HttpResponseMessage same = await Compare(id, id);
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        foreach (string path in new[] { $"/api/datasets/{Guid.NewGuid()}", $"/api/reconciliations/{Guid.NewGuid()}", $"/api/reconciliations/{Guid.NewGuid()}/csv" })
        {
            using HttpResponseMessage get = await fixture.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizeRequestsAreRejected(bool csv)
    {
        string key = Key();
        using HttpResponseMessage response = csv
            ? await UploadCsv(key, new string('x', 2 * 1024 * 1024 + 1))
            : await Upload(key, [new("a", new string('x', 2 * 1024 * 1024), "TRY", 1)]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("reference")]
    [InlineData("sourceRecordId")]
    public async Task NullCharactersAreRejectedBeforeDatabaseWrite(string field)
    {
        var record = new Dictionary<string, object> { ["sourceRecordId"] = "id", ["reference"] = "ref", ["currency"] = "TRY", ["amountMinor"] = 100 };
        string source = "test";
        if (field == "source") source = "bad\0source";
        else record[field] = "bad\0value";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        { Content = JsonContent.Create(new { source, records = new[] { record } }) };
        request.Headers.Add("Idempotency-Key", Key());
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty((await Body(response)).GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task MissingAmountIsNotTreatedAsZero()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/datasets")
        { Content = JsonContent.Create(new { source = "test", records = new[] { new { sourceRecordId = "a", reference = "R", currency = "TRY" } } }) };
        request.Headers.Add("Idempotency-Key", Key());
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<Guid> Dataset(Row[] rows)
    {
        using HttpResponseMessage response = await Upload(Key(), rows);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Id(await Body(response));
    }

    private Task<HttpResponseMessage> Compare(Guid left, Guid right) => fixture.Client.PostAsJsonAsync(
        "/api/reconciliations", new { leftDatasetId = left, rightDatasetId = right });

    private async Task<HttpResponseMessage> Upload(string key, Row[] rows, string source = "test")
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/datasets")
        { Content = JsonContent.Create(new { source, records = rows }) };
        request.Headers.Add("Idempotency-Key", key);
        return await fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> UploadCsv(string key, string csv, string source = "test")
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/datasets/csv")
        { Content = new StringContent(csv, Encoding.UTF8, "text/csv") };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-Source", source);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    private static Guid Id(JsonElement body) => body.GetProperty("id").GetGuid();
    private static string Key() => Guid.NewGuid().ToString("N");
    private static void AssertTotals(JsonElement totals, decimal left, decimal right, decimal difference)
    {
        Assert.Equal(left, totals.GetProperty("leftAmountMinor").GetDecimal());
        Assert.Equal(right, totals.GetProperty("rightAmountMinor").GetDecimal());
        Assert.Equal(difference, totals.GetProperty("differenceMinor").GetDecimal());
    }
}
