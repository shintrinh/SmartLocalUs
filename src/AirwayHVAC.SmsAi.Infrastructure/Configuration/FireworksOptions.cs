// Repo path: src/AirwayHVAC.SmsAi.Infrastructure/Configuration/FireworksOptions.cs
// NEW FILE — Fireworks tier (30/10). Fireworks (Muse Spark Open) options.
// Mirrors BasetenOptions shape, but with Fireworks defaults, so the fallback chain
// can bind a MetaResponder to Fireworks without touching the "Meta" section
// (which stays pointed at api.meta.ai / muse-spark-1.3).
// Bound from the "Fireworks" section; env mappings in Program.cs:
// FIREWORKS_BASE_URL, FIREWORKS_API_KEY, FIREWORKS_MODEL.
// If FIREWORKS_BASE_URL / FIREWORKS_MODEL are unset, the defaults below apply,
// so only FIREWORKS_API_KEY is strictly required to enable the Fireworks leg.

namespace AirwayHVAC.SmsAi.Infrastructure.Configuration;

public sealed class FireworksOptions
{
    public const string SectionName = "Fireworks";

    public string BaseUrl { get; init; } = "https://api.fireworks.ai/inference/v1";

    public string ApiKey { get; init; } = string.Empty;

    public string Model { get; init; } = "accounts/fireworks/models/muse-spark-open";

    public string ReasoningEffort { get; init; } = "minimal";

    public int MaxOutputTokens { get; init; } = 1024;

    public string SystemPrompt { get; init; } = "You are an expert HVAC customer support assistant. Be concise, helpful, and ask clarifying questions when needed.";
}
