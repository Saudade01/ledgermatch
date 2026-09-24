using System.Text.Json;
using LedgerMatch.Core;

namespace LedgerMatch.Core.Tests;

public sealed class ReconcilerTests
{
    [Fact]
    public void Classifies_exact_matches_amount_mismatches_and_absent_records()
    {
        PaymentRecord[] left = [new("l1", "same", "TRY", 500), new("l2", "amount", "EUR", 100), new("l3", "left", "TRY", 1)];
        PaymentRecord[] right = [new("r1", "same", "TRY", 500), new("r2", "amount", "EUR", 99), new("r3", "right", "TRY", 1)];
        var groups = Reconciler.Compare(left, right);
        Assert.Equal(["amount", "left", "right", "same"], groups.Select(g => g.Reference));
        Assert.Equal(["amount_mismatch", "missing_right", "missing_left", "matched"], groups.Select(g => g.Status));
    }

    [Fact]
    public void Duplicate_takes_precedence_over_missing_or_equal_summed_amounts()
    {
        PaymentRecord[] left = [new("b", "dup", "TRY", 40), new("a", "dup", "TRY", 60)];
        var missing = Assert.Single(Reconciler.Compare(left, []));
        Assert.Equal("duplicate", missing.Status);
        Assert.Equal(["a", "b"], missing.Left.Select(r => r.SourceRecordId));
        var sameTotal = Assert.Single(Reconciler.Compare(left, [new("r", "dup", "TRY", 100)]));
        Assert.Equal("duplicate", sameTotal.Status);
        Assert.Equal("duplicate", Assert.Single(Reconciler.Compare([], left)).Status);
    }

    [Fact]
    public void Currency_mismatch_never_matches_and_reference_comparison_is_ordinal()
    {
        var groups = Reconciler.Compare([new("l1", "Abc", "TRY", 100), new("l2", "abc", "EUR", 100)],
            [new("r1", "Abc", "EUR", 100)]);
        Assert.Equal(3, groups.Count);
        Assert.Equal(["EUR", "TRY", "EUR"], groups.Select(g => g.Currency));
        Assert.All(groups, group => Assert.StartsWith("missing_", group.Status));
    }

    [Fact]
    public void Every_record_survives_once_and_order_is_independent_of_input_order()
    {
        PaymentRecord[] left = Enumerable.Range(0, 71).Select(i => new PaymentRecord($"l{i:D3}", $"ref{i % 19:D2}", i % 2 == 0 ? "TRY" : "EUR", i * 19)).ToArray();
        PaymentRecord[] right = Enumerable.Range(0, 53).Select(i => new PaymentRecord($"r{i:D3}", $"ref{i % 23:D2}", i % 3 == 0 ? "TRY" : "EUR", i * 13)).ToArray();
        var groups = Reconciler.Compare(left, right);
        Assert.Equal(left.OrderBy(r => r.SourceRecordId, StringComparer.Ordinal), groups.SelectMany(g => g.Left).OrderBy(r => r.SourceRecordId, StringComparer.Ordinal));
        Assert.Equal(right.OrderBy(r => r.SourceRecordId, StringComparer.Ordinal), groups.SelectMany(g => g.Right).OrderBy(r => r.SourceRecordId, StringComparer.Ordinal));
        Assert.Equal(JsonSerializer.Serialize(groups), JsonSerializer.Serialize(Reconciler.Compare(left.Reverse().ToArray(), right.Reverse().ToArray())));
    }

    [Fact]
    public void Large_minor_amounts_compare_without_floating_point_rounding_or_overflow()
    {
        Assert.Equal("matched", Assert.Single(Reconciler.Compare([new("l", "a", "TRY", long.MaxValue)], [new("r", "a", "TRY", long.MaxValue)])).Status);
        Assert.Equal("amount_mismatch", Assert.Single(Reconciler.Compare([new("l", "a", "TRY", long.MaxValue)], [new("r", "a", "TRY", long.MaxValue - 1)])).Status);
        Assert.Empty(Reconciler.Compare([], []));
    }
}
