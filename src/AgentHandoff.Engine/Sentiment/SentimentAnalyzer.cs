namespace AgentHandoff.Engine.Sentiment;

/// <summary>
/// Result of scoring a user message for frustration / urgency.
/// Scores are 0-10. <see cref="ShouldEscalate"/> is true when the orchestrator should
/// short-circuit the normal handoff workflow and route the customer to a human supervisor.
/// </summary>
public sealed record SentimentVerdict(
    int  Frustration,
    int  Urgency,
    bool ShouldEscalate,
    string Reason);

/// <summary>
/// Lightweight, deterministic heuristic classifier — keyword-based with caps/exclamation
/// signals. Cheap (no extra LLM call), explainable (all signals surface in <see cref="SentimentVerdict.Reason"/>),
/// and good enough for the demo. For production, swap in Azure AI Language Sentiment Analysis
/// or a fine-tuned classifier.
/// </summary>
public sealed class SentimentAnalyzer
{
    /// <summary>Combined-score threshold above which the workflow escalates to a human.</summary>
    public int EscalationThreshold { get; init; } = 8;

    /// <summary>Frustration-only threshold (independent escalation trigger).</summary>
    public int FrustrationThreshold { get; init; } = 6;

    private static readonly string[] FrustrationWords =
    {
        "useless", "ridiculous", "unacceptable", "terrible", "horrible", "furious",
        "angry", "fed up", "stop wasting", "incompetent", "garbage", "rubbish",
        "complete waste", "absurd", "outrageous", "insulting", "pathetic", "appalling",
        // Hebrew equivalents
        "חרא", "מעצבן", "לא סביר", "נורא", "כועס", "כועסת", "נמאס לי",
        "בזבוז זמן", "חוסר מקצועיות",
    };

    private static readonly string[] UrgencyWords =
    {
        "urgent", "asap", "immediately", "right now", "right away", "emergency",
        "as soon as possible", "deadline", "critical", "time-sensitive", "can't wait",
        // Hebrew
        "דחוף", "מיד", "כעת", "עכשיו", "חירום", "בהקדם", "בדחיפות",
    };

    /// <summary>
    /// Explicit human-handoff requests — these escalate immediately regardless of
    /// the numeric scores.
    /// </summary>
    private static readonly string[] EscalationPhrases =
    {
        "speak to a human", "speak to a manager", "speak to your manager",
        "talk to a human", "talk to a manager",
        "human agent", "real person", "supervisor", "let me speak to",
        "i want to complain", "file a complaint", "lawsuit", "ombudsman", "regulator",
        "speak to your supervisor",
        // Hebrew
        "תן לי לדבר עם", "אני רוצה לדבר עם בנקאי", "אני רוצה אדם אמיתי",
        "תעבירו אותי למישהו אנושי", "אני רוצה להגיש תלונה",
    };

    public SentimentVerdict Analyze(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new SentimentVerdict(0, 0, false, "empty input");

        var lower = text.ToLowerInvariant();

        // 1. Explicit escalation request → instant trigger.
        foreach (var phrase in EscalationPhrases)
        {
            if (lower.Contains(phrase))
                return new SentimentVerdict(
                    Frustration:    8,
                    Urgency:        5,
                    ShouldEscalate: true,
                    Reason:         $"explicit escalation: '{phrase}'");
        }

        // 2. Numeric scoring.
        var letters = text.Count(char.IsLetter);
        var upper   = text.Count(c => char.IsLetter(c) && char.IsUpper(c));
        var capRatio = letters > 0 ? (double)upper / letters : 0;

        var exclamations = text.Count(c => c == '!');
        var frustHits  = FrustrationWords.Count(w => lower.Contains(w));
        var urgentHits = UrgencyWords.Count(w => lower.Contains(w));

        var frustration = Math.Min(10,
            frustHits * 3
          + Math.Min(exclamations, 3)
          + (capRatio > 0.5 && letters >= 10 ? 4 : 0));

        var urgency = Math.Min(10, urgentHits * 3);

        var combined = frustration + (urgency / 2);
        var shouldEscalate = frustration >= FrustrationThreshold || combined >= EscalationThreshold;

        var reason = $"frustration={frustration} (words={frustHits}, !={exclamations}, caps={capRatio:F2}); " +
                     $"urgency={urgency} (words={urgentHits}); combined={combined}; " +
                     $"thresholds f≥{FrustrationThreshold} OR combined≥{EscalationThreshold}";

        return new SentimentVerdict(frustration, urgency, shouldEscalate, reason);
    }
}
