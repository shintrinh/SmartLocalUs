// Repo path: src/AirwayHVAC.SmsAi.Infrastructure/Services/FallbackResponder.cs
// NEW FILE — Day 5-6 auto-fallback (27/10).
// Tries each responder in order and returns the first non-empty reply.
// A responder that throws or returns empty/whitespace is skipped with a
// warning log; the next responder in the chain is tried.
// If every responder fails, returns empty, so the orchestrator falls back
// to its static flow reply — exactly the behavior a single failed provider
// has today. Nothing here invents a reply: an empty chain result means
// "no AI answered", and the static reply is the honest fallback.
// Production caller: ConversationOrchestrator, via IAiResponder, when
// AiProvider is "Fallback" (see ServiceCollectionExtensions). Chain order
// is fixed by the DI factory: Meta Model API first, Baseten (Muse Spark Open)
// second, Fireworks (Muse Spark Open) third, Modal (Muse Spark Open) fourth.
// Azure OpenAI is temporarily out of the chain (30/10); its code is kept,
// so restoring it is one line in the DI factory.
// NOTE: each member runs its own complete turn (including its own tool
// loop) before the chain moves on; the chain does not split a turn.

using AirwayHVAC.SmsAi.Domain.Entities;
using AirwayHVAC.SmsAi.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace AirwayHVAC.SmsAi.Infrastructure.Services;

public sealed class FallbackResponder : IAiResponder
{
    private readonly IReadOnlyList<IAiResponder> _chain;

    private readonly ILogger<FallbackResponder>? _logger;

    public FallbackResponder(IReadOnlyList<IAiResponder> chain, ILogger<FallbackResponder>? logger = null)
    {
        _chain = chain;
        _logger = logger;
    }

    public async Task<string> GenerateHvacResponseAsync(
        Tenant tenant,
        string customerPhone,
        IReadOnlyCollection<Message> conversationHistory,
        string inboundMessage,
        CancellationToken cancellationToken,
        AiReplyContext? context = null)
    {
        foreach (var responder in _chain)
        {
            try
            {
                var reply = await responder.GenerateHvacResponseAsync(
                    tenant, customerPhone, conversationHistory, inboundMessage, cancellationToken, context);
                if (!string.IsNullOrWhiteSpace(reply))
                {
                    return reply;
                }

                _logger?.LogWarning("Fallback: responder {Responder} returned an empty reply, trying next.", responder.GetType().Name);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Fallback: responder {Responder} failed, trying next.", responder.GetType().Name);
            }
        }

        _logger?.LogWarning("Fallback: all responders failed, returning empty so the orchestrator uses its static reply.");
        return string.Empty;
    }
}
