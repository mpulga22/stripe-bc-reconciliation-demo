using System.Text.Json;

namespace StripeBcReconDemo.Tests;

/// <summary>
/// Unit tests for the posting rules in <see cref="JournalBuilder"/>.
///
/// Each test follows the same three steps:
///   Arrange: build a small Stripe payout and its transactions by hand
///   Act:     run JournalBuilder.Build
///   Assert:  check the journal lines and the reconciliation result
///
/// Amounts are in cents, the way Stripe sends them: 12000 = 120.00 USD.
/// </summary>
public class JournalBuilderTests
{
    private static readonly AccountMap Accounts = new();

    // ---- Reconciliation ------------------------------------------------

    [Fact]
    public void Payout_reconciles_when_bank_amount_matches_charges_minus_refunds_and_fees()
    {
        // Arrange: two charges and one refund. The bank receives the sum of their nets.
        var charge1 = Charge("ch_1", amount: 12000, fee: 378, customerNo: "C10000"); // net 116.22
        var charge2 = Charge("ch_2", amount: 4500, fee: 161, customerNo: "C20000");  // net 43.39
        var refund = Refund("re_1", chargeId: "ch_2", amount: 4500);                // net -45.00
        var payout = PayoutOf(11622 + 4339 - 4500);                                  // 114.61

        // Act
        var result = Build(payout, charge1, charge2, refund);

        // Assert
        Assert.True(result.IsReconciled);
        Assert.Equal(0m, result.Difference);
        Assert.Equal(0m, result.ClearingBalance);
    }

    [Fact]
    public void Payout_does_not_reconcile_when_bank_amount_is_one_cent_short()
    {
        var charge = Charge("ch_1", amount: 10000, fee: 320, customerNo: "C10000"); // net 96.80
        var payout = PayoutOf(9679);                                                // 96.79, one cent short

        var result = Build(payout, charge);

        Assert.False(result.IsReconciled);
        Assert.Equal(-0.01m, result.Difference);
    }

    // ---- Charges ---------------------------------------------------------

    [Fact]
    public void Charge_credits_the_customer_from_the_charge_metadata()
    {
        var charge = Charge("ch_1", amount: 12000, fee: 378, customerNo: "C10000");

        var result = Build(PayoutOf(11622), charge);

        var line = Assert.Single(result.Lines, l => l.AccountType == "Customer");
        Assert.Equal("C10000", line.AccountNo);
        Assert.Equal(-120.00m, line.Amount); // negative = credit to the customer
        Assert.Equal(Accounts.StripeClearingGl, line.BalAccountNo);
        Assert.Equal("ch_1", line.ExternalDocumentNo);
    }

    [Fact]
    public void Charge_without_a_customer_number_goes_to_the_review_account_with_a_warning()
    {
        var charge = Charge("ch_1", amount: 2500, fee: 103, customerNo: null);

        var result = Build(PayoutOf(2397), charge);

        var line = Assert.Single(result.Lines, l => l.AccountType == "Customer");
        Assert.Equal(Accounts.UnmappedCustomerNo, line.AccountNo);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("ch_1", warning);

        // The money is still explained: flagged for review, not lost.
        Assert.True(result.IsReconciled);
    }

    // ---- Refunds ---------------------------------------------------------

    [Fact]
    public void Refund_debits_the_same_customer_as_its_charge()
    {
        var charge1 = Charge("ch_1", amount: 12000, fee: 378, customerNo: "C10000");
        var charge2 = Charge("ch_2", amount: 4500, fee: 161, customerNo: "C20000");
        var refund = Refund("re_1", chargeId: "ch_2", amount: 4500);

        var result = Build(PayoutOf(11622 + 4339 - 4500), charge1, charge2, refund);

        var refundLine = Assert.Single(result.Lines, l => l.DocumentType == "Refund");
        Assert.Equal("C20000", refundLine.AccountNo);
        Assert.Equal(45.00m, refundLine.Amount); // positive = debit to the customer
    }

    [Fact]
    public void Refund_for_a_charge_outside_this_payout_goes_to_the_review_account()
    {
        var charge = Charge("ch_2", amount: 12000, fee: 378, customerNo: "C10000");
        var refund = Refund("re_1", chargeId: "ch_from_last_month", amount: 2000);

        var result = Build(PayoutOf(11622 - 2000), charge, refund);

        var refundLine = Assert.Single(result.Lines, l => l.DocumentType == "Refund");
        Assert.Equal(Accounts.UnmappedCustomerNo, refundLine.AccountNo);
        Assert.Contains(result.Warnings, w => w.Contains("re_1"));
    }

    // ---- Fees and payout lines --------------------------------------------

    [Fact]
    public void Fees_are_posted_as_one_line_per_payout()
    {
        var charge1 = Charge("ch_1", amount: 12000, fee: 378, customerNo: "C10000");
        var charge2 = Charge("ch_2", amount: 4500, fee: 161, customerNo: "C20000");

        var result = Build(PayoutOf(11622 + 4339), charge1, charge2);

        var feeLine = Assert.Single(result.Lines, l => l.AccountNo == Accounts.StripeFeesGl);
        Assert.Equal(5.39m, feeLine.Amount); // 3.78 + 1.61, a debit to Stripe fees
    }

