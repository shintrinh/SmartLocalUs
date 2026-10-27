// Proves the Day 1-2 wiring: ConversationOrchestrator routes the reply text
// through IAiResponder when one is injected, on BOTH channels, while the flow
// engine still drives stage/lead/events. A fake IAiResponder stands in for Azure
// OpenAI and for Meta alike — the wiring is provider-agnostic by construction
// (the provider switch lives in DI, not in this class).
// Conventions mirror HandleInboundSmsUseCaseTests: xUnit, hand-written fakes,
// real ConversationFlowEngine, NullLogger. No Twilio, no Cosmos, no network.

using AirwayHVAC.SmsAi.Application.Services;
using AirwayHVAC.SmsAi.Domain.Entities;
using AirwayHVAC.SmsAi.Domain.Enums;
using AirwayHVAC.SmsAi.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AirwayHVAC.SmsAi.Application.Tests.Services;

public sealed class ConversationOrchestratorAiReplyTests
{
    private static readonly Tenant Tenant = new(
        "tenant_123",
        "tenant_123",
        "CoolAir HVAC",
        "+18777804236",
        "Houston TX",
        "America/Chicago",
        new BusinessHours("08:00", "18:00"),
        DateTimeOffset.UtcNow);

    [Fact]
    public async Task Voice_AiReply_ReplacesStaticVoiceReply()
    {
        var fakeAi = new FakeAiResponder { Reply = "AI voice answer" };
        var orchestrator = CreateOrchestrator(fakeAi);

        var result = await orchestrator.ProcessCustomerMessageAsync(
            Tenant, "+15550001111", "Hello", ConversationChannel.Voice,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("AI voice answer", result.ReplyMessage);
        Assert.Equal(ConversationStage.Issue, result.Stage);
        Assert.Equal(ConversationChannel.Voice, result.Channel);
    }

    [Fact]
    public async Task Sms_AiReply_ReplacesStaticSmsReply()
    {
        var fakeAi = new FakeAiResponder { Reply = "AI sms answer" };
        var orchestrator = CreateOrchestrator(fakeAi);

        var result = await orchestrator.ProcessCustomerMessageAsync(
            Tenant, "+15550001111", "Hello", ConversationChannel.Sms,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("AI sms answer", result.ReplyMessage);
        Assert.Equal(ConversationStage.Issue, result.Stage);
        Assert.Equal(ConversationChannel.Sms, result.Channel);
    }

    [Fact]
    public async Task AiThrows_FallsBackToStaticReply()
    {
        var fakeAi = new FakeAiResponder { Throw = true };
        var orchestrator = CreateOrchestrator(fakeAi);

        var result = await orchestrator.ProcessCustomerMessageAsync(
            Tenant, "+15550001111", "Hello", ConversationChannel.Sms,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(
            "I can help with that. What issue are you having with your HVAC system?",
            result.ReplyMessage);
    }

    [Fact]
    public async Task AiReturnsWhitespace_FallsBackToStaticReply()
    {
        var fakeAi = new FakeAiResponder { Reply = "   " };
        var orchestrator = CreateOrchestrator(fakeAi);

        var result = await orchestrator.ProcessCustomerMessageAsync(
            Tenant, "+15550001111", "Hello", ConversationChannel.Sms,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(
            "I can help with that. What issue are you having with your HVAC system?",
            result.ReplyMessage);
    }

    [Fact]
    public async Task AiReceivesTenantPhoneHistoryAndMessage()
    {
        var fakeAi = new FakeAiResponder { Reply = "AI answer" };
        var orchestrator = CreateOrchestrator(fakeAi);

        await orchestrator.ProcessCustomerMessageAsync(
            Tenant, "+15550001111", "Hello", ConversationChannel.Voice,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, fakeAi.CallCount);
        Assert.Equal("tenant_123", fakeAi.LastTenant!.TenantId);
        Assert.Equal("+15550001111", fakeAi.LastPhone);
        Assert.Equal("Hello", fakeAi.LastMessage);
        Assert.Contains(
            fakeAi.LastHistory!,
            m => m.Role == "customer" && m.Text == "Hello");
    }

    [Fact]
    public async Task MultiTurn_StageStillAdvances_WithAiReplies()
    {
        var fakeAi = new FakeAiResponder { Reply = "AI answer" };
        var orchestrator = CreateOrchestrator(fakeAi);

        var turn1 = await orchestrator.ProcessCustomerMessageAsync(
            Tenant, "+15550001111", "AC not cooling", ConversationChannel.Sms,
            DateTimeOffset.UtcNow, CancellationToken.None);
        var turn2 = await orchestrator.ProcessCustomerMessageAsync(
            Tenant, "+15550001111", "123 Main St", ConversationChannel.Sms,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("AI answer", turn1.ReplyMessage);
        Assert.Equal(ConversationStage.Address, turn1.Stage);
        Assert.Equal("AI answer", turn2.ReplyMessage);
        Assert.Equal(ConversationStage.Urgency, turn2.Stage);
    }

    [Fact]
    public async Task AiReceivesStageContext_AfterIssueCaptured()
    {
        var fakeAi = new FakeAiResponder { Reply = "AI answer" };
        var orchestrator = CreateOrchestrator(fakeAi);

        await orchestrator.ProcessCustomerMessageAsync(
            Tenant, "+15550001111", "AC not cooling", ConversationChannel.Sms,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(fakeAi.LastContext);
        Assert.Equal(ConversationStage.Address, fakeAi.LastContext!.Stage);
        Assert.Equal("AC not cooling", fakeAi.LastContext.Issue);
        Assert.Contains("Address", fakeAi.LastContext.MissingFields);
        Assert.Contains("Urgency", fakeAi.LastContext.MissingFields);
        Assert.Contains("PreferredTime", fakeAi.LastContext.MissingFields);
        Assert.DoesNotContain("Issue", fakeAi.LastContext.MissingFields);
    }

    [Fact]
    public async Task AiReceivesConfirmContext_WhenAllFieldsPresent()
    {
        var fakeAi = new FakeAiResponder { Reply = "AI answer" };
        var orchestrator = CreateOrchestrator(fakeAi);

        await orchestrator.ProcessCustomerMessageAsync(
            Tenant, "+15550001111", "AC not cooling", ConversationChannel.Sms,
            DateTimeOffset.UtcNow, CancellationToken.None);
        await orchestrator.ProcessCustomerMessageAsync(
            Tenant, "+15550001111", "123 Main St", ConversationChannel.Sms,
            DateTimeOffset.UtcNow, CancellationToken.None);
        await orchestrator.ProcessCustomerMessageAsync(
            Tenant, "+15550001111", "emergency", ConversationChannel.Sms,
            DateTimeOffset.UtcNow, CancellationToken.None);
        var confirm = await orchestrator.ProcessCustomerMessageAsync(
            Tenant, "+15550001111", "tomorrow 9am", ConversationChannel.Sms,
            DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("AI answer", confirm.ReplyMessage);
        Assert.Equal(ConversationStage.Confirm, confirm.Stage);
        Assert.NotNull(fakeAi.LastContext);
        Assert.Equal(ConversationStage.Confirm, fakeAi.LastContext!.Stage);
        Assert.Empty(fakeAi.LastContext.MissingFields);
    }

    [Fact]
    public void AiReplyContext_BuildStageGuidance_MentionsStageAndMissing()
    {
        var context = new AiReplyContext(
            ConversationStage.Address, "AC not cooling", null, null, null);

        var guidance = context.BuildStageGuidance();

        Assert.Contains("Address", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("service address", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AC not cooling", guidance, StringComparison.Ordinal);
    }

    private static ConversationOrchestrator CreateOrchestrator(IAiResponder aiResponder)
    {
        var conversationService = new ConversationService(
            new FakeConversationRepository(), NullLogger<ConversationService>.Instance);
        var leadService = new LeadService(new FakeLeadRepository());
        var eventService = new EventService(new FakeEventRepository());
        return new ConversationOrchestrator(
            conversationService,
            leadService,
            eventService,
            new ConversationFlowEngine(),
            NullLogger<ConversationOrchestrator>.Instance,
            aiResponder);
    }

    private sealed class FakeAiResponder : IAiResponder
    {
        public string? Reply { get; set; }

        public bool Throw { get; set; }

        public int CallCount { get; private set; }

        public Tenant? LastTenant { get; private set; }

        public string? LastPhone { get; private set; }

        public IReadOnlyCollection<Message>? LastHistory { get; private set; }

        public string? LastMessage { get; private set; }

        public AiReplyContext? LastContext { get; private set; }

        public Task<string> GenerateHvacResponseAsync(
            Tenant tenant,
            string customerPhone,
            IReadOnlyCollection<Message> conversationHistory,
            string inboundMessage,
            CancellationToken cancellationToken,
            AiReplyContext? context = null)
        {
            CallCount++;
            LastTenant = tenant;
            LastPhone = customerPhone;
            LastHistory = conversationHistory;
            LastMessage = inboundMessage;
            LastContext = context;
            if (Throw)
            {
                throw new InvalidOperationException("AI down");
            }

            return Task.FromResult(Reply ?? string.Empty);
        }
    }

    private sealed class FakeConversationRepository : IConversationRepository
    {
        private readonly Dictionary<string, Conversation> _store = new();

        public Conversation? LastSaved { get; private set; }

        public Task<Conversation?> GetByPhoneAsync(string tenantId, string phone, CancellationToken cancellationToken)
            => GetByPhoneAsync(tenantId, phone, ConversationChannel.Sms, cancellationToken);

        public Task<Conversation?> GetByPhoneAsync(string tenantId, string phone, ConversationChannel channel, CancellationToken cancellationToken)
        {
            _store.TryGetValue($"{tenantId}:{phone}:{channel}", out var conversation);
            return Task.FromResult(conversation);
        }

        public Task<Conversation?> GetByIdAsync(string tenantId, string conversationId, CancellationToken cancellationToken)
        {
            var conversation = _store.Values.FirstOrDefault(x => x.TenantId == tenantId && x.Id == conversationId);
            return Task.FromResult(conversation);
        }

        public Task<(IReadOnlyList<Conversation> Items, int TotalCount)> GetPagedAsync(
            string tenantId,
            int page,
            int pageSize,
            string? status,
            CancellationToken cancellationToken)
        {
            var query = _store.Values.Where(x => x.TenantId == tenantId);
            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(x => string.Equals(x.Status, status, StringComparison.OrdinalIgnoreCase));
            }

            var totalCount = query.Count();
            var items = query
                .OrderByDescending(x => x.LastMessageAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return Task.FromResult<(IReadOnlyList<Conversation> Items, int TotalCount)>((items, totalCount));
        }

        public Task<IReadOnlyList<Conversation>> GetByLastMessageAtRangeAsync(
            string tenantId,
            DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive,
            CancellationToken cancellationToken)
        {
            var items = _store.Values
                .Where(x => x.TenantId == tenantId && x.LastMessageAt >= fromInclusive && x.LastMessageAt < toExclusive)
                .ToList();
            return Task.FromResult<IReadOnlyList<Conversation>>(items);
        }

        public Task UpsertAsync(Conversation conversation, CancellationToken cancellationToken)
        {
            LastSaved = conversation;
            _store[$"{conversation.TenantId}:{conversation.Phone}:{conversation.Channel}"] = conversation;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLeadRepository : ILeadRepository
    {
        private Lead? _lead;

        public Task<Lead?> GetByConversationIdAsync(string tenantId, string conversationId, CancellationToken cancellationToken)
            => Task.FromResult(_lead);

        public Task<Lead?> GetByIdAsync(string tenantId, string leadId, CancellationToken cancellationToken)
            => Task.FromResult(_lead is not null && _lead.TenantId == tenantId && _lead.Id == leadId ? _lead : null);

        public Task<(IReadOnlyList<Lead> Items, int TotalCount)> GetPagedAsync(
            string tenantId,
            int page,
            int pageSize,
            string? status,
            CancellationToken cancellationToken)
        {
            var items = _lead is null
                ? []
                : new List<Lead> { _lead }.Where(x => x.TenantId == tenantId).ToList();

            return Task.FromResult<(IReadOnlyList<Lead> Items, int TotalCount)>((items, items.Count));
        }

        public Task<IReadOnlyList<Lead>> GetByCreatedAtRangeAsync(
            string tenantId,
            DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive,
            CancellationToken cancellationToken)
        {
            var items = _lead is null
                ? []
                : new List<Lead> { _lead }
                    .Where(x => x.TenantId == tenantId && x.CreatedAt >= fromInclusive && x.CreatedAt < toExclusive)
                    .ToList();

            return Task.FromResult<IReadOnlyList<Lead>>(items);
        }

        public Task UpsertAsync(Lead lead, CancellationToken cancellationToken)
        {
            _lead = lead;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEventRepository : IEventRepository
    {
        public List<EventLog> Events { get; } = [];

        public Task AddAsync(EventLog eventLog, CancellationToken cancellationToken)
        {
            Events.Add(eventLog);
            return Task.CompletedTask;
        }
    }
}
