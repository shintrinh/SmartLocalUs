// Repo path: src/AirwayHVAC.SmsAi.Infrastructure/Services/MetaResponder.cs
// Meta Model API implementation of IAiResponder.
// Same contract and message shape as OpenAiResponder; only the provider changes.
// Uses plain HttpClient against the OpenAI-compatible endpoint so reasoning_effort
// is sent explicitly in the JSON body (no dependency on the .NET SDK version's
// typed reasoning options). No new package reference required.
//
// Day 3-4 (hoàn thiện calling) additions:
//   1. Stage guidance: when the orchestrator passes an AiReplyContext, it is
//      added as a system message, so the model asks for the next missing field
//      (or reads back details at Confirm) instead of diverging from the stage
//      machine. The guidance is never stored in conversation history.
//   2. book_appointment tool (Meta AI Tooling criterion): the model may call
//      it when the customer asks to book. One tool round only: tool results go
//      back in a second request WITHOUT tools, forcing a text answer, so the
//      loop cannot run away. The tool records the structured booking request;
//      it is not a booking backend — lead creation still belongs to
//      LeadService, which builds leads from conversation fields.
//   3. Tool-call logging (27/10, sau test voice qua curl): every tool call
//      the model makes is logged (name + arguments + result status), because
//      a Completed conversation alone does not prove the tool ran. The logger
//      is an optional ctor param so existing constructions (incl. tests)
//      keep compiling; when no logger is injected, logging is silently skipped.
//      NOTE: arguments can contain the caller's name — test runs use
//      synthetic data; treat these logs accordingly with real customers.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AirwayHVAC.SmsAi.Domain.Entities;
using AirwayHVAC.SmsAi.Domain.Interfaces;
using AirwayHVAC.SmsAi.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AirwayHVAC.SmsAi.Infrastructure.Services;

public sealed class MetaResponder : IAiResponder
{
    private static readonly HttpClient SharedClient = new();

    private readonly MetaOptions _options;

    private readonly ILogger<MetaResponder>? _logger;

    public MetaResponder(IOptions<MetaOptions> options, ILogger<MetaResponder>? logger = null)
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
        var apiKey = _options.ApiKey;
        var model = _options.Model;
        var baseUrl = _options.BaseUrl;

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(baseUrl))
        {
            return "Thanks for contacting us. Please share your address and preferred appointment time.";
        }

        // Message shape mirrors OpenAiResponder exactly: system prompt, tenant
        // context, history (customer -> user, otherwise assistant), then inbound.
        var messages = new List<object>
        {
            new { role = "system", content = _options.SystemPrompt },
            new { role = "system", content = $"Tenant {tenant.CompanyName} in {tenant.ServiceArea}, business hours {tenant.BusinessHours.Open}-{tenant.BusinessHours.Close} {tenant.Timezone}." }
        };

        if (context is not null)
        {
            messages.Add(new { role = "system", content = context.BuildStageGuidance() });
        }

        foreach (var message in conversationHistory)
        {
            messages.Add(new { role = message.Role == "customer" ? "user" : "assistant", content = message.Text });
        }

        messages.Add(new { role = "user", content = inboundMessage });

        // Meta Model API is OpenAI-compatible. reasoning_effort is sent explicitly:
        // Spark reasons before responding, so "minimal" keeps time-to-first-token low.
        // max_completion_tokens must cover reasoning tokens, which are billed at the
        // output rate and count against the output cap.
        // tool_choice is "auto" only on this API, per the verified docs.
        var payload = new
        {
            model,
            messages,
            reasoning_effort = _options.ReasoningEffort,
            max_completion_tokens = _options.MaxOutputTokens,
            tools = BookAppointmentTools,
            tool_choice = "auto"
        };

        using var response = await SendAsync(baseUrl, apiKey, payload, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var messageElement = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message");

        if (messageElement.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
        {
            // One tool round: assistant message (with its tool calls) + one tool
            // result per call, then a second request without tools so the model
            // must answer in text. A second round of tool calls is ignored by
            // design — we take whatever text comes back.
            var followup = new List<object>(messages);
            var callsJson = new List<object>();
            foreach (var call in toolCalls.EnumerateArray())
            {
                callsJson.Add(JsonSerializer.Deserialize<object>(call.GetRawText())!);
            }

            followup.Add(new { role = "assistant", content = (string?)null, tool_calls = callsJson });

            foreach (var call in toolCalls.EnumerateArray())
            {
                var callId = call.GetProperty("id").GetString() ?? string.Empty;
                var functionName = call.GetProperty("function").GetProperty("name").GetString() ?? string.Empty;
                var arguments = call.GetProperty("function").GetProperty("arguments").GetString() ?? "{}";
                var status = functionName == "book_appointment" ? "received" : "unknown_tool";
                _logger?.LogInformation("Meta tool call {ToolName} ({Status}) arguments {Arguments}", functionName, status, arguments);
                var result = functionName == "book_appointment"
                    ? $"{{\"status\":\"received\",\"arguments\":{arguments}}}"
                    : "{\"status\":\"unknown_tool\"}";
                followup.Add(new { role = "tool", tool_call_id = callId, content = result });
            }

            var secondPayload = new
            {
                model,
                messages = followup,
                reasoning_effort = _options.ReasoningEffort,
                max_completion_tokens = _options.MaxOutputTokens
            };

            using var secondResponse = await SendAsync(baseUrl, apiKey, secondPayload, cancellationToken);
            using var secondDocument = await JsonDocument.ParseAsync(await secondResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var secondContent = secondDocument.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            _logger?.LogInformation("Meta tool loop completed, returning final text.");
            return (secondContent ?? string.Empty).Trim();
        }

        var content = messageElement.GetProperty("content").GetString();

        return (content ?? string.Empty).Trim();
    }

    private static async Task<HttpResponseMessage> SendAsync(string baseUrl, string apiKey, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await SharedClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    // Single tool, shared shape with OpenAiResponder. caller_name is optional
    // (the customer often books before giving a name); day + service are
    // required so a call always carries a schedulable request.
    private static readonly object[] BookAppointmentTools =
    [
        new
        {
            type = "function",
            function = new
            {
                name = "book_appointment",
                description = "Record a booking request with the details the customer provided. Use it when the customer asks to book or schedule an appointment.",
                parameters = new
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
                }
            }
        }
    ];
}
