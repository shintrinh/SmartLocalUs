// Repo path: src/AirwayHVAC.SmsAi.Application/Services/ConversationOrchestrator.cs
// PATCHED (wiring IAiResponder into live paths, Day 1-2; stage context, Day 3-4)
// — full file, replaces the original.
// Change vs original (4 hunks):
//   1. New optional ctor param IAiResponder? aiResponder = null.
//      Optional so existing manual constructions (incl. tests) keep compiling;
//      DI injects it because IAiResponder is registered (Meta or Azure by AI_PROVIDER).
//   2. New field _aiResponder.
//   3. In ProcessCustomerMessageAsync, BOTH channels: try IAiResponder for the
//      reply text; on empty reply or exception, fall back to the flow-engine reply.
//      The flow engine STILL runs first, so stage progression, lead handling and
//      events are unchanged.
//   4. Day 3-4 (hoàn thiện calling): after the flow engine runs, build an
//      AiReplyContext from the conversation's post-turn stage + captured fields
//      and pass it to IAiResponder, so the AI asks for the right next thing
//      instead of diverging from the stage machine. The context is built from
//      the conversation object only — no new data source, no history pollution
//      (the guidance lives in the provider request, not in stored messages).
// Sequence per Alen 27/10: Azure first (AI_PROVIDER unset -> OpenAiResponder,
// his original design restored), then Meta (AI_PROVIDER=Meta -> MetaResponder).
// SMS is Alen's MVP with no real customers running, so wiring both channels now
// is safe — no live-customer behavior change, no real customer data at stake.
// Why this file: IAiResponder had NO production caller (verified 27/10 via code
// search: GenerateHvacResponseAsync appears only in the interface + OpenAiResponder).
// Both SMS and voice replies came from ConversationFlowEngine static replies, so the
// Day-0 provider swap alone changes no live reply. This wiring is what actually
// connects the providers (Azure first, then Meta) to the product.

using AirwayHVAC.SmsAi.Domain.Enums;
using AirwayHVAC.SmsAi.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace AirwayHVAC.SmsAi.Application.Services;

public sealed class ConversationOrchestrator
{
    private readonly ConversationService _conversationService;
    private readonly LeadService _leadService;
    private readonly EventService _eventService;
    private readonly ConversationFlowEngine _flowEngine;
    private readonly ILogger<ConversationOrchestrator> _logger;
    private readonly IAiResponder? _aiResponder;

    public ConversationOrchestrator(
        ConversationService conversationService,
        LeadService leadService,
        EventService eventService,
        ConversationFlowEngine flowEngine,
        ILogger<ConversationOrchestrator> logger,
        IAiResponder? aiResponder = null)
    {
        _conversationService = conversationService;
        _leadService = leadService;
        _eventService = eventService;
        _flowEngine = flowEngine;
        _logger = logger;
        _aiResponder = aiResponder;
    }

    public async Task<ConversationOrchestrationResult> ProcessCustomerMessageAsync(
        AirwayHVAC.SmsAi.Domain.Entities.Tenant tenant,
        string customerPhoneNumber,
        string messageBody,
        ConversationChannel channel,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversationService.LoadOrCreateAsync(tenant.TenantId, customerPhoneNumber, channel, timestamp, cancellationToken);
        var previousStage = conversation.Stage;
        var channelName = channel.ToString().ToLowerInvariant();

        conversation.AppendCustomerMessage(messageBody, timestamp);
        await _eventService.TrackAsync(tenant.TenantId, $"{channelName}_received", customerPhoneNumber, conversation.Id, timestamp, cancellationToken);

        var flowResult = _flowEngine.ProcessCustomerMessage(conversation, messageBody, channel);
        var reply = flowResult.ReplyMessage;

        // WIRING (hackathon Day 1-2, updated 27/10 per Alen: BOTH channels):
        // the flow engine still drives stage, but the reply text comes from the
        // AI provider when one is wired in. The customer message was already
        // appended above, so conversation.Messages is the history IAiResponder
        // expects (customer -> user, ai -> assistant). Provider sequence:
        // AI_PROVIDER unset -> Azure OpenAiResponder (Alen's original design);
        // AI_PROVIDER=Meta -> MetaResponder. SMS is MVP with no real customers,
        // so wiring it now carries no live-customer risk.
        // Day 3-4: the flow engine already ran, so conversation.Stage and the
        // captured fields are post-turn — exactly what the AI needs to stay
        // on-stage (ask for the next missing field, read back at Confirm).
        if (_aiResponder is not null)
        {
            try
            {
                var context = new AiReplyContext(
                    conversation.Stage,
                    conversation.Issue,
                    conversation.Address,
                    conversation.Urgency,
                    conversation.PreferredTime);
                var aiReply = await _aiResponder.GenerateHvacResponseAsync(
                    tenant, customerPhoneNumber, conversation.Messages, messageBody, cancellationToken, context);
                if (!string.IsNullOrWhiteSpace(aiReply))
                {
                    reply = aiReply;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI reply failed for tenant {TenantId}, falling back to flow reply", tenant.TenantId);
            }
        }

        conversation.AppendAiMessage(reply, DateTimeOffset.UtcNow);

        var (lead, created) = await _leadService.CreateOrUpdateFromConversationAsync(conversation, cancellationToken);
        conversation.AttachLead(lead.Id);

        await _conversationService.SaveAsync(conversation, cancellationToken);

        if (created)
        {
            await _eventService.TrackAsync(tenant.TenantId, "lead_created", customerPhoneNumber, conversation.Id, timestamp, cancellationToken);
        }

        if (previousStage != conversation.Stage)
        {
            await _eventService.TrackAsync(tenant.TenantId, "stage_changed", customerPhoneNumber, conversation.Id, DateTimeOffset.UtcNow, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(lead.PreferredTime))
        {
            await _eventService.TrackAsync(tenant.TenantId, "appointment_requested", customerPhoneNumber, conversation.Id, DateTimeOffset.UtcNow, cancellationToken);
        }

        if (conversation.Stage == ConversationStage.Completed)
        {
            await _eventService.TrackAsync(tenant.TenantId, "booking_confirmed", customerPhoneNumber, conversation.Id, DateTimeOffset.UtcNow, cancellationToken);
        }

        _logger.LogInformation(
            "{Channel} message processed for tenant {TenantId}, conversation {ConversationId}, lead {LeadId}, stage {Stage}",
            channel,
            tenant.TenantId,
            conversation.Id,
            lead.Id,
            conversation.Stage);

        return new ConversationOrchestrationResult(conversation.Id, lead.Id, reply, conversation.Stage, conversation.Channel);
    }
}

public sealed record ConversationOrchestrationResult(
    string ConversationId,
    string LeadId,
    string ReplyMessage,
    ConversationStage Stage,
    ConversationChannel Channel);
