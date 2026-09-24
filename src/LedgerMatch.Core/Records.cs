namespace LedgerMatch.Core;

public sealed record PaymentRecord(string SourceRecordId, string Reference, string Currency, long AmountMinor);

public sealed record ValidationIssue(int Row, string Field, string Code, string Message);

public sealed record ValidationResult(IReadOnlyList<PaymentRecord> Records, IReadOnlyList<ValidationIssue> Errors);

public static class RecordValidation
{
    public const int MaxRecords = 10_000;
    public const int MaxErrors = 100;

    public static ValidationResult NormalizeAndValidate(IReadOnlyList<PaymentRecord?>? records)
    {
        if (records is null || records.Count == 0)
            return new([], [new(0, "records", "required", "Provide at least one record.")]);
        if (records.Count > MaxRecords)
            return new([], [new(0, "records", "too_many_records", $"At most {MaxRecords} records are allowed.")]);

        List<PaymentRecord> normalized = new(records.Count);
        List<ValidationIssue> errors = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        for (int i = 0; i < records.Count && errors.Count < MaxErrors; i++)
        {
            PaymentRecord? record = records[i];
            if (record is null)
            {
                Add(i + 1, "record", "required", "Record cannot be null.");
                continue;
            }

            string id = record.SourceRecordId?.Trim() ?? "";
            string reference = record.Reference?.Trim() ?? "";
            string currency = record.Currency?.Trim().ToUpperInvariant() ?? "";
            ValidateText(id, "sourceRecordId", i + 1);
            ValidateText(reference, "reference", i + 1);
            if (id.Length > 0 && !ids.Add(id))
                Add(i + 1, "sourceRecordId", "duplicate_id", "Source record IDs must be unique within a dataset.");
            if (currency is not ("TRY" or "EUR"))
                Add(i + 1, "currency", "unsupported_currency", "Currency must be TRY or EUR.");
            if (record.AmountMinor < 0)
                Add(i + 1, "amountMinor", "invalid_amount", "Amount must be nonnegative; refunds are not supported.");
            normalized.Add(new(id, reference, currency, record.AmountMinor));
        }

        return errors.Count > 0 ? new([], errors) : new(normalized, []);

        void Add(int row, string field, string code, string message)
        {
            if (errors.Count < MaxErrors) errors.Add(new(row, field, code, message));
        }

        void ValidateText(string value, string field, int row)
        {
            if (value.Length == 0) Add(row, field, "required", $"{field} is required.");
            else if (value.Contains('\0')) Add(row, field, "invalid_text", $"{field} cannot contain a NUL character.");
            else if (value.Length > 128) Add(row, field, "too_long", $"{field} must be at most 128 characters.");
        }
    }
}
