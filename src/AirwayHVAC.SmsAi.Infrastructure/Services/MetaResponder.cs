// Repo path: src/AirwayHVAC.SmsAi.Infrastructure/Services/MetaResponder.cs
// NEW FILE — Meta Model API implementation of IAiResponder.
// Same contract and message shape as OpenAiResponder; only the provider changes.
// Uses plain HttpClient against the OpenAI-compatible endpoint so reasoning_effort
// is sent explicitly in the JSON body (no dependency on the .NET SDK version's
// typed reasoning options). No new package reference required.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AirwayHVAC.SmsAi.Domain.Entities;
using AirwayHVAC.SmsAi.Domain.Interfaces;
using AirwayHVAC.SmsAi.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AirwayHVAC.SmsAi.Infrastructure.Services;

public sealed class MetaResponder : IAiResponder
{
    private static readonly HttpClient SharedClient = new();

    private readonly MetaOptions _options;

    public MetaResponder(IOptions<MetaOptions> options)
    {
        _options = options.Value;
    }

    public async Task<string> GenerateHvacResponseAsync(Tenant tenant, string customerPhone, IReadOnlyCollection<Message> conversationHistory, string inboundMessage, CancellationToken cancellationToken)
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

        foreach (var message in conversationHistory)
        {
            messages.Add(new { role = message.Role == "customer" ? "user" : "assistant", content = message.Text });
        }

        messages.Add(new { role = "user", content = inboundMessage });

        // Meta Model API is OpenAI-compatible. reasoning_effort is sent explicitly:
        // Spark reasons before responding, so "minimal" keeps time-to-first-token low.
        // max_completion_tokens must cover reasoning tokens, which are billed at the
        // output rate and count against the output cap.
        var payload = new
        {
            model,
            messages,
            reasoning_effort = _options.ReasoningEffort,
            max_completion_tokens = _options.MaxOutputTokens
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await SharedClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return (content ?? string.Empty).Trim();
    }
}
