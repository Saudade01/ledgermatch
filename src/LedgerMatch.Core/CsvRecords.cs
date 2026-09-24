using System.Globalization;
using System.Text;

namespace LedgerMatch.Core;

public static class CsvRecords
{
    private static readonly string[] Header = ["sourceRecordId", "reference", "currency", "amountMinor"];

    public static ValidationResult Read(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return Failure(0, "header", "invalid_header", "CSV header is required.");
        if (csv[0] == '\uFEFF') csv = csv[1..];
        var parsed = Parse(csv);
        if (parsed.Error is not null) return new([], [parsed.Error]);
        if (parsed.Rows.Count == 0 || !parsed.Rows[0].SequenceEqual(Header, StringComparer.Ordinal))
            return Failure(0, "header", "invalid_header", "Expected header: sourceRecordId,reference,currency,amountMinor.");

        List<PaymentRecord?> records = [];
        List<ValidationIssue> errors = [];
        for (int row = 1; row < parsed.Rows.Count && errors.Count < RecordValidation.MaxErrors; row++)
        {
            string[] fields = parsed.Rows[row];
            if (fields.Length != 4)
            {
                errors.Add(new(row, "record", "invalid_columns", "Each CSV record must contain exactly four columns."));
                continue;
            }
            if (!long.TryParse(fields[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long amount))
            {
                errors.Add(new(row, "amountMinor", "invalid_amount", "Amount must be a 64-bit integer in minor units, without separators."));
                continue;
            }
            records.Add(new(fields[0], fields[1], fields[2], amount));
        }
        // Parse failures reject the whole upload. Do not normalize a partial record list,
        // since doing so would report incorrect row numbers or accidentally accept data.
        return errors.Count > 0 ? new([], errors) : RecordValidation.NormalizeAndValidate(records);
    }

    private static ValidationResult Failure(int row, string field, string code, string message) =>
        new([], [new(row, field, code, message)]);

    // Small strict RFC4180 reader: quotes are permitted only at field boundaries, embedded
    // quotes are doubled, and quoted fields may contain commas and line breaks.
    private static (List<string[]> Rows, ValidationIssue? Error) Parse(string csv)
    {
        List<string[]> rows = [];
        List<string> fields = [];
        StringBuilder field = new();
        bool quoted = false;
        bool closedQuote = false;
        bool fieldStarted = false;

        for (int i = 0; i < csv.Length; i++)
        {
            char c = csv[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                else { quoted = false; closedQuote = true; }
                continue;
            }
            if (closedQuote && c is not (',' or '\r' or '\n')) return InvalidQuote();
            if (c == '"')
            {
                if (fieldStarted) return InvalidQuote();
                quoted = true;
                fieldStarted = true;
            }
            else if (c == ',') FinishField();
            else if (c is '\r' or '\n')
            {
                FinishField();
                rows.Add(fields.ToArray());
                fields.Clear();
                if (rows.Count > RecordValidation.MaxRecords + 1) return TooMany();
                if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
            }
            else { field.Append(c); fieldStarted = true; }
        }
        if (quoted) return InvalidQuote();
        if (fieldStarted || fields.Count > 0)
        {
            FinishField();
            rows.Add(fields.ToArray());
        }
        return rows.Count > RecordValidation.MaxRecords + 1 ? TooMany() : (rows, null);

        void FinishField()
        {
            fields.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
            closedQuote = false;
        }

        (List<string[]>, ValidationIssue) InvalidQuote() =>
            ([], new(rows.Count, "record", "invalid_csv", "Malformed CSV quoting."));
        (List<string[]>, ValidationIssue) TooMany() =>
            ([], new(0, "records", "too_many_records", $"At most {RecordValidation.MaxRecords} records are allowed."));
    }
}
