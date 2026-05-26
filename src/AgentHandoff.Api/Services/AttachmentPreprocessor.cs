using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentHandoff.Api.Models;
using AgentHandoff.Engine.Configuration;

namespace AgentHandoff.Api.Services;

/// <summary>
/// Folds chat attachments into a single textual prompt the agents can reason about.
/// Text-like files (text/*, application/json, csv, md, txt) are decoded UTF-8 inline.
/// PDFs and images are routed through the MCP <c>OcrDocument</c> tool when MCP is in
/// Remote mode; otherwise their bytes are surfaced to the agent verbatim as base64
/// (so the model can still call <c>ExtractTransferRequest</c> with them).
/// </summary>
public sealed class AttachmentPreprocessor
{
    private static readonly string[] TextLikePrefixes = { "text/" };
    private static readonly string[] TextLikeMimes =
    {
        "application/json", "application/xml", "application/csv",
        "application/x-yaml", "application/yaml",
    };
    private static readonly string[] TextLikeExtensions =
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly McpOptions _mcp;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AttachmentPreprocessor> _log;

    public AttachmentPreprocessor(McpOptions mcp, IHttpClientFactory http, ILogger<AttachmentPreprocessor> log)
    {
        _mcp = mcp;
        _http = http;
        _log = log;
    }

    public async Task<string> BuildAugmentedMessageAsync(
        string userMessage,
        IReadOnlyList<ChatAttachment>? attachments,
        CancellationToken ct)
    {
        if (attachments is null || attachments.Count == 0)
            return userMessage ?? string.Empty;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(userMessage))
            sb.Append(userMessage.Trim()).Append("\n\n");

        for (var i = 0; i < attachments.Count; i++)
        {
            var att = attachments[i];
            var name = string.IsNullOrWhiteSpace(att.Filename) ? $"file-{i + 1}" : att.Filename;
            var mime = att.ContentType ?? GuessMimeFromExtension(name);

            if (string.IsNullOrEmpty(att.Base64))
            {
                sb.AppendLine($"[Attachment '{name}' was empty and skipped]");
                continue;
            }

            byte[] bytes;
            try { bytes = Convert.FromBase64String(att.Base64); }
            catch (FormatException)
            {
                sb.AppendLine($"[Attachment '{name}' has invalid base64 and was skipped]");
                continue;
            }

            if (IsTextLike(mime, name))
            {
                var text = SafeDecodeUtf8(bytes);
                sb.AppendLine($"[Attached file '{name}' ({mime ?? "text/plain"}, {bytes.Length} bytes) — contents follow inline; treat as part of the user's message and do NOT re-fetch or re-OCR it]");
                sb.AppendLine("<<<ATTACHMENT_BEGIN>>>");
                sb.AppendLine(text);
                sb.AppendLine("<<<ATTACHMENT_END>>>");
                sb.AppendLine();
                continue;
            }

            // Binary (PDF / image) — try MCP OCR.
            var ocr = await TryOcrAsync(att.Base64, ct).ConfigureAwait(false);
            if (ocr is not null)
            {
                sb.AppendLine($"[Attached file '{name}' ({mime ?? "application/octet-stream"}, {bytes.Length} bytes) — already OCR'd; the extracted text follows inline. Use this text directly with ExtractTransferRequest(text=...) if needed; do NOT call OCR tools again and do NOT pass blobUri or base64 for this attachment]");
                sb.AppendLine("<<<ATTACHMENT_BEGIN>>>");
                sb.AppendLine(ocr);
                sb.AppendLine("<<<ATTACHMENT_END>>>");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"[Attached file '{name}' ({mime ?? "application/octet-stream"}, {bytes.Length} bytes) — OCR unavailable. If needed, call ExtractTransferRequest with base64 below]");
                sb.AppendLine("<<<ATTACHMENT_BASE64_BEGIN>>>");
                sb.AppendLine(att.Base64);
                sb.AppendLine("<<<ATTACHMENT_BASE64_END>>>");
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string?> TryOcrAsync(string base64, CancellationToken ct)
    {
        if (!_mcp.IsRemoteMode || string.IsNullOrWhiteSpace(_mcp.ServerPath))
            return null;

        try
        {
            using var client = _http.CreateClient();
            client.BaseAddress = new Uri(_mcp.ServerPath.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(120);

            using var resp = await client
                .PostAsJsonAsync("mcp/execute", new
                {
                    toolName = "OcrDocument",
                    arguments = new { base64 },
                }, Json, ct)
                .ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("OcrDocument call failed: {Status} {Body}", (int)resp.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("result", out var resultEl))
                return null;

            var resultStr = resultEl.ValueKind == JsonValueKind.String ? resultEl.GetString() : resultEl.GetRawText();
            if (string.IsNullOrWhiteSpace(resultStr))
                return null;

            using var inner = JsonDocument.Parse(resultStr);
            if (inner.RootElement.TryGetProperty("error", out var errEl))
            {
                _log.LogWarning("OcrDocument returned error: {Error}", errEl.GetString());
                return null;
            }
            if (inner.RootElement.TryGetProperty("text", out var textEl))
                return textEl.GetString();

            return resultStr;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OcrDocument call threw.");
            return null;
        }
    }

    private static bool IsTextLike(string? mime, string filename)
    {
        if (!string.IsNullOrWhiteSpace(mime))
        {
            var lower = mime.ToLowerInvariant();
            if (TextLikePrefixes.Any(p => lower.StartsWith(p))) return true;
            if (TextLikeMimes.Contains(lower)) return true;
        }
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return TextLikeExtensions.Contains(ext);
    }

    private static string SafeDecodeUtf8(byte[] bytes)
    {
        try { return Encoding.UTF8.GetString(bytes); }
        catch { return $"[binary content, {bytes.Length} bytes]"; }
    }

    private static string? GuessMimeFromExtension(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".log" => "text/plain",
            ".md" or ".markdown" => "text/markdown",
            ".csv" => "text/csv",
            ".tsv" => "text/tab-separated-values",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".yaml" or ".yml" => "application/yaml",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tiff" or ".tif" => "image/tiff",
            _ => null,
        };
    }
}
