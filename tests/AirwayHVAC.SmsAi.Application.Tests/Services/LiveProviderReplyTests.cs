// Live-provider tests: the SAME orchestrator path the fake tests proved, but
// with the REAL providers — Azure OpenAI first, then Meta. No Twilio, no Cosmos,
// no Functions host: the orchestrator runs in-process against the fake repos,
// and only the AI call goes to the real API.
// Each test SKIPS (visibly, via test output) when its credentials are missing,
// so `dotnet test --filter LiveProvider` is safe to run any time:
//   - Azure runs when AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY are set
//     (AZURE_OPENAI_DEPLOYMENT optional, default gpt-4o-mini).
//   - Meta runs when META_API_KEY is set
//     (META_BASE_URL / META_MODEL optional, defaults api.meta.ai/v1 + muse-spark-1.3).
// The message sent is synthetic ("AC not cooling") — never real customer data,
// which also keeps the Meta free-credit data-use caveat out of scope.
// The key assertion is negative on purpose: the reply must differ from BOTH
// known fallbacks (the flow-engine static reply AND the providers' own
// missing-key fallback), which proves the live provider actually answered
// instead of any fallback path firing.

using AirwayHVAC.SmsAi.Application.Services;
using AirwayHVAC.SmsAi.Domain.Entities;
using AirwayHVAC.SmsAi.Domain.Enums;
using AirwayHVAC.SmsAi.Domain.Interfaces;
using AirwayHVAC.SmsAi.Infrastructure.Configuration;
using AirwayHVAC.SmsAi.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AirwayHVAC.SmsAi.Application.Tests.Services;

public sealed class LiveProviderReplyTests
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

    private const string FlowEngineStaticReply = "Thanks. What is the service address?";

    private const string ProviderMissingKeyFallback =
        "Thanks for contacting us. Please share your address and preferred appointment time.";

    private readonly ITestOutputHelper _output;

    public LiveProviderReplyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Azure_LiveProvider_RepliesThroughOrchestrator()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
        {
            _output.WriteLine("SKIPPED: set AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY to run the live Azure test.");
            return;
        }

        var provider = new OpenAiResponder(new StaticOptions<AzureOpenAiOptions>(new AzureOpenAiOptions
        {
            Endpoint = endpoint,
            ApiKey = apiKey,
            DeploymentName = string.IsNullOrWhiteSpace(deployment) ? "gpt-4o-mini" : deployment
        }));

        var reply = await RunThroughOrchestrator(provider);

        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.NotEqual(FlowEngineStaticReply, reply);
        Assert.NotEqual(ProviderMissingKeyFallback, reply);
    }

    [Fact]
    public async Task Meta_LiveProvider_RepliesThroughOrchestrator()
    {
        var apiKey = Environment.GetEnvironmentVariable("META_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _output.WriteLine("SKIPPED: set META_API_KEY to run the live Meta test.");
            return;
        }

        var baseUrl = Environment.GetEnvironmentVariable("META_BASE_URL");
        var model = Environment.GetEnvironmentVariable("META_MODEL");
        var provider = new MetaResponder(new StaticOptions<MetaOptions>(new MetaOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.meta.ai/v1" : baseUrl,
            ApiKey = apiKey,
            Model = string.IsNullOrWhiteSpace(model) ? "muse-spark-1.3" : model
        }));

        var reply = await RunThroughOrchestrator(provider);

        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.NotEqual(FlowEngineStaticReply, reply);
        Assert.NotEqual(ProviderMissingKeyFallback, reply);
    }

    private static async Task<string> RunThroughOrchestrator(IAiResponder provider)
    {
        var conversationService = new ConversationService(
            new FakeConversationRepository(), NullLogger<ConversationService>.Instance);
        var leadService = new LeadService(new FakeLeadRepository());
        var eventService = new EventService(new FakeEventRepository());
        var orchestrator = new ConversationOrchestrator(
            conversationService,
            leadService,
            eventService,
            new ConversationFlowEngine(),
            NullLogger<ConversationOrchestrator>.Instance,
            provider);

        var result = await orchestrator.ProcessCustomerMessageAsync(
            Tenant, "+15550001111", "AC not cooling", ConversationChannel.Sms,
            DateTimeOffset.UtcNow, CancellationToken.None);
        return result.ReplyMessage;
    }

    private sealed class StaticOptions<T> : IOptions<T>
        where T : class
    {
        public StaticOptions(T value)
        {
            Value = value;
        }

        public T Value { get; }
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
}
