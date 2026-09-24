# Matching and storage notes

## Matching by reference

Both inputs need a shared payment reference. Reference and currency identify a candidate group; equal amounts make a one-to-one candidate a match. References are case-sensitive after trimming. Guessing that two descriptions refer to the same payment could hide a missing record. Systems without shared references need a different workflow; they are outside this version.

## Amounts in kuruş and cents

TRY and EUR both use two decimal places. `amountMinor: 12500` means 125.00, not 12,500 units. Integer input avoids culture-dependent decimal parsing and rounding during comparison. CSV values such as `125.00` are rejected instead of guessed. Totals use a wider decimal representation so summing valid long amounts does not overflow a long. Negative amounts, other currencies and currency conversion are outside the contract.

## Duplicate references

Two left records of 2000 and one right record of 4000 could represent a split payment, duplicate import from upstream, or an incorrect reference. This prototype cannot distinguish those explanations. It reports the whole group as `duplicate`, retains every record, and does not sum it into a match. Unique `sourceRecordId` values are required within an import; repeated payment references are accepted because detecting them is part of the job.

## Retrying an import

A dataset is a snapshot. Retrying an import with the same key and normalized content returns its existing ID, even if record order changes or CSV is replaced by JSON. Reusing the key with different content produces a conflict. New content needs a new key. Unique database constraints are needed because two simultaneous requests can both pass an application-level existence check.

The reconciliation identity is an ordered pair of dataset IDs. Swapping left and right changes missing-side labels and the sign of totals, so it is a different comparison. Saved results can be retrieved after restarting the API.

## Invalid rows

Accepting only some rows makes it easy to interpret rejected rows as missing payments. Validation therefore returns row-level errors and persists no dataset when any row is invalid. Imports are limited to 2 MiB and 10,000 records because parsing and comparison happen in the request. Larger files would need background processing.

## Reports and totals

Each input record appears once in the output groups and once in the CSV export. Currency totals include duplicate and unmatched rows, so they describe the supplied datasets rather than only successful matches. Difference is left minus right, calculated separately for each currency. Text fields in exported CSV neutralize spreadsheet formula prefixes; this display protection does not change stored references.

## Deployment

Docker Compose runs the API and PostgreSQL locally. Requests run synchronously; there is no queue or external service involved in matching. The API has no authentication, so it should not be exposed publicly. Connecting a payment provider would also require handling its pagination, rate limits, and settlement rules.
