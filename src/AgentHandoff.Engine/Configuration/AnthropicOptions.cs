namespace AgentHandoff.Engine.Configuration;

/// <summary>
/// Process-wide credentials and defaults for the 'anthropic' runtime transport.
/// Bind from the "Anthropic" configuration section.
/// </summary>
public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    public string? ApiKey { get; set; }

    /// <summary>Default model used when an agent does not override <c>Runtime.AnthropicModel</c>.</summary>
    public string Model { get; set; } = "claude-haiku-4-5";

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}
