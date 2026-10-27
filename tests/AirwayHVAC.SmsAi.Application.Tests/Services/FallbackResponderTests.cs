// Proves the fallback chain without any real provider: hand-written fakes
// stand in for Azure (primary) and Baseten (secondary). A chain member that
// throws or returns empty must be skipped, and total failure must return
// empty — never an invented reply — so the orchestrator can use its static
// flow reply exactly as it does for a single failed provider today.

using AirwayHVAC.SmsAi.Domain.Entities;
using AirwayHVAC.SmsAi.Domain.Enums;
using AirwayHVAC.SmsAi.Domain.Interfaces;
using AirwayHVAC.SmsAi.Infrastructure.Services;
using Xunit;

namespace AirwayHVAC.SmsAi.Application.Tests.Services;

public sealed class FallbackResponderTests
{
    private static readonly Tenant Tenant = new(
        "tenant-1",
        "Acme HVAC",
        "client-1",
        "https://example.com",
        "+18777804236",
        "America/New_York",
        "gpt-6-luna-1",
        "You are an expert HVAC customer support assistant.");

    [Fact]
    public async Task PrimaryReply_Returned_SecondaryNotCalled()
    {
        var primary = new FakeAiResponder { Reply = "Azure answer" };
        var secondary = new FakeAiResponder { Reply = "Baseten answer" };
        var fallback = new FallbackResponder([primary, secondary]);

        var reply = await fallback.GenerateHvacResponseAsync(Tenant, "+15550001111", [], "Hello", CancellationToken.None);

        Assert.Equal("Azure answer", reply);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, secondary.CallCount);
    }

    [Fact]
    public async Task PrimaryThrows_SecondaryReplyReturned()
    {
        var primary = new FakeAiResponder { Throw = true };
        var secondary = new FakeAiResponder { Reply = "Baseten answer" };
        var fallback = new FallbackResponder([primary, secondary]);

        var reply = await fallback.GenerateHvacResponseAsync(Tenant, "+15550001111", [], "Hello", CancellationToken.None);

        Assert.Equal("Baseten answer", reply);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(1, secondary.CallCount);
    }

    [Fact]
    public async Task PrimaryEmpty_SecondaryReplyReturned()
    {
        var primary = new FakeAiResponder { Reply = "   " };
        var secondary = new FakeAiResponder { Reply = "Baseten answer" };
        var fallback = new FallbackResponder([primary, secondary]);

        var reply = await fallback.GenerateHvacResponseAsync(Tenant, "+15550001111", [], "Hello", CancellationToken.None);

        Assert.Equal("Baseten answer", reply);
        Assert.Equal(1, secondary.CallCount);
    }

    [Fact]
    public async Task AllFail_ReturnsEmpty_SoOrchestratorUsesStaticReply()
    {
        var primary = new FakeAiResponder { Throw = true };
        var secondary = new FakeAiResponder { Reply = string.Empty };
        var fallback = new FallbackResponder([primary, secondary]);

        var reply = await fallback.GenerateHvacResponseAsync(Tenant, "+15550001111", [], "Hello", CancellationToken.None);

        Assert.Equal(string.Empty, reply);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(1, secondary.CallCount);
    }

    [Fact]
    public async Task ContextPassedThrough_ToAnsweringResponder()
    {
        var primary = new FakeAiResponder { Throw = true };
        var secondary = new FakeAiResponder { Reply = "Baseten answer" };
        var fallback = new FallbackResponder([primary, secondary]);
        var context = new AiReplyContext(ConversationStage.Address, "AC not cooling", null, null, null);

        await fallback.GenerateHvacResponseAsync(Tenant, "+15550001111", [], "Hello", CancellationToken.None, context);

        Assert.NotNull(secondary.LastContext);
        Assert.Equal(ConversationStage.Address, secondary.LastContext!.Stage);
    }

    private sealed class FakeAiResponder : IAiResponder
    {
        public string Reply { get; init; } = string.Empty;

        public bool Throw { get; init; }

        public int CallCount { get; private set; }

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
            LastContext = context;
            if (Throw)
            {
                throw new InvalidOperationException("Simulated provider failure.");
            }

            return Task.FromResult(Reply);
        }
    }
}
