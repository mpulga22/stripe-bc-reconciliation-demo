namespace StripeBcReconDemo;

// ---- Stripe side --------------------------------------------------------
// These records mirror the parts of Stripe's API objects the demo needs.
// Amounts are in the currency's smallest unit (cents for USD), exactly as
// Stripe returns them.

/// <summary>Stripe's list envelope: { "object": "list", "data": [...], "has_more": false }.</summary>
public sealed record StripeList<T>(List<T> Data, bool HasMore);

/// <summary>A Stripe payout: the money that actually lands in the bank.</summary>
public sealed record Payout(
    string Id,
    long Amount,
    string Currency,
    long ArrivalDate,
    string Status);

/// <summary>
/// A Stripe balance transaction. Every charge, refund and payout moves the
/// Stripe balance through one of these. Fetched with expand[]=data.source,
/// so <see cref="Source"/> holds the charge or refund object itself.
/// </summary>
public sealed record BalanceTransaction(
    string Id,
    string Type,
    long Amount,
    long Fee,
    long Net,
    string Currency,
    long Created,
    string? Description,
    SourceObject? Source);

/// <summary>The expanded source of a balance transaction (a charge or a refund).</summary>
public sealed record SourceObject(
    string Id,
    string Object,
    string? Charge,
    Dictionary<string, string>? Metadata);

// ---- Business Central side ----------------------------------------------

/// <summary>
/// Where each kind of Stripe money should post. The numbers are examples:
/// replace them with the client's own chart of accounts.
/// </summary>
public sealed record AccountMap(
    string StripeClearingGl = "10950",
    string StripeFeesGl = "67100",
    string BankAccountNo = "WWB-OPERATING",
    string UnmappedCustomerNo = "STRIPE-UNMAPPED",
    string CustomerMetadataKey = "bc_customer_no");

/// <summary>
/// One line of a Business Central general journal, using the same columns
/// you see on the General Journals page. Amount is positive for a debit to
/// the Account and negative for a credit; the Bal. Account takes the
/// opposite side.
/// </summary>
public sealed record JournalLine(
    DateOnly PostingDate,
    string DocumentType,
    string DocumentNo,
    string ExternalDocumentNo,
    string AccountType,
    string AccountNo,
    string Description,
    decimal Amount,
    string BalAccountType,
    string BalAccountNo);

/// <summary>Everything the reconciliation found for one payout.</summary>
public sealed record ReconciliationResult(
    Payout Payout,
    IReadOnlyList<JournalLine> Lines,
    int ChargeCount,
    decimal GrossCharges,
    int RefundCount,
    decimal Refunds,
    decimal Fees,
    decimal ExpectedPayout,
    decimal ActualPayout,
    decimal ClearingBalance,
    IReadOnlyList<string> Warnings)
{
    public decimal Difference => ActualPayout - ExpectedPayout;
    public bool IsReconciled => Difference == 0m && ClearingBalance == 0m;
}
