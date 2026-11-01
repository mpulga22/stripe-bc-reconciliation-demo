using System.Globalization;
using System.Text;
using System.Text.Json;
using StripeBcReconDemo;

// Step 1 of the demo: reconcile one Stripe payout from sample files.
// Later steps swap the files for the Stripe API (test mode) and post the
// journal to a Business Central sandbox.

var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "output");
Directory.CreateDirectory(outputDir);

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

var payout = Load<Payout>("payout.json");
var transactions = Load<StripeList<BalanceTransaction>>("balance_transactions.json").Data;

// Business Central caps Document No. at 20 characters and real payout IDs
// are longer, so the payout ID goes in External Document No. instead.
var result = JournalBuilder.Build(payout, transactions, new AccountMap(), documentNo: "STP-0001");

PrintReport(result);

var csvPath = Path.Combine(outputDir, $"journal_{payout.Id}.csv");
WriteCsv(csvPath, result.Lines);
Console.WriteLine();
Console.WriteLine($"Journal: {result.Lines.Count} lines written to {csvPath}");

return result.IsReconciled ? 0 : 1;

T Load<T>(string fileName)
{
    var path = Path.Combine(dataDir, fileName);
    return JsonSerializer.Deserialize<T>(File.ReadAllText(path), json)
        ?? throw new InvalidDataException($"{fileName} is empty or not valid JSON.");
}

static void PrintReport(ReconciliationResult r)
{
    var c = CultureInfo.InvariantCulture;
    string Money(decimal value) => value.ToString("N2", c).PadLeft(12);
    var arrival = DateTimeOffset.FromUnixTimeSeconds(r.Payout.ArrivalDate).UtcDateTime.ToString("yyyy-MM-dd", c);

    Console.WriteLine($"Payout {r.Payout.Id} | arrives {arrival} | {r.Payout.Currency.ToUpperInvariant()}");
    Console.WriteLine(new string('-', 44));
    Console.WriteLine($"  Charges ({r.ChargeCount})".PadRight(28) + Money(r.GrossCharges));
    Console.WriteLine($"  Refunds ({r.RefundCount})".PadRight(28) + Money(r.Refunds));
    Console.WriteLine("  Stripe fees".PadRight(28) + Money(-r.Fees));
    Console.WriteLine("  Expected payout".PadRight(28) + Money(r.ExpectedPayout));
    Console.WriteLine("  Actual payout".PadRight(28) + Money(r.ActualPayout));
    Console.WriteLine("  Difference".PadRight(28) + Money(r.Difference) + (r.Difference == 0m ? "   OK" : "   MISMATCH"));
    Console.WriteLine("  Clearing after posting".PadRight(28) + Money(r.ClearingBalance) + (r.ClearingBalance == 0m ? "   OK" : "   NOT ZERO"));

    Console.WriteLine();
    Console.WriteLine(r.IsReconciled
        ? "Result: payout reconciles. Every cent in the bank is explained."
        : "Result: payout does NOT reconcile. Check the warnings below.");

    if (r.Warnings.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Needs review ({r.Warnings.Count}):");
        foreach (var warning in r.Warnings)
        {
            Console.WriteLine($"  - {warning}");
        }
    }
}

static void WriteCsv(string path, IReadOnlyList<JournalLine> lines)
{
    var c = CultureInfo.InvariantCulture;
    var sb = new StringBuilder();
    sb.AppendLine("Posting Date,Document Type,Document No.,External Document No.,Account Type,Account No.,Description,Amount,Bal. Account Type,Bal. Account No.");
    foreach (var l in lines)
    {
        sb.AppendLine(string.Join(',',
            l.PostingDate.ToString("yyyy-MM-dd", c),
            Csv(l.DocumentType),
            Csv(l.DocumentNo),
            Csv(l.ExternalDocumentNo),
            Csv(l.AccountType),
            Csv(l.AccountNo),
            Csv(l.Description),
            l.Amount.ToString("0.00", c),
            Csv(l.BalAccountType),
            Csv(l.BalAccountNo)));
    }
    File.WriteAllText(path, sb.ToString());
}

static string Csv(string value) =>
    value.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
