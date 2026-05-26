using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.AI.DocumentIntelligence;
using ModelContextProtocol.Server;
using OpenAI.Chat;

namespace AgentHandoff.McpServer.Tools;

/// <summary>
/// Money-transfer extraction & resolution tools.
/// <para>
/// <see cref="ExtractTransferRequest"/> uses Azure AI Document Intelligence
/// (prebuilt-read) for OCR when a blobUri is supplied, then Azure OpenAI in JSON-mode
/// to project the OCR'd / inline text into a strict per-field-confidence JSON shape.
/// If neither service is configured, falls back to a small regex extractor so the
/// demo remains runnable.
/// </para>
/// </summary>
[McpServerToolType]
public static class MoneyTransferTools
{
    /// <summary>Set by Program.cs at startup when DocumentIntelligence is configured.</summary>
    public static DocumentIntelligenceClient? DocIntelClient { get; set; }

    /// <summary>Set by Program.cs at startup when AzureOpenAI is configured.</summary>
    public static ChatClient? OpenAIChat { get; set; }

    private const string DefaultBank = "Discount";

    private static readonly string[] KnownBanks =
    {
        "Discount", "Hapoalim", "Leumi", "Mizrahi", "FIBI", "Mercantile", "Igud", "Massad",
    };

    private static readonly Dictionary<string, string> BankAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["discount"] = "Discount", ["bank discount"] = "Discount", ["דיסקונט"] = "Discount",
        ["hapoalim"] = "Hapoalim", ["bank hapoalim"] = "Hapoalim", ["הפועלים"] = "Hapoalim",
        ["leumi"]    = "Leumi",    ["bank leumi"] = "Leumi",    ["לאומי"] = "Leumi",
        ["mizrahi"]  = "Mizrahi",  ["mizrahi tefahot"] = "Mizrahi", ["מזרחי"] = "Mizrahi", ["מזרחי טפחות"] = "Mizrahi",
        ["fibi"]     = "FIBI",     ["first international"] = "FIBI", ["הבינלאומי"] = "FIBI",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string ExtractorSystemPrompt = """
        You are a strict information-extraction component for an Israeli bank.
        From the customer's money-transfer request (English or Hebrew), produce a JSON object
        EXACTLY matching this schema (no other keys, no prose, no markdown):

        {
          "fromAccount": { "value": "<string|null>", "confidence": <0..1> },
          "toAccount":   { "value": "<string|null>", "confidence": <0..1> },
          "toBank":      { "value": "<string>",      "confidence": <0..1>, "source": "<explicit|default|fuzzy>" },
          "amount":      { "value": <number|null>,   "currency": "<ILS|USD|EUR>", "confidence": <0..1> },
          "memo":        { "value": "<string|null>", "confidence": <0..1> },
          "language":    "<en|he>",
          "warnings":    [ "<short warning strings>" ]
        }

        Rules:
        - Preferred account format is "bb-bbb-aaaaaaa" (Israeli bank-branch-account, e.g. 12-345-6789012). When the customer writes the account in that exact form, return it verbatim with confidence 0.95+.
        - When the customer provides a plain numeric account identifier (5-13 digits, with or without separators) and no canonical "bb-bbb-aaaaaaa" form is present, return the digits as-is (strip spaces/dashes) with confidence 0.6-0.75 and add a warning "<fieldName> format unclear — normalize to bb-bbb-aaaaaaa". Do NOT set value=null in this case.
        - Only set fromAccount.value=null or toAccount.value=null when the request contains NO digits at all that could plausibly be that account. Use confidence<=0.3 in that case.
        - If the destination bank is NOT explicitly named, set toBank.value="Discount" and toBank.source="default" with confidence around 1.0.
        - Recognise Hebrew bank names: דיסקונט→Discount, לאומי→Leumi, הפועלים→Hapoalim, מזרחי / מזרחי טפחות→Mizrahi, הבינלאומי→FIBI.
        - Recognise currency tokens: ILS / NIS / שח / ש"ח / shekel(s) → "ILS"; dollar(s)/USD → "USD"; euro/EUR → "EUR". Default to "ILS" only when amount is present but currency missing, AND add a warning "currency inferred".
        - Add warnings for missing or low-confidence fields.
        - language="he" if any Hebrew letters appear in the source text, else "en".
        - Return ONLY the JSON object.
        """;

