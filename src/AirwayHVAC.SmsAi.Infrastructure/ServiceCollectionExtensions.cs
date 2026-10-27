// Repo path: src/AirwayHVAC.SmsAi.Infrastructure/ServiceCollectionExtensions.cs
// PATCHED FILE — changes vs the original:
//  1. services.Configure<MetaOptions>(...) added next to the other options.
//  2. IAiResponder registration switches on the "AiProvider" setting:
//     "Meta" -> MetaResponder, anything else (default) -> OpenAiResponder.
//     The Azure path is untouched, so SMS keeps working when AiProvider is unset.
//  3. Day 5-6 auto-fallback (27/10): AiProvider "Fallback" registers a
//     FallbackResponder chain: Azure OpenAI first (deployment from
//     AzureOpenAiOptions, e.g. gpt-6-luna-1), then Baseten (Muse Spark Open
//     via a MetaResponder bound to the "Baseten" section). If Azure throws
//     or returns empty, the chain tries Baseten; if all fail, the chain
//     returns empty and the orchestrator falls back to its static reply,
//     exactly as it does for a single failed provider today.

using AirwayHVAC.SmsAi.Application.Services;
using AirwayHVAC.SmsAi.Domain.Interfaces;
using AirwayHVAC.SmsAi.Infrastructure.Configuration;
using AirwayHVAC.SmsAi.Infrastructure.Repositories;
using AirwayHVAC.SmsAi.Infrastructure.Services;
using AirwayHVAC.SmsAi.Shared.Abstractions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AirwayHVAC.SmsAi.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TwilioOptions>(configuration.GetSection(TwilioOptions.SectionName));
        services.Configure<TwilioVoiceOptions>(configuration.GetSection(TwilioVoiceOptions.SectionName));
        services.Configure<AzureOpenAiOptions>(configuration.GetSection(AzureOpenAiOptions.SectionName));
        services.Configure<MetaOptions>(configuration.GetSection(MetaOptions.SectionName));
        services.Configure<BasetenOptions>(configuration.GetSection(BasetenOptions.SectionName));
        services.Configure<CosmosOptions>(configuration.GetSection(CosmosOptions.SectionName));
        services.Configure<TenantRoutingOptions>(configuration.GetSection(TenantRoutingOptions.SectionName));

        services.AddSingleton(static serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CosmosOptions>>().Value;
            return new CosmosClient(options.Endpoint, options.Key, new CosmosClientOptions
            {
                ApplicationName = "AirwayHVAC.SmsAi",
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
                },
            });
        });

        var aiProvider = configuration["AiProvider"];
        if (string.Equals(aiProvider, "Fallback", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<OpenAiResponder>();
            services.AddScoped<IAiResponder>(static serviceProvider =>
            {
                var azure = serviceProvider.GetRequiredService<OpenAiResponder>();
                var basetenConfig = serviceProvider.GetRequiredService<IOptions<BasetenOptions>>().Value;
                var basetenMeta = new MetaOptions
                {
                    BaseUrl = basetenConfig.BaseUrl,
                    ApiKey = basetenConfig.ApiKey,
                    Model = basetenConfig.Model,
                    ReasoningEffort = basetenConfig.ReasoningEffort,
                    MaxOutputTokens = basetenConfig.MaxOutputTokens,
                    SystemPrompt = basetenConfig.SystemPrompt,
                };
                var baseten = new MetaResponder(
                    Options.Create(basetenMeta),
                    serviceProvider.GetService<ILogger<MetaResponder>>());
                var logger = serviceProvider.GetService<ILogger<FallbackResponder>>();
                return new FallbackResponder([azure, baseten], logger);
            });
        }
        else if (string.Equals(aiProvider, "Meta", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IAiResponder, MetaResponder>();
        }
        else
        {
            services.AddScoped<IAiResponder, OpenAiResponder>();
        }

        services.AddScoped<IOutboundSmsService, TwilioSmsService>();

        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<ILeadRepository, LeadRepository>();
        services.AddScoped<IEventRepository, EventRepository>();
        services.AddScoped<IVoiceSessionRepository, VoiceSessionRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ITenantResolver, HeaderTenantResolver>();

        services.AddScoped<ConversationService>();
        services.AddScoped<ConversationFlowEngine>();
        services.AddScoped<ConversationOrchestrator>();
        services.AddScoped<LeadService>();
        services.AddScoped<EventService>();
        services.AddScoped<DashboardService>();

        return services;
    }
}
