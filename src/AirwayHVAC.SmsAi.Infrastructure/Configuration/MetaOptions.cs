// Repo path: src/AirwayHVAC.SmsAi.Infrastructure/Configuration/MetaOptions.cs
// NEW FILE — Meta Model API provider options. Mirrors AzureOpenAiOptions shape.

namespace AirwayHVAC.SmsAi.Infrastructure.Configuration;

public sealed class MetaOptions
{
    public const string SectionName = "Meta";

    public string BaseUrl { get; init; } = "https://api.meta.ai/v1";

    public string ApiKey { get; init; } = string.Empty;

    public string Model { get; init; } = "muse-spark-1.3";

    public string ReasoningEffort { get; init; } = "minimal";

    public int MaxOutputTokens { get; init; } = 1024;

    public string SystemPrompt { get; init; } = "You are an expert HVAC customer support assistant. Be concise, helpful, and ask clarifying questions when needed.";
}
