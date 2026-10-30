// Repo path: src/AirwayHVAC.SmsAi.Infrastructure/Configuration/ModalOptions.cs
// NEW FILE — Modal tier (30/10). Modal (Muse Spark Open) options.
// Mirrors BasetenOptions/FireworksOptions shape, but with Modal defaults, so the
// fallback chain can bind a MetaResponder to Modal without touching the "Meta"
// section (which stays pointed at api.meta.ai / muse-spark-1.3).
// Bound from the "Modal" section; env mappings in Program.cs:
// MODAL_BASE_URL, MODAL_API_KEY, MODAL_MODEL.
// If MODAL_BASE_URL / MODAL_MODEL are unset, the defaults below apply,
// so only MODAL_API_KEY is strictly required to enable the Modal leg.
// MODAL_API_KEY is the Modal proxy token in "token-id.token-secret" form,
// set in the environment — never committed to the repo.

namespace AirwayHVAC.SmsAi.Infrastructure.Configuration;

public sealed class ModalOptions
{
    public const string SectionName = "Modal";

    public string BaseUrl { get; init; } = "https://phuctrinh--ep-muse-spark-open-816b-a42b-server.us-west.modal.direct/v1";

    public string ApiKey { get; init; } = string.Empty;

    public string Model { get; init; } = "meta-models/Muse-Spark-Open-816B-A42B";

    public string ReasoningEffort { get; init; } = "minimal";

    public int MaxOutputTokens { get; init; } = 1024;

    public string SystemPrompt { get; init; } = "You are an expert HVAC customer support assistant. Be concise, helpful, and ask clarifying questions when needed.";
}
