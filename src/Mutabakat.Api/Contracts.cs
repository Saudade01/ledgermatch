using Mutabakat.Core;

namespace Mutabakat.Api;

public sealed record DatasetRequest(string? Source, IReadOnlyList<PaymentRecord?>? Records);
public sealed record DatasetResponse(Guid Id, string Source, int RecordCount);
public sealed record ReconciliationRequest(Guid LeftDatasetId, Guid RightDatasetId);
public sealed record CurrencyTotals(string Currency, decimal LeftAmountMinor, decimal RightAmountMinor, decimal DifferenceMinor);
public sealed record ReconciliationSummary(
    IReadOnlyDictionary<string, int> GroupsByStatus,
    int LeftRecordCount,
    int RightRecordCount,
    IReadOnlyList<CurrencyTotals> TotalsByCurrency);
public sealed record ReconciliationResponse(
    Guid Id, Guid LeftDatasetId, Guid RightDatasetId,
    IReadOnlyList<MatchGroup> Groups, ReconciliationSummary Summary);
