# Mortgage validation demo bundle

Drop the `.txt` files from this folder into the chat composer (paperclip → multi-select)
together with a prompt like:

> Please validate my mortgage application MTG-2026-0042.
> Property price 1,650,000 ILS, profession: software engineer (salaried), income band: high.

The Magentic Manager will plan five steps:

```
mortgage_intake → doc_requirements → doc_classifier → doc_authenticator → doc_report
```

Final answer is a JSON array — one row per required document.

## What each sample file represents

| File | Intended type | Authenticity hint |
|---|---|---|
| `government_id_front.txt` | GovernmentID | Genuine |
| `payslip_march_2026.txt` | PayslipLast3Months | Genuine |
| `payslip_april_2026.txt` | PayslipLast3Months | Genuine |
| `payslip_february_2026.txt` | PayslipLast3Months | Genuine |
| `bank_statement_april_2026.txt` | BankStatementLast3Months | Genuine |
| `employment_letter.txt` | EmploymentLetter | Genuine |
| `property_appraisal.txt` | PropertyAppraisal | Genuine |
| `government_id_back_TAMPERED.txt` | GovernmentID | **Tampered** (filename contains `tampered`) |
| `bank_statement_FORGED.txt` | BankStatementLast3Months | **Forged** (filename contains `forged`) |
| `tax_return_2025.txt` | TaxReturn | Not required for salaried high-income — should pass as informational |

## Expected demo outcomes

- Drop all the genuine files → every required doc Valid.
- Drop only `payslip_march_2026.txt` + `government_id_front.txt` → most required docs MissingRequired.
- Include `government_id_back_TAMPERED.txt` → NotGenuine row with remark "tamper score 0.71".
- Include `bank_statement_FORGED.txt` → NotGenuine row with remark "signature invalid".

The MCP tools (`MortgageDocumentTools`) are deterministic stubs: they classify and authenticate
based on filename keywords, so the demo always produces the same output for the same inputs.
