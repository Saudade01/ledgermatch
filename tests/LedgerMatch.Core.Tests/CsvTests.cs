using LedgerMatch.Core;

namespace LedgerMatch.Core.Tests;

public sealed class CsvTests
{
    private const string Header = "sourceRecordId,reference,currency,amountMinor";

    [Fact]
    public void Reads_BOM_quoted_commas_escaped_quotes_multiline_and_long_max()
    {
        var result = CsvRecords.Read("\uFEFF" + Header + "\r\n\" source,1 \",\"Ref \"\"quoted\"\"\r\nline\",eur,9223372036854775807\r\n");
        Assert.Empty(result.Errors);
        Assert.Equal(new("source,1", "Ref \"quoted\"\r\nline", "EUR", long.MaxValue), Assert.Single(result.Records));
    }

    [Theory]
    [InlineData("")]
    [InlineData("reference,sourceRecordId,currency,amountMinor\na,b,TRY,1")]
    [InlineData("sourceRecordId,reference,currency,amountMinor,extra\na,b,TRY,1,")]
    [InlineData("SourceRecordId,reference,currency,amountMinor\na,b,TRY,1")]
    public void Rejects_incorrect_headers(string csv) => AssertRejected(csv, "invalid_header");

    [Theory]
    [InlineData("a,b,TRY")]
    [InlineData("a,b,TRY,1,extra")]
    [InlineData("\na,b,TRY,1")]
    public void Rejects_wrong_column_count(string row) => AssertRejected(Header + "\n" + row, "invalid_columns");

    [Theory]
    [InlineData("a,\"unterminated,TRY,1")]
    [InlineData("a,b\"c,TRY,1")]
    [InlineData("a,\"b\"c,TRY,1")]
    [InlineData("a,\"b\" ,TRY,1")]
    public void Rejects_malformed_quotes_without_accepting_preceding_rows(string row) =>
        AssertRejected(Header + "\nvalid,ref,TRY,1\n" + row, "invalid_csv");

    [Theory]
    [InlineData("1.2")]
    [InlineData("\"1,000\"")]
    [InlineData("1e3")]
    [InlineData("9223372036854775808")]
    [InlineData("-1")]
    [InlineData("")]
    public void Rejects_noninteger_negative_or_out_of_range_amounts(string amount) =>
        AssertRejected(Header + "\na,r,TRY," + amount, "invalid_amount");

    [Fact]
    public void CSV_and_JSON_share_normalization_and_validation()
    {
        var csv = CsvRecords.Read(Header + "\n a , Ref , eur ,42\nb,other,TRY,0");
        var json = RecordValidation.NormalizeAndValidate([new(" a ", " Ref ", " eur ", 42), new("b", "other", "TRY", 0)]);
        Assert.Empty(csv.Errors);
        Assert.Equal(json.Records, csv.Records);
        AssertRejected(Header + "\na,r,TRY,1\n a ,other,EUR,2", "duplicate_id");
        AssertRejected(Header + "\na,r,USD,1", "unsupported_currency");
        AssertRejected(Header + "\na,\"\",TRY,1", "required");
    }

    [Fact]
    public void Rejects_empty_dataset_and_limits_records_and_errors()
    {
        AssertRejected(Header + "\r\n", "required");
        string rows = string.Join('\n', Enumerable.Range(1, 10_000).Select(i => $"{i},r,TRY,1"));
        Assert.Equal(10_000, CsvRecords.Read(Header + "\n" + rows).Records.Count);
        AssertRejected(Header + "\n" + rows + "\nextra,r,TRY,1\n", "too_many_records");
        var malformed = CsvRecords.Read(Header + "\n" + string.Join('\n', Enumerable.Repeat("a,r,TRY,no", 101)));
        Assert.Empty(malformed.Records);
        Assert.Equal(100, malformed.Errors.Count);
    }

    private static void AssertRejected(string csv, string code)
    {
        var result = CsvRecords.Read(csv);
        Assert.Empty(result.Records);
        Assert.Contains(result.Errors, error => error.Code == code);
    }
}
