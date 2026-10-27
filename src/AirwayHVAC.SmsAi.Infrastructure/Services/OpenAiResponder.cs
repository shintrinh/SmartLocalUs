// Repo path: src/AirwayHVAC.SmsAi.Infrastructure/Services/OpenAiResponder.cs
// Azure OpenAI implementation of IAiResponder (Alen's original design).
//
// Day 3-4 (hoàn thiện calling) additions, mirroring MetaResponder so the
// stage story is identical on both providers:
//   1. Stage guidance: when the orchestrator passes an AiReplyContext, it is
//      added as a system message (never stored in conversation history).
//   2. book_appointment tool, one round only: tool results go back in a
//      second request WITHOUT tools, forcing a text answer. The tool records
//      the structured booking request; it is not a booking backend — lead
//      creation still belongs to LeadService, from conversation fields.
//   3. Tool-call logging (27/10, sau test voice qua curl): every tool call
//      is logged (name + arguments + result status), because a Completed
//      conversation alone does not prove the tool ran. Optional ctor param
//      so existing constructions keep compiling. NOTE: arguments can contain
//      the caller's name — test runs use synthetic data; treat these logs
//      accordingly with real customers.

using AirwayHVAC.SmsAi.Domain.Entities;
using AirwayHVAC.SmsAi.Domain.Interfaces;
using AirwayHVAC.SmsAi.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace AirwayHVAC.SmsAi.Infrastructure.Services;

public sealed class OpenAiResponder : IAiResponder
{
    private readonly AzureOpenAiOptions _options;

    private readonly ILogger<OpenAiResponder>? _logger;

    public OpenAiResponder(Microsoft.Extensions.Options.IOptions<AzureOpenAiOptions> options, ILogger<OpenAiResponder>? logger = null)
    {
        _options = options.Value;
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
        var endpoint = _options.Endpoint;
        var deployment = _options.DeploymentName;
        var apiKey = _options.ApiKey;

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deployment) || string.IsNullOrWhiteSpace(apiKey))
        {
            return "Thanks for contacting us. Please share your address and preferred appointment time.";
        }

        ChatClient client = new(
            credential: new ApiKeyCredential(apiKey),
            model: deployment,
            options: new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(_options.SystemPrompt),
            new SystemChatMessage($"Tenant {tenant.CompanyName} in {tenant.ServiceArea}, business hours {tenant.BusinessHours.Open}-{tenant.BusinessHours.Close} {tenant.Timezone}.")
        };

        if (context is not null)
        {
            messages.Add(new SystemChatMessage(context.BuildStageGuidance()));
        }

        foreach (var message in conversationHistory)
        {
            messages.Add(message.Role == "customer" ? new UserChatMessage(message.Text) : new AssistantChatMessage(message.Text));
        }

        messages.Add(new UserChatMessage(inboundMessage));

        var options = new ChatCompletionOptions();
        options.Tools.Add(ChatTool.CreateFunctionTool(
            "book_appointment",
            "Record a booking request with the details the customer provided. Use it when the customer asks to book or schedule an appointment.",
            BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new
                {
                    caller_name = new { type = "string", description = "Customer name, if provided" },
                    day = new { type = "string", description = "Day for the appointment, e.g. Saturday" },
                    service = new { type = "string", description = "Service requested, e.g. haircut, AC repair" },
                    time = new { type = "string", description = "Preferred time, if provided" }
                },
                required = new[] { "day", "service" }
            })));

        var completion = await client.CompleteChatAsync(messages, options, cancellationToken);
        if (completion.Value.ToolCalls.Count == 0)
        {
            return TextOf(completion.Value);
        }

        // One tool round, mirroring MetaResponder: assistant message (with its
        // tool calls) + one tool result per call, then a second request
        // without tools so the model must answer in text.
        var followup = new List<ChatMessage>(messages);
        followup.Add(new AssistantChatMessage(completion.Value));
        foreach (var call in completion.Value.ToolCalls)
        {
            var status = call.FunctionName == "book_appointment" ? "received" : "unknown_tool";
            _logger?.LogInformation("Azure tool call {ToolName} ({Status}) arguments {Arguments}", call.FunctionName, status, call.FunctionArguments.ToString());
            var result = call.FunctionName == "book_appointment"
                ? $"{{\"status\":\"received\",\"arguments\":{call.FunctionArguments}}}"
                : "{\"status\":\"unknown_tool\"}";
            followup.Add(new ToolChatMessage(call.Id, result));
        }

        var second = await client.CompleteChatAsync(followup, cancellationToken: cancellationToken);
        _logger?.LogInformation("Azure tool loop completed, returning final text.");
        return TextOf(second.Value);
    }

    private static string TextOf(ChatCompletion completion)
        => string.Join(string.Empty, completion.Content.Select(x => x.Text)).Trim();
}
