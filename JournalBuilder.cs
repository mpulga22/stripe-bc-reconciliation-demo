namespace StripeBcReconDemo;

/// <summary>
/// Turns one Stripe payout and its balance transactions into Business
/// Central journal lines, then checks that the Stripe clearing account nets
/// to zero. If it does, the bank deposit is fully explained.
///
/// Posting map used here:
///   Charge  -> credit the customer,  debit Stripe clearing (gross amount)
///   Refund  -> debit the customer,   credit Stripe clearing
///   Fees    -> debit Stripe fees,    credit Stripe clearing (one line per payout)
///   Payout  -> debit the bank,       credit Stripe clearing (net amount)
/// </summary>
public static class JournalBuilder
{
    // Currencies Stripe treats as having no minor unit (no cents).
    private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg",
        "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf",
    };

    public static ReconciliationResult Build(
        Payout payout,
        IReadOnlyList<BalanceTransaction> transactions,
        AccountMap accounts,
        string documentNo)
    {
        var lines = new List<JournalLine>();
        var warnings = new List<string>();
        var postingDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(payout.ArrivalDate).UtcDateTime);

        // Charges carry the Business Central customer number in their metadata.
        // Remember it per charge so refunds can find the right customer later.
        var customerByCharge = new Dictionary<string, string>();

        long grossMinor = 0, refundsMinor = 0, feesMinor = 0, expectedNetMinor = 0;
        int chargeCount = 0, refundCount = 0;

        foreach (var txn in transactions.OrderBy(t => t.Created))
        {
            if (!string.Equals(txn.Currency, payout.Currency, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{txn.Id}: currency {txn.Currency.ToUpperInvariant()} differs from the payout's {payout.Currency.ToUpperInvariant()}; skipped.");
                continue;
            }

            switch (txn.Type)
            {
                case "charge":
                case "payment":
                {
                    var chargeId = txn.Source?.Id ?? txn.Id;
                    var customerNo = FindCustomer(txn.Source, accounts, chargeId, warnings);
                    customerByCharge[chargeId] = customerNo;

                    lines.Add(new JournalLine(
                        postingDate, "Payment", documentNo, chargeId,
                        "Customer", customerNo,
                        Truncate($"Stripe payment {txn.Description ?? chargeId}", 100),
                        -ToMajor(txn.Amount, txn.Currency),
                        "G/L Account", accounts.StripeClearingGl));

                    chargeCount++;
                    grossMinor += txn.Amount;
                    feesMinor += txn.Fee;
                    expectedNetMinor += txn.Net;
                    break;
                }

                case "refund":
                case "payment_refund":
                {
                    var refundId = txn.Source?.Id ?? txn.Id;
                    var chargeId = txn.Source?.Charge;
                    string customerNo;
                    if (chargeId is not null && customerByCharge.TryGetValue(chargeId, out var known))
                    {
                        customerNo = known;
                    }
                    else
                    {
                        customerNo = accounts.UnmappedCustomerNo;
                        warnings.Add($"{refundId}: refund for a charge outside this payout; posted to {customerNo} for review.");
                    }

                    lines.Add(new JournalLine(
                        postingDate, "Refund", documentNo, refundId,
                        "Customer", customerNo,
                        Truncate($"Stripe refund of {chargeId ?? "unknown charge"}", 100),
                        -ToMajor(txn.Amount, txn.Currency), // txn.Amount is negative, so this debits the customer
                        "G/L Account", accounts.StripeClearingGl));

                    refundCount++;
                    refundsMinor += txn.Amount;
                    feesMinor += txn.Fee;
                    expectedNetMinor += txn.Net;
                    break;
                }

                default:
                    // Disputes, adjustments and the like get their own rules in a later step.
                    warnings.Add($"{txn.Id}: type '{txn.Type}' is not handled yet, so it is left out of the journal.");
                    break;
            }
        }

        if (feesMinor != 0)
        {
            lines.Add(new JournalLine(
                postingDate, "", documentNo, payout.Id,
                "G/L Account", accounts.StripeFeesGl,
                $"Stripe fees for payout {payout.Id}",
                ToMajor(feesMinor, payout.Currency),
                "G/L Account", accounts.StripeClearingGl));
        }

        lines.Add(new JournalLine(
            postingDate, "", documentNo, payout.Id,
            "Bank Account", accounts.BankAccountNo,
            $"Stripe payout {payout.Id}",
            ToMajor(payout.Amount, payout.Currency),
            "G/L Account", accounts.StripeClearingGl));

        // Every line uses Stripe clearing as its balancing account, so the
        // clearing account moves by the opposite of each line's amount.
        var clearingBalance = -lines
            .Where(l => l.BalAccountType == "G/L Account" && l.BalAccountNo == accounts.StripeClearingGl)
            .Sum(l => l.Amount);

        return new ReconciliationResult(
            payout,
            lines,
            chargeCount,
            ToMajor(grossMinor, payout.Currency),
            refundCount,
            ToMajor(refundsMinor, payout.Currency),
            ToMajor(feesMinor, payout.Currency),
            ToMajor(expectedNetMinor, payout.Currency),
            ToMajor(payout.Amount, payout.Currency),
            clearingBalance,
            warnings);
    }

    private static string FindCustomer(SourceObject? source, AccountMap accounts, string chargeId, List<string> warnings)
    {
        if (source?.Metadata is { } metadata
            && metadata.TryGetValue(accounts.CustomerMetadataKey, out var customerNo)
            && !string.IsNullOrWhiteSpace(customerNo))
        {
            return customerNo;
        }

        warnings.Add($"{chargeId}: no '{accounts.CustomerMetadataKey}' in the charge metadata; posted to {accounts.UnmappedCustomerNo} for review.");
        return accounts.UnmappedCustomerNo;
    }

    private static decimal ToMajor(long minorUnits, string currency) =>
        ZeroDecimalCurrencies.Contains(currency) ? minorUnits : minorUnits / 100m;

    // Business Central limits Description to 100 characters.
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
