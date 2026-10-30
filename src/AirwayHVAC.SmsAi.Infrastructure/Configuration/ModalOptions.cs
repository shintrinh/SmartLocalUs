// Repo path: src/AirwayHVAC.SmsAi.Infrastructure/Configuration/ModalOptions.cs
// NEW FILE — Modal tier (30/10). Modal (Muse Spark Open) options.
// Mirrors BasetenOptions/FireworksOptions shape, but with Modal defaults, so the
// fallback chain can bind a MetaResponder to Modal without touching the "Meta"
// section (which stays pointed at api.meta.ai / muse-spark-1.3).
// Bound from the "Modal" section; env mappings in Program.cs:
// MODAL_BASE_URL, MODAL_PROXY_TOKEN_ID, MODAL_PROXY_TOKEN_SECRET, MODAL_MODEL.
// If MODAL_BASE_URL / MODAL_MODEL are unset, the defaults below apply.
// Modal auth needs BOTH proxy token values: the DI factory composes the
// request key as "token-id.token-secret" (Modal's own format). If either
// value is missing, the leg sends no key and fails per-call, so it is
// safely skipped by the fallback chain. Token values live in the
// environment only — never committed to the repo.

namespace AirwayHVAC.SmsAi.Infrastructure.Configuration;

public sealed class ModalOptions
{
    public const string SectionName = "Modal";

    public string BaseUrl { get; init; } = "https://phuctrinh--ep-muse-spark-open-816b-a42b-server.us-west.modal.direct/v1";

    public string TokenId { get; init; } = string.Empty;

    public string TokenSecret { get; init; } = string.Empty;

    public string Model { get; init; } = "meta-models/Muse-Spark-Open-816B-A42B";

    public string ReasoningEffort { get; init; } = "minimal";

    public int MaxOutputTokens { get; init; } = 1024;

    public string SystemPrompt { get; init; } = "You are an expert HVAC customer support assistant. Be concise, helpful, and ask clarifying questions when needed.";
}
