using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace AgentHandoff.McpServer.Tools;

/// <summary>
/// Deterministic stubs powering the mortgage-document validation demo.
/// In production each tool would call: a rule engine (required-docs),
/// Azure AI Document Intelligence custom models (classify), a forgery/tamper
/// detector + issuer PKI cross-check (authenticate), and a persistence layer (report).
/// </summary>
[McpServerToolType]
public static class MortgageDocumentTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── 1. Ingest ────────────────────────────────────────────────────────────
    [McpServerTool, Description(
        "Register an incoming mortgage document bundle. Pass the applicationId and a JSON " +
        "array of submitted documents: [{\"filename\":\"payslip_march_2026.txt\",\"summary\":\"...\"}]. " +
        "Returns {bundleId, applicationId, docs:[{documentId, filename}]}.")]
    public static string IngestMortgageBundle(
        [Description("Mortgage application identifier, e.g. MTG-2026-0042.")] string applicationId,
        [Description("JSON array of {filename, summary} objects describing each uploaded document.")] string documentsJson)
    {
        var docs = ParseDocuments(documentsJson);
        var bundleId = $"BND-{applicationId}-{Math.Abs(applicationId.GetHashCode()) % 10000:D4}";

        var enriched = docs.Select((d, i) => new
        {
            documentId = $"DOC-{i + 1:D3}",
            filename   = d.Filename,
            sizeChars  = d.Summary?.Length ?? 0,
        }).ToArray();

        return Json(new { bundleId, applicationId, docs = enriched });
    }

    // ── 2. Required documents ────────────────────────────────────────────────
    [McpServerTool, Description(
        "Compute the list of required documents for a mortgage application based on customer profile " +
        "and loan parameters. Returns a JSON array of {docType, mandatory, reason}.")]
    public static string ComputeRequiredDocuments(
        [Description("Mortgage application identifier.")] string applicationId,
        [Description("Mortgage value in ILS.")] decimal mortgageValue,
        [Description("Profession label: 'salaried', 'self_employed', 'business_owner', 'retiree', 'student'.")] string profession,
        [Description("Income band: 'low', 'mid', 'high', 'very_high'.")] string incomeBand,
        [Description("Property type: 'apartment', 'house', 'commercial', 'land'.")] string propertyType,
        [Description("True if first-time buyer (eligible for government assistance program).")] bool firstTimeBuyer)
    {
        var prof   = (profession   ?? "salaried").Trim().ToLowerInvariant();
        var income = (incomeBand   ?? "mid").Trim().ToLowerInvariant();
        var pType  = (propertyType ?? "apartment").Trim().ToLowerInvariant();

        var reqs = new List<object>
        {
            new { docType = "GovernmentID",             mandatory = true, reason = "Identity verification (KYC)." },
            new { docType = "BankStatementLast3Months", mandatory = true, reason = "Verify cash flow and balance." },
        };

        if (prof is "salaried")
        {
            reqs.Add(new { docType = "PayslipLast3Months", mandatory = true, reason = "Verify employment income (salaried)." });
            reqs.Add(new { docType = "EmploymentLetter",   mandatory = true, reason = "Confirm employment status and tenure." });
        }
        else if (prof is "self_employed" or "business_owner")
        {
            reqs.Add(new { docType = "TaxReturn",                 mandatory = true, reason = "Verify income (self-employed) — last 2 fiscal years." });
            reqs.Add(new { docType = "BusinessFinancialStatement", mandatory = true, reason = "Verify business cash flow." });
        }
        else if (prof is "retiree")
        {
            reqs.Add(new { docType = "PensionStatement", mandatory = true, reason = "Verify pension income." });
        }

        if (mortgageValue >= 1_000_000m)
            reqs.Add(new { docType = "PropertyAppraisal", mandatory = true, reason = "Required for mortgages of 1,000,000 ILS or more." });

        if (pType is "house" or "commercial" or "land")
            reqs.Add(new { docType = "LandRegistryExtract", mandatory = true, reason = "Title verification for non-apartment property." });

        if (firstTimeBuyer && income is "low" or "mid")
            reqs.Add(new { docType = "GovernmentSubsidyForm", mandatory = false, reason = "Eligibility for first-time-buyer subsidy." });

        return Json(new { applicationId, mortgageValue, requirements = reqs });
    }

    // ── 3. Classify ──────────────────────────────────────────────────────────
    [McpServerTool, Description(
        "Classify each submitted document by its actual type. Pass the bundleId and a JSON array of " +
        "{documentId, filename, declaredType?, summary}. Returns array of {documentId, filename, " +
        "declaredType, detectedType, match, confidence}.")]
    public static string ClassifyDocument(
        [Description("Bundle identifier returned by IngestMortgageBundle.")] string bundleId,
        [Description("JSON array of documents to classify.")] string documentsJson)
    {
        var docs = ParseDocuments(documentsJson);
        var results = docs.Select(d =>
        {
            var detected = DetectType(d.Filename, d.Summary);
            var declared = d.DeclaredType ?? detected;   // when no declaration, accept detection
            var match    = string.Equals(declared, detected, StringComparison.OrdinalIgnoreCase);
            var conf     = detected == "Unknown" ? 0.35 : 0.92;
            return new { documentId = d.DocumentId, filename = d.Filename, declaredType = declared, detectedType = detected, match, confidence = conf };
        }).ToArray();

        return Json(new { bundleId, classifications = results });
    }

    // ── 4. Authenticate ──────────────────────────────────────────────────────
    [McpServerTool, Description(
        "Run authenticity checks (tamper detection, signature validity, issuer cross-check) per document. " +
        "Returns array of {documentId, filename, tamperScore, signatureValid, issuerVerified, anomalies, genuine}.")]
    public static string AuthenticateDocument(
        [Description("Bundle identifier.")] string bundleId,
        [Description("JSON array of documents to authenticate.")] string documentsJson)
    {
        var docs = ParseDocuments(documentsJson);
        var results = docs.Select(d =>
        {
            var lower = (d.Filename ?? string.Empty).ToLowerInvariant();
            var summary = (d.Summary ?? string.Empty).ToLowerInvariant();

            var suspectTamper = lower.Contains("tamper") || summary.Contains("tamper")
                                 || summary.Contains("pixel anomalies") || summary.Contains("microprint");
            var suspectForgery = lower.Contains("forge") || summary.Contains("invalid")
                                 || summary.Contains("does not verify") || summary.Contains("inflated");

            double tamperScore   = suspectTamper ? 0.71 : 0.06;
            bool signatureValid  = !suspectForgery;
            bool issuerVerified  = !(suspectTamper || suspectForgery);

            var anomalies = new List<string>();
            if (suspectTamper)  anomalies.Add("pixel anomalies along field baseline; microprint border missing");
            if (suspectForgery) anomalies.Add("digital signature chain does not verify");

            bool genuine = !suspectTamper && !suspectForgery;

            return new
            {
                documentId = d.DocumentId,
                filename = d.Filename,
                tamperScore,
                signatureValid,
                issuerVerified,
                anomalies = anomalies.ToArray(),
                genuine,
            };
        }).ToArray();

        return Json(new { bundleId, authentications = results });
    }

    // ── 5. Final report ──────────────────────────────────────────────────────
    [McpServerTool, Description(
        "Merge the requirements list, classifications, and authentications into the final per-document " +
        "validation report. Returns a JSON array of {documentType, submitted, validType, genuine, " +
        "validity, remarks}. validity ∈ {Valid, MissingRequired, WrongType, NotGenuine, NeedsReview}.")]
    public static string EmitValidationReport(
        [Description("Mortgage application identifier.")] string applicationId,
        [Description("Requirements JSON (output of ComputeRequiredDocuments).")] string requirementsJson,
        [Description("Classifications JSON (output of ClassifyDocument).")] string classificationsJson,
        [Description("Authentications JSON (output of AuthenticateDocument).")] string authenticationsJson)
    {
        var reqs    = ExtractArray(requirementsJson,    "requirements");
        var classes = ExtractArray(classificationsJson, "classifications");
        var auths   = ExtractArray(authenticationsJson, "authentications");

        var classByType = classes
            .GroupBy(c => c.GetProperty("detectedType").GetString() ?? "Unknown", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

        var authByDocId = auths.ToDictionary(
            a => a.GetProperty("documentId").GetString() ?? string.Empty,
            a => a,
            StringComparer.OrdinalIgnoreCase);

        var rows = new List<object>();

        foreach (var req in reqs)
        {
            var docType   = req.GetProperty("docType").GetString() ?? string.Empty;
            var mandatory = req.TryGetProperty("mandatory", out var m) && m.GetBoolean();

            if (!classByType.TryGetValue(docType, out var matched) || matched.Length == 0)
            {
                rows.Add(new
                {
                    documentType = docType,
                    submitted    = false,
                    validType    = false,
                    genuine      = (bool?)null,
                    validity     = mandatory ? "MissingRequired" : "Missing",
                    remarks      = mandatory
                        ? "Required document not submitted."
                        : "Optional document not submitted.",
                });
                continue;
            }

            // Use the first matching document of this type (extend to handle multi-instance if needed).
            var c = matched[0];
            var documentId = c.GetProperty("documentId").GetString() ?? string.Empty;
            var filename   = c.GetProperty("filename").GetString()   ?? string.Empty;

            var typeMatch = c.GetProperty("match").GetBoolean();
            authByDocId.TryGetValue(documentId, out var a);

            bool genuine        = a.ValueKind != JsonValueKind.Undefined && a.GetProperty("genuine").GetBoolean();
            double tamperScore  = a.ValueKind != JsonValueKind.Undefined ? a.GetProperty("tamperScore").GetDouble() : 0.0;
            bool sigValid       = a.ValueKind == JsonValueKind.Undefined || a.GetProperty("signatureValid").GetBoolean();

            string validity;
            string remarks;
            if (!typeMatch)
            {
                validity = "WrongType";
                var detected = c.GetProperty("detectedType").GetString() ?? "Unknown";
                remarks  = $"{filename} classified as {detected}, expected {docType}.";
            }
            else if (!genuine)
            {
                validity = "NotGenuine";
                var reasons = new List<string>();
                if (tamperScore >= 0.4) reasons.Add($"tamper score {tamperScore:F2}");
                if (!sigValid)          reasons.Add("signature invalid");
                if (reasons.Count == 0) reasons.Add("authenticity checks failed");
                remarks = $"{filename}: {string.Join("; ", reasons)}.";
            }
            else
            {
                validity = "Valid";
                remarks  = string.Empty;
            }

            rows.Add(new
            {
                documentType = docType,
                submitted    = true,
                validType    = typeMatch,
                genuine      = (bool?)genuine,
                validity,
                remarks,
            });
        }

        // Surface any submitted-but-not-required docs as informational rows.
        var requiredTypes = reqs
            .Select(r => r.GetProperty("docType").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in classes)
        {
            var detected = c.GetProperty("detectedType").GetString() ?? "Unknown";
            if (requiredTypes.Contains(detected)) continue;
            rows.Add(new
            {
                documentType = detected,
                submitted    = true,
                validType    = (bool?)null,
                genuine      = (bool?)null,
                validity     = "NotRequired",
                remarks      = $"{c.GetProperty("filename").GetString()} submitted but not required for this application.",
            });
        }

        return Json(new { applicationId, report = rows });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private static string DetectType(string? filename, string? summary)
    {
        var f = (filename ?? string.Empty).ToLowerInvariant();
        var s = (summary  ?? string.Empty).ToLowerInvariant();
        bool Has(string needle) => f.Contains(needle) || s.Contains(needle);

        if (Has("payslip") || Has("salary slip") || s.Contains("gross salary"))            return "PayslipLast3Months";
        if (Has("bank_statement") || Has("account statement") || s.Contains("opening balance")) return "BankStatementLast3Months";
        if (Has("employment_letter") || s.Contains("to whom it may concern"))              return "EmploymentLetter";
        if (Has("appraisal") || s.Contains("market value") || s.Contains("forced-sale"))   return "PropertyAppraisal";
        if (Has("tax_return") || s.Contains("annual return") || s.Contains("form 1301"))   return "TaxReturn";
        if (Has("government_id") || Has("teudat zehut") || s.Contains("identity card"))    return "GovernmentID";
        if (Has("pension") || s.Contains("pension statement"))                              return "PensionStatement";
        if (Has("land_registry") || s.Contains("gush") && s.Contains("helka"))             return "LandRegistryExtract";
        if (Has("subsidy") || s.Contains("first-time buyer"))                              return "GovernmentSubsidyForm";
        return "Unknown";
    }

    private sealed record DocItem(string DocumentId, string Filename, string? DeclaredType, string? Summary);

    private static List<DocItem> ParseDocuments(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : (doc.RootElement.TryGetProperty("docs", out var inner) ? inner : default);
        if (arr.ValueKind != JsonValueKind.Array) return new();

        var list = new List<DocItem>();
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            i++;
            list.Add(new DocItem(
                DocumentId:   el.TryGetProperty("documentId",   out var id)   ? (id.GetString()   ?? $"DOC-{i:D3}") : $"DOC-{i:D3}",
                Filename:     el.TryGetProperty("filename",     out var fn)   ? (fn.GetString()   ?? string.Empty)   : string.Empty,
                DeclaredType: el.TryGetProperty("declaredType", out var dt)   ? dt.GetString()    : null,
                Summary:      el.TryGetProperty("summary",      out var sm)   ? sm.GetString()    : null));
        }
        return list;
    }

    private static IReadOnlyList<JsonElement> ExtractArray(string json, string keyIfWrapped)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement arr = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : (doc.RootElement.TryGetProperty(keyIfWrapped, out var inner) ? inner : default);
        if (arr.ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();
        // Clone elements so they remain valid after this document is disposed.
        return arr.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOpts);
}
