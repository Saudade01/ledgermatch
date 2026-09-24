namespace LedgerMatch.Core;

public sealed record MatchGroup(
    string Reference,
    string Currency,
    string Status,
    IReadOnlyList<PaymentRecord> Left,
    IReadOnlyList<PaymentRecord> Right);

public static class Reconciler
{
    // Callers validate datasets before storing them. There is deliberately no fuzzy matching,
    // currency conversion or summing of duplicates: ambiguous references need human review.
    public static IReadOnlyList<MatchGroup> Compare(
        IReadOnlyList<PaymentRecord> left, IReadOnlyList<PaymentRecord> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var leftGroups = left.GroupBy(record => (record.Reference, record.Currency))
            .ToDictionary(group => group.Key, group => group.OrderBy(record => record.SourceRecordId, StringComparer.Ordinal).ToArray());
        var rightGroups = right.GroupBy(record => (record.Reference, record.Currency))
            .ToDictionary(group => group.Key, group => group.OrderBy(record => record.SourceRecordId, StringComparer.Ordinal).ToArray());

        List<MatchGroup> result = [];
        foreach (var key in leftGroups.Keys.Union(rightGroups.Keys)
                     .OrderBy(key => key.Reference, StringComparer.Ordinal)
                     .ThenBy(key => key.Currency, StringComparer.Ordinal))
        {
            PaymentRecord[] l = leftGroups.GetValueOrDefault(key) ?? [];
            PaymentRecord[] r = rightGroups.GetValueOrDefault(key) ?? [];
            string status = l.Length > 1 || r.Length > 1 ? "duplicate"
                : l.Length == 0 ? "missing_left"
                : r.Length == 0 ? "missing_right"
                : l[0].AmountMinor == r[0].AmountMinor ? "matched"
                : "amount_mismatch";
            result.Add(new(key.Reference, key.Currency, status, l, r));
        }
        return result;
    }
}
