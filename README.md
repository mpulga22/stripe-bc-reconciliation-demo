# Stripe → Business Central reconciliation demo

A small .NET 10 console app that takes one Stripe payout and its balance
transactions, builds the Business Central general journal lines for it, and
checks that the payout reconciles to the cent.

Everything here is original sample data. It contains no client code or data.

## Run it

```
dotnet run
```

You'll see a reconciliation report in the terminal, and the journal is saved
to `output/journal_po_demo001.csv` with the same columns as Business Central's
General Journals page.

## What it does

| Stripe event | Business Central posting |
| --- | --- |
| Charge | Credit the customer, debit the Stripe clearing account (gross) |
| Refund | Debit the customer, credit Stripe clearing |
| Stripe fees | Debit Stripe fees, credit Stripe clearing (one line per payout) |
| Payout | Debit the bank account, credit Stripe clearing (net) |

If the clearing account nets to zero after posting, every cent that reached
the bank is explained. Charges without a `bc_customer_no` in their Stripe
metadata are posted to a review customer and flagged.

## Files

- `data/payout.json` and `data/balance_transactions.json`: sample Stripe data,
  shaped like the API's responses (balance transactions fetched with
  `expand[]=data.source`)
- `JournalBuilder.cs`: the posting rules and the reconciliation check
- `Models.cs`: Stripe and Business Central records, and the account map to
  change for a real chart of accounts
- `Program.cs`: loads the data, prints the report, writes the CSV

## Next steps

1. Read real payouts from Stripe test mode with a restricted, read-only key
2. Handle disputes and currency conversion
3. Post the journal to a Business Central sandbox through its API
