using LedgerMatch.Core;

namespace LedgerMatch.Core.Tests;

public sealed class ValidationTests
{
    [Fact]
    public void Normalizes_fields_but_preserves_reference_case_and_minor_units()
    {
        PaymentRecord original = new("  source-1 ", " Ref-iI ", " eur ", long.MaxValue);
        var result = RecordValidation.NormalizeAndValidate([original]);
        Assert.Empty(result.Errors);
        Assert.Equal(new("source-1", "Ref-iI", "EUR", long.MaxValue), Assert.Single(result.Records));
        Assert.Equal("  source-1 ", original.SourceRecordId);
    }

    [Fact]
    public void Zero_is_a_valid_amount()
    {
        var result = RecordValidation.NormalizeAndValidate([new("id", "ref", "TRY", 0)]);
        Assert.Empty(result.Errors);
        Assert.Equal(0L, Assert.Single(result.Records).AmountMinor);
    }

    [Fact]
    public void Invalid_rows_reject_entire_dataset_and_report_one_based_rows()
    {
        var result = RecordValidation.NormalizeAndValidate([
            new("valid", "ref", "TRY", 42), null,
            new("", " ", "USD", -1), new("x", new string('r', 129), "EUR", 1)]);
        Assert.Empty(result.Records);
        Assert.Contains(result.Errors, e => e.Row == 2 && e.Field == "record");
        Assert.Contains(result.Errors, e => e.Row == 3 && e.Field == "sourceRecordId");
        Assert.Contains(result.Errors, e => e.Row == 3 && e.Field == "reference");
        Assert.Contains(result.Errors, e => e.Row == 3 && e.Field == "currency");
        Assert.Contains(result.Errors, e => e.Row == 3 && e.Field == "amountMinor");
        Assert.Contains(result.Errors, e => e.Row == 4 && e.Code == "too_long");
    }

    [Fact]
    public void IDs_are_unique_after_trimming_but_remain_case_sensitive()
    {
        var duplicate = RecordValidation.NormalizeAndValidate([
            new("id", "a", "TRY", 1), new(" id ", "b", "EUR", 2)]);
        Assert.Empty(duplicate.Records);
        Assert.Contains(duplicate.Errors, e => e.Row == 2 && e.Code == "duplicate_id");
        Assert.Empty(RecordValidation.NormalizeAndValidate([
            new("id", "a", "TRY", 1), new("ID", "a", "TRY", 1)]).Errors);
    }

    [Fact]
    public void Enforces_dataset_and_error_limits()
    {
        Assert.NotEmpty(RecordValidation.NormalizeAndValidate(null).Errors);
        Assert.NotEmpty(RecordValidation.NormalizeAndValidate([]).Errors);
        PaymentRecord?[] valid = Enumerable.Range(0, 10_000)
            .Select(i => (PaymentRecord?)new PaymentRecord(i.ToString(), "r", "TRY", 1)).ToArray();
        Assert.Equal(10_000, RecordValidation.NormalizeAndValidate(valid).Records.Count);
        var tooMany = RecordValidation.NormalizeAndValidate([.. valid, new("extra", "r", "TRY", 1)]);
        Assert.Equal("too_many_records", Assert.Single(tooMany.Errors).Code);
        var bad = RecordValidation.NormalizeAndValidate(Enumerable.Repeat<PaymentRecord?>(new("", "", "", -1), 100).ToArray());
        Assert.Empty(bad.Records);
        Assert.Equal(100, bad.Errors.Count);
    }

    [Fact]
    public void Null_strings_from_JSON_are_validation_errors()
    {
        var result = RecordValidation.NormalizeAndValidate([new(null!, null!, null!, 1)]);
        Assert.Empty(result.Records);
        Assert.Equal(3, result.Errors.Count);
    }
}
