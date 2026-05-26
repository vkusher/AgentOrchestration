# Money-transfer sample requests

Test inputs for the `transfer_intake` agent. Mix of explicit and implicit phrasings,
English and Hebrew, with and without a named bank. When no bank is mentioned the
extractor must default `toBank` to `Discount` with `source: "default"`.

| File | Lang | Bank in text | Notes |
|---|---|---|---|
| req-en-01-explicit.txt | en | Discount (explicit) | Clean, all fields present |
| req-en-02-no-bank.txt  | en | none | Bank must default to Discount |
| req-en-03-other-bank.txt | en | Hapoalim | Bank must NOT be defaulted |
| req-en-04-ambiguous.txt | en | none | Missing currency, lower-confidence amount |
| req-he-01-no-bank.txt  | he | none | Hebrew, default to Discount |
| req-he-02-leumi.txt    | he | Leumi | Hebrew, named bank |
| req-en-05-form.pdf     | en | Discount | PDF form (extracted via Document Intelligence) |

Run against the API once the new agents are wired:

```pwsh
$body = @{
  message   = (Get-Content samples/money-transfer/req-en-02-no-bank.txt -Raw)
  sessionId = "transfer-smoke-1"
  mode      = "handoff"
} | ConvertTo-Json
curl.exe -sS -H "Content-Type: application/json" -X POST `
  https://***.azurewebsites.net/api/chat/stream --data $body
```

For the PDF, upload to the `transfer-requests` blob container first, then send the
blob URI in the request payload (intake tool will fetch + OCR via Document Intelligence).
