// Repo path: src/AirwayHVAC.SmsAi.Infrastructure/Configuration/BasetenOptions.cs
// NEW FILE — Day 5-6 auto-fallback (27/10). Baseten (Muse Spark Open) options.
// Mirrors MetaOptions shape, but with Baseten defaults, so the fallback chain
// can bind a MetaResponder to Baseten without touching the "Meta" section
// (which stays pointed at api.meta.ai / muse-spark-1.3).
// Bound from the "Baseten" section; env mappings in Program.cs:
// BASETEN_BASE_URL, BASETEN_API_KEY, BASETEN_MODEL.
// If BASETEN_BASE_URL / BASETEN_MODEL are unset, the defaults below apply,
// so only BASETEN_API_KEY is strictly required to enable the Baseten leg.

namespace AirwayHVAC.SmsAi.Infrastructure.Configuration;

public sealed class BasetenOptions
{
    public const string SectionName = "Baseten";

    public string BaseUrl { get; init; } = "https://inference.baseten.co/v1";

    public string ApiKey { get; init; } = string.Empty;

    public string Model { get; init; } = "meta-models/muse-spark-open-1026";

    public string ReasoningEffort { get; init; } = "minimal";

    public int MaxOutputTokens { get; init; } = 1024;

    public string SystemPrompt { get; init; } = "You are an expert HVAC customer support assistant. Be concise, helpful, and ask clarifying questions when needed.";
}
