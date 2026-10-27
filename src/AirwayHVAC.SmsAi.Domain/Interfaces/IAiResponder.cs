// Repo path: src/AirwayHVAC.SmsAi.Domain/Interfaces/IAiResponder.cs
// CHANGED (Day 3-4): optional AiReplyContext parameter. Optional so every
// existing caller keeps compiling; the orchestrator passes the context, and
// providers that receive it add a stage-guidance system message. Fake
// responders in tests implement the same signature.

using AirwayHVAC.SmsAi.Domain.Entities;

namespace AirwayHVAC.SmsAi.Domain.Interfaces;

public interface IAiResponder
{
    Task<string> GenerateHvacResponseAsync(
        Tenant tenant,
        string customerPhone,
        IReadOnlyCollection<Message> conversationHistory,
        string inboundMessage,
        CancellationToken cancellationToken,
        AiReplyContext? context = null);
}
