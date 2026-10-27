// Proves the wiring end to end at use-case level, still with no Twilio and no
// network: the voice Gather loop speaks the AI reply, and the SMS loop sends the
// AI reply through the outbound service. The fake IAiResponder stands in for
// Azure OpenAI and for Meta alike — the provider switch lives in DI.
// Conventions mirror the neighbouring test files: Moq for the voice test
// (like HandleVoiceGatherUseCaseTests), hand-written fakes for the SMS test
// (like HandleInboundSmsUseCaseTests).

using AirwayHVAC.SmsAi.Application.Services;
using AirwayHVAC.SmsAi.Application.UseCases;
using AirwayHVAC.SmsAi.Domain.Entities;
using AirwayHVAC.SmsAi.Domain.Enums;
using AirwayHVAC.SmsAi.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AirwayHVAC.SmsAi.Application.Tests.UseCases;

public sealed class AiReplyUseCaseTests
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
    public async Task Gather_NonHandoffSpeech_SpeaksAiReply()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var voiceSession = new VoiceSession(
            "voice_CA_TEST_AI",
            "tenant_123",
            "conversation_123",
            "twilio",
            "CA_TEST_AI",
            "+15555550123",
            "+18777804236",
            "in-progress",
            ConversationStage.Address,
            2,
            0,
            null,
            now,
            now);

        var tenantRepository = new Mock<ITenantRepository>();
        var voiceSessionRepository = new Mock<IVoiceSessionRepository>();
        var conversationRepository = new Mock<IConversationRepository>();
        var leadRepository = new Mock<ILeadRepository>();
        var eventRepository = new Mock<IEventRepository>();

        tenantRepository
            .Setup(x => x.GetByTenantIdAsync("tenant_123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Tenant);
        voiceSessionRepository
            .Setup(x => x.GetByProviderCallIdAsync("CA_TEST_AI", It.IsAny<CancellationToken>()))
            .ReturnsAsync(voiceSession);
        conversationRepository
            .Setup(x => x.GetByPhoneAsync("tenant_123", "+15555550123", ConversationChannel.Voice, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Conversation?)null);
        leadRepository
            .Setup(x => x.GetByConversationIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Lead?)null);

        var fakeAi = new FakeAiResponder { Reply = "AI gather answer" };
        var eventService = new EventService(eventRepository.Object);
        var conversationService = new ConversationService(
            conversationRepository.Object,
            new Mock<ILogger<ConversationService>>().Object);
        var orchestrator = new ConversationOrchestrator(
            conversationService,
            new LeadService(leadRepository.Object),
            eventService,
            new ConversationFlowEngine(),
            new Mock<ILogger<ConversationOrchestrator>>().Object,
            fakeAi);
        var sut = new HandleVoiceGatherUseCase(
            tenantRepository.Object,
            voiceSessionRepository.Object,
            orchestrator,
            eventService,
            new Mock<ILogger<HandleVoiceGatherUseCase>>().Object);

        var result = await sut.ExecuteAsync(
            new HandleVoiceGatherCommand("CA_TEST_AI", "My AC is not working", 0.95m),
            CancellationToken.None);

        Assert.Equal("AI gather answer", result.ReplyMessage);
        Assert.Equal(ConversationStage.Address, result.Stage);
        Assert.False(result.Completed);
        Assert.False(result.HandoffRequested);
        Assert.Equal("in-progress", voiceSession.Status);
    }

    [Fact]
    public async Task SmsUseCase_AiReply_SentViaOutboundSms()
    {
        var tenantRepo = new FakeTenantRepository();
        var conversationRepo = new FakeConversationRepository();
        var leadRepo = new FakeLeadRepository();
        var eventRepo = new FakeEventRepository();
        var sms = new FakeOutboundSmsService();
        var fakeAi = new FakeAiResponder { Reply = "AI sms answer" };

        var conversationService = new ConversationService(
            conversationRepo, NullLogger<ConversationService>.Instance);
        var leadService = new LeadService(leadRepo);
        var eventService = new EventService(eventRepo);
        var orchestrator = new ConversationOrchestrator(
            conversationService,
            leadService,
            eventService,
            new ConversationFlowEngine(),
            NullLogger<ConversationOrchestrator>.Instance,
            fakeAi);
        var sut = new HandleInboundSmsUseCase(
            tenantRepo,
            sms,
            orchestrator,
            eventService,
            NullLogger<HandleInboundSmsUseCase>.Instance);

        var result = await sut.ExecuteAsync(
            new HandleInboundSmsCommand(string.Empty, "+18777804236", "+15555550123", "Hello"),
            CancellationToken.None);

        Assert.Equal("AI sms answer", result.ReplyMessage);
        Assert.Equal("AI sms answer", sms.LastMessage);
        Assert.Equal("+15555550123", sms.LastToPhoneNumber);
    }

    private sealed class FakeAiResponder : IAiResponder
    {
        public string? Reply { get; set; }

        public Task<string> GenerateHvacResponseAsync(
            Tenant tenant,
            string customerPhone,
            IReadOnlyCollection<Message> conversationHistory,
            string inboundMessage,
            CancellationToken cancellationToken,
            AiReplyContext? context = null)
            => Task.FromResult(Reply ?? string.Empty);
    }

    private sealed class FakeTenantRepository : ITenantRepository
    {
        public Task<Tenant?> GetByTenantIdAsync(string tenantId, CancellationToken cancellationToken)
            => Task.FromResult<Tenant?>(new Tenant(tenantId, tenantId, "CoolAir HVAC", "+18777804236", "Houston TX", "America/Chicago", new BusinessHours("08:00", "18:00"), DateTimeOffset.UtcNow));

        public Task<Tenant?> GetByPhoneNumberAsync(string phoneNumber, CancellationToken cancellationToken)
        {
            if (string.Equals(phoneNumber, "+18777804236", StringComparison.Ordinal))
            {
                return Task.FromResult<Tenant?>(new Tenant("tenant_123", "tenant_123", "CoolAir HVAC", phoneNumber, "Houston TX", "America/Chicago", new BusinessHours("08:00", "18:00"), DateTimeOffset.UtcNow));
            }

            return Task.FromResult<Tenant?>(null);
        }
    }

    private sealed class FakeConversationRepository : IConversationRepository
    {
        private readonly Dictionary<string, Conversation> _store = new();

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
        public Task AddAsync(EventLog eventLog, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeOutboundSmsService : IOutboundSmsService
    {
        public string LastToPhoneNumber { get; private set; } = string.Empty;

        public string LastMessage { get; private set; } = string.Empty;

        public Task SendAsync(string tenantId, string toPhoneNumber, string message, CancellationToken cancellationToken)
        {
            LastToPhoneNumber = toPhoneNumber;
            LastMessage = message;
            return Task.CompletedTask;
        }
    }
}
