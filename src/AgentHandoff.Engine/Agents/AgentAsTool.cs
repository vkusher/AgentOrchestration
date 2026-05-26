using System.ComponentModel;
using AgentHandoff.Engine.Orchestration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentHandoff.Engine.Agents;

/// <summary>
/// Exposes an existing <see cref="AIAgent"/> as a callable <see cref="AIFunction"/>
/// so another agent can invoke it as a tool. Each call is a fresh, isolated agent
/// run (no shared thread) — i.e. a small distributed unit of work per invocation.
/// </summary>
internal static class AgentAsTool
{
    public static AIFunction CreateDocClassifierTool(AIAgent classifier, Action<AgentEvent>? onEvent)
    {
        [Description(
            "Classify a single submitted mortgage document via the dedicated doc_classifier agent. " +
            "For text/already-OCR'd documents, pass the extracted text in `documentText` and leave " +
            "`base64` empty. For raw PDFs/images that have NOT been OCR'd yet, leave `documentText` " +
            "empty and pass the file bytes in `base64`; the classifier will OCR it via its own tool. " +
            "Returns the JSON object {documentId, filename, detectedType, match, confidence}.")]
        async Task<string> ClassifyDocumentAgent(
            [Description("Stable per-document id assigned by IngestMortgageBundle (e.g. DOC-001).")] string documentId,
            [Description("File name from the [Attached file '...'] header line.")] string filename,
            [Description("Extracted text body of the document (may be the full text, up to a few thousand chars). Empty string if only base64 is supplied.")] string documentText,
            [Description("OPTIONAL. Base64 of the raw PDF/image bytes when the document has NOT been OCR'd upstream. Empty string when documentText is supplied.")] string base64,
            CancellationToken ct)
        {
            var evt = new ToolCallEvent("doc_classifier", "agent.invoke", "request", "AgentAsTool", DateTimeOffset.UtcNow);
            onEvent?.Invoke(evt);
            TurnEventBus.Publish(evt);

            var prompt =
                $"Classify this single document and return ONLY the resulting JSON object " +
                $"(no prose, no fences):\n" +
                $"documentId: {documentId}\n" +
                $"filename: {filename}\n" +
                $"documentText: {documentText}\n" +
                $"base64: {(string.IsNullOrWhiteSpace(base64) ? string.Empty : base64)}";

            var response = await classifier.RunAsync(prompt, cancellationToken: ct).ConfigureAwait(false);
            var text = response.Text ?? string.Empty;

            var done = new ToolCallEvent("doc_classifier", "agent.invoke", "response", "AgentAsTool", DateTimeOffset.UtcNow);
            onEvent?.Invoke(done);
            TurnEventBus.Publish(done);

            return text;
        }

        return AIFunctionFactory.Create(ClassifyDocumentAgent, name: "ClassifyDocumentAgent");
    }
}