    [Fact]
    public void Payout_line_debits_the_bank_for_the_amount_that_arrived()
    {
        var charge = Charge("ch_1", amount: 12000, fee: 378, customerNo: "C10000");

        var result = Build(PayoutOf(11622), charge);

        var bankLine = Assert.Single(result.Lines, l => l.AccountType == "Bank Account");
        Assert.Equal(Accounts.BankAccountNo, bankLine.AccountNo);
        Assert.Equal(116.22m, bankLine.Amount);
    }

    // ---- Edge cases ------------------------------------------------------

    [Fact]
    public void Transaction_in_another_currency_is_skipped_with_a_warning()
    {
        var usdCharge = Charge("ch_1", amount: 12000, fee: 378, customerNo: "C10000");
        var eurCharge = Charge("ch_2", amount: 5000, fee: 175, customerNo: "C30000", currency: "eur");

        var result = Build(PayoutOf(11622), usdCharge, eurCharge);

        Assert.Equal(1, result.ChargeCount);
        Assert.Contains(result.Warnings, w => w.Contains("EUR"));
        Assert.True(result.IsReconciled);
    }

    [Fact]
    public void Unhandled_transaction_type_is_left_out_of_the_journal_with_a_warning()
    {
        var charge = Charge("ch_1", amount: 12000, fee: 378, customerNo: "C10000");
        var adjustment = new BalanceTransaction(
            "txn_adj_1", "adjustment", -500, 0, -500, "usd", Created: 3, "Manual adjustment", Source: null);

        var result = Build(PayoutOf(11622), charge, adjustment);

        Assert.DoesNotContain(result.Lines, l => l.ExternalDocumentNo == "txn_adj_1");
        Assert.Contains(result.Warnings, w => w.Contains("adjustment"));
    }

    // A Theory runs the same test once for each InlineData row.
    // Stripe sends most currencies in cents, but some (like JPY) have no cents at all.
    [Theory]
    [InlineData("usd", 12000L, 120.0)]
    [InlineData("jpy", 12000L, 12000.0)]
    [InlineData("JPY", 500L, 500.0)]
    public void Amounts_convert_from_Stripe_minor_units_by_currency(string currency, long minorUnits, double expected)
    {
        var charge = Charge("ch_1", amount: minorUnits, fee: 0, customerNo: "C10000", currency: currency);

        var result = Build(PayoutOf(minorUnits, currency), charge);

        var line = Assert.Single(result.Lines, l => l.AccountType == "Customer");
        Assert.Equal(-(decimal)expected, line.Amount);
    }

    // ---- The sample data from the demo ----------------------------------

    [Fact]
    public void Sample_payout_in_the_data_folder_reconciles()
    {
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var payout = JsonSerializer.Deserialize<Payout>(File.ReadAllText(FindDataFile("payout.json")), json)!;
        var transactions = JsonSerializer.Deserialize<StripeList<BalanceTransaction>>(
            File.ReadAllText(FindDataFile("balance_transactions.json")), json)!.Data;

        var result = JournalBuilder.Build(payout, transactions, Accounts, "STP-0001");

        Assert.True(result.IsReconciled);
        Assert.Equal(6, result.ChargeCount);
        Assert.Equal(1, result.RefundCount);
        Assert.Equal(16.02m, result.Fees);
        Assert.Equal(428.98m, result.ActualPayout);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("ch_demo005", warning);
    }

    // ---- Helpers ----------------------------------------------------------

    private static ReconciliationResult Build(Payout payout, params BalanceTransaction[] transactions) =>
        JournalBuilder.Build(payout, transactions, Accounts, "STP-TEST");

    private static Payout PayoutOf(long amount, string currency = "usd") =>
        new("po_test", amount, currency, ArrivalDate: 1793318400, Status: "paid");

    // Charges get Created = 1 and refunds Created = 2, so refunds always sort after
    // the charges they belong to. Ties keep their order, because OrderBy is stable.
    private static BalanceTransaction Charge(string id, long amount, long fee, string? customerNo, string currency = "usd")
    {
        var metadata = new Dictionary<string, string>();
        if (customerNo is not null)
        {
            metadata[Accounts.CustomerMetadataKey] = customerNo;
        }

        return new BalanceTransaction(
            $"txn_{id}", "charge", amount, fee, amount - fee, currency, Created: 1,
            $"Invoice for {id}", new SourceObject(id, "charge", Charge: null, metadata));
    }

    private static BalanceTransaction Refund(string id, string chargeId, long amount) =>
        new($"txn_{id}", "refund", -amount, 0, -amount, "usd", Created: 2,
            $"Refund of {chargeId}", new SourceObject(id, "refund", chargeId, new Dictionary<string, string>()));

    // Walks up from the test's bin folder until it finds the demo's data folder.
    private static string FindDataFile(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find data/{fileName} above {AppContext.BaseDirectory}.");
    }
}