    [McpServerTool, Description(
        "Extract a money-transfer request from free-text (English or Hebrew) and return a strict JSON object " +
        "with per-field confidence scores. Fields: fromAccount, toAccount, toBank, amount{value,currency}, " +
        "memo, language, warnings. If 'toBank' is not explicitly named in the input, default it to 'Discount' " +
        "with source='default'. For PDFs/images, supply EITHER 'blobUri' (absolute https URL to a readable blob, " +
        "with SAS if private) OR 'base64' (the raw file bytes, base64-encoded inline). OCR is performed via " +
        "Azure AI Document Intelligence (prebuilt-read) before extraction.")]
    public static async Task<string> ExtractTransferRequest(
        [Description("The customer's free-text transfer request. Required when blobUri and base64 are both empty.")] string? text = null,
        [Description("Optional absolute https URL to a PDF/image of the transfer request (public or SAS).")] string? blobUri = null,
        [Description("Optional base64-encoded PDF/image bytes. Use when you don't have blob storage handy.")] string? base64 = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(blobUri) && string.IsNullOrWhiteSpace(base64))
            return Json(new { error = "Provide one of 'text', 'blobUri', or 'base64'." });

        string corpus = text ?? string.Empty;

        // ── Step 1a: OCR via Document Intelligence (blobUri path) ───────────
        if (!string.IsNullOrWhiteSpace(blobUri))
        {
            if (DocIntelClient is null)
                return Json(new { error = "blobUri provided but Document Intelligence is not configured on the MCP server." });

            if (!Uri.TryCreate(blobUri, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Json(new
                {
                    error = "blobUri must be an absolute https URL to a readable blob (with SAS if private). " +
                            "Local file paths are not supported by Document Intelligence — upload the file to Azure Blob Storage first, " +
                            "or use the 'base64' argument to send the file inline.",
                    received = blobUri,
                });
            }

            try
            {
                var op = await DocIntelClient.AnalyzeDocumentAsync(
                    Azure.WaitUntil.Completed,
                    "prebuilt-read",
                    uriSource: uri,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var ocrText = op.Value?.Content ?? string.Empty;
                corpus = string.IsNullOrWhiteSpace(corpus) ? ocrText : (corpus + "\n\n" + ocrText);
            }
            catch (Exception ex)
            {
                return Json(new { error = $"Document Intelligence call failed: {ex.Message}" });
            }
        }

        // ── Step 1b: OCR via Document Intelligence (inline base64 path) ─────
        if (!string.IsNullOrWhiteSpace(base64))
        {
            if (DocIntelClient is null)
                return Json(new { error = "base64 provided but Document Intelligence is not configured on the MCP server." });

            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch (FormatException) { return Json(new { error = "base64 argument is not valid base64." }); }

            try
            {
                var op = await DocIntelClient.AnalyzeDocumentAsync(
                    Azure.WaitUntil.Completed,
                    "prebuilt-read",
                    bytesSource: BinaryData.FromBytes(bytes),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var ocrText = op.Value?.Content ?? string.Empty;
                corpus = string.IsNullOrWhiteSpace(corpus) ? ocrText : (corpus + "\n\n" + ocrText);
            }
            catch (Exception ex)
            {
                return Json(new { error = $"Document Intelligence (inline) call failed: {ex.Message}" });
            }
        }

        if (string.IsNullOrWhiteSpace(corpus))
            return Json(new { error = "OCR returned no text and no inline text was provided." });

        // ── Step 2: Structured extraction via Azure OpenAI (JSON mode) ──────
        if (OpenAIChat is not null)
        {
            try
            {
                var resp = await OpenAIChat.CompleteChatAsync(
                    new ChatMessage[]
                    {
                        new SystemChatMessage(ExtractorSystemPrompt),
                        new UserChatMessage(corpus),
                    },
                    new ChatCompletionOptions
                    {
                        ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
                        Temperature    = 0f,
                    },
                    cancellationToken).ConfigureAwait(false);

                var raw = resp.Value.Content.Count > 0 ? resp.Value.Content[0].Text : null;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    using var _ = JsonDocument.Parse(raw); // validate
                    return raw;
                }
            }
            catch (Exception ex)
            {
                return RegexFallback(corpus, fallbackReason: $"OpenAI extraction failed: {ex.Message}");
            }
        }

        return RegexFallback(corpus, fallbackReason: OpenAIChat is null
            ? "AzureOpenAI not configured on MCP server; using regex fallback."
            : null);
    }

    [McpServerTool, Description(
        "Generic OCR helper: run Azure AI Document Intelligence (prebuilt-read) on an inline base64-encoded " +
        "PDF or image and return the raw extracted text plus per-page line counts. Use this when a customer " +
        "attaches a document to the conversation and the orchestrator wants the file's text content as plain " +
        "text (not as a transfer-extraction JSON).")]
    public static async Task<string> OcrDocument(
        [Description("Base64-encoded PDF or image bytes.")] string base64,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return Json(new { error = "base64 is required." });

        if (DocIntelClient is null)
            return Json(new { error = "Document Intelligence is not configured on the MCP server." });

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return Json(new { error = "base64 argument is not valid base64." }); }

        try
        {
            var op = await DocIntelClient.AnalyzeDocumentAsync(
                Azure.WaitUntil.Completed,
                "prebuilt-read",
                bytesSource: BinaryData.FromBytes(bytes),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var result   = op.Value;
            var text     = result?.Content ?? string.Empty;
            var pageCnt  = result?.Pages?.Count ?? 0;
            var language = DetectLanguage(text);

            return Json(new
            {
                text,
                pages    = pageCnt,
                language,
                bytes    = bytes.Length,
            });
        }
        catch (Exception ex)
        {
            return Json(new { error = $"Document Intelligence (OCR) call failed: {ex.Message}" });
        }
    }

    [McpServerTool, Description(
        "Resolve a bank name (or alias, in English or Hebrew) to its canonical bank id. " +
        "If 'query' is null/empty/unknown, returns the default bank ('Discount') with source='default'.")]
    public static string ResolveBank(
        [Description("Bank name, alias, code, or empty for default.")] string? query = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Json(new { value = DefaultBank, confidence = 1.0, source = "default" });

        var trimmed = query.Trim();
        if (BankAliases.TryGetValue(trimmed, out var canonical))
            return Json(new { value = canonical, confidence = 0.98, source = "alias" });

        var fuzzy = KnownBanks.FirstOrDefault(b => trimmed.Contains(b, StringComparison.OrdinalIgnoreCase));
        if (fuzzy is not null)
            return Json(new { value = fuzzy, confidence = 0.85, source = "fuzzy" });

        return Json(new { value = DefaultBank, confidence = 0.5, source = "default-fallback",
                          note = $"Unknown bank '{trimmed}', defaulted to {DefaultBank}." });
    }

    [McpServerTool, Description(
        "Validate an Israeli bank account number for the given bank. Checks the format " +
        "'bb-bbb-aaaaaaa' (bank-branch-account). Returns isValid + reason.")]
    public static string ValidateAccount(
        [Description("Account in 'bb-bbb-aaaaaaa' format, e.g. 12-345-6789012.")] string account,
        [Description("Canonical bank id (use ResolveBank).")] string bank)
    {
        if (string.IsNullOrWhiteSpace(account))
            return Json(new { isValid = false, reason = "Account is empty." });

        var match = Regex.Match(account, @"^\s*(\d{2})-(\d{3})-(\d{6,8})\s*$");
        if (!match.Success)
            return Json(new { isValid = false, reason = "Account does not match format 'bb-bbb-aaaaaaa'." });

        return Json(new
        {
            isValid     = true,
            bank        = string.IsNullOrWhiteSpace(bank) ? DefaultBank : bank,
            bankCode    = match.Groups[1].Value,
            branchCode  = match.Groups[2].Value,
            accountNo   = match.Groups[3].Value,
        });
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static string Json(object o) => JsonSerializer.Serialize(o, JsonOpts);

    private static string RegexFallback(string text, string? fallbackReason)
    {
        var lang     = DetectLanguage(text);
        var warnings = new List<string>();
        if (!string.IsNullOrEmpty(fallbackReason))
            warnings.Add(fallbackReason);

        var (amountValue, amountConf, currency) = ExtractAmount(text, warnings);
        var (fromAcc, fromConf) = ExtractAccount(text, side: "from");
        var (toAcc, toConf)     = ExtractAccount(text, side: "to");
        var (bank, bankConf, bankSource) = ExtractBank(text);
        var (memo, memoConf) = ExtractMemo(text);

        return Json(new
        {
            fromAccount = new { value = fromAcc, confidence = fromConf },
            toAccount   = new { value = toAcc,   confidence = toConf   },
            toBank      = new { value = bank,    confidence = bankConf, source = bankSource },
            amount      = new { value = amountValue, currency = currency, confidence = amountConf },
            memo        = new { value = memo,    confidence = memoConf  },
            language    = lang,
            warnings    = warnings,
        });
    }

    private static string DetectLanguage(string text) =>
        text.Any(c => c >= 0x0590 && c <= 0x05FF) ? "he" : "en";

    private static (decimal? value, double conf, string currency) ExtractAmount(string text, List<string> warnings)
    {
        var m = Regex.Match(text,
            @"(?<num>\d{1,3}(?:[,]\d{3})*(?:\.\d+)?|\d+)\s*(?<cur>ILS|NIS|USD|EUR|ש""ח|ש'ח|שח|shekels?|dollars?|euro)",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var raw = m.Groups["num"].Value.Replace(",", "");
            if (decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                                 System.Globalization.CultureInfo.InvariantCulture, out var v))
                return (v, 0.97, NormalizeCurrency(m.Groups["cur"].Value));
        }

        var bare = Regex.Match(text, @"\b\d{2,7}(?:\.\d+)?\b");
        if (bare.Success && decimal.TryParse(bare.Value, System.Globalization.NumberStyles.Number,
                                             System.Globalization.CultureInfo.InvariantCulture, out var v2))
        {
            warnings.Add("currency inferred (no explicit currency token)");
            return (v2, 0.6, "ILS");
        }

        var lower = text.ToLowerInvariant();
        if (lower.Contains("fifteen hundred"))           { warnings.Add("amount parsed from words; verify"); return (1500m, 0.45, "ILS"); }
        if (lower.Contains("two thousand five hundred")) { warnings.Add("amount parsed from words; verify"); return (2500m, 0.45, "ILS"); }

        warnings.Add("amount not found");
        return (null, 0.0, "ILS");
    }

    private static string NormalizeCurrency(string raw)
    {
        var t = raw.Trim().ToLowerInvariant();
        if (t is "ils" or "nis" or "shekel" or "shekels" or "ש\"ח" or "ש'ח" or "שח") return "ILS";
        if (t is "usd" or "dollar" or "dollars") return "USD";
        if (t is "eur" or "euro") return "EUR";
        return raw.ToUpperInvariant();
    }

    private static (string? value, double conf) ExtractAccount(string text, string side)
    {
        var matches = Regex.Matches(text, @"\b\d{2}-\d{3}-\d{6,8}\b").Cast<Match>().ToList();
        if (matches.Count == 0) return (null, 0.0);
        if (matches.Count == 1) return (matches[0].Value, 0.55);

        var keyword = side == "from"
            ? new[] { "from", "מחשבון" }
            : new[] { "to", "לחשבון" };

        foreach (var m in matches)
        {
            var window = text[Math.Max(0, m.Index - 30)..m.Index];
            if (keyword.Any(k => window.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return (m.Value, 0.92);
        }

        var idx = side == "from" ? 0 : 1;
        return idx < matches.Count ? (matches[idx].Value, 0.7) : (null, 0.0);
    }

    private static (string value, double conf, string source) ExtractBank(string text)
    {
        foreach (var (alias, canonical) in BankAliases)
            if (text.Contains(alias, StringComparison.OrdinalIgnoreCase))
                return (canonical, 0.95, "explicit");
        return (DefaultBank, 1.0, "default");
    }

    private static (string? value, double conf) ExtractMemo(string text)
    {
        var m = Regex.Match(text,
            @"(?:memo|ref(?:erence)?|for|מטרת ההעברה|הערה)\s*[:\-]?\s*(?<v>[^\n.]+)",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var v = m.Groups["v"].Value.Trim().TrimEnd('.', ',', ';');
            return (v, 0.8);
        }
        return (null, 0.0);
    }
}
