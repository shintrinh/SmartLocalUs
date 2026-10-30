// Repo path: src/AirwayHVAC.SmsAi.Infrastructure/ServiceCollectionExtensions.cs
// PATCHED FILE — changes vs the original:
//  1. services.Configure<MetaOptions>(...) added next to the other options.
//  2. IAiResponder registration switches on the "AiProvider" setting:
//     "Meta" -> MetaResponder, anything else (default) -> OpenAiResponder.
//     The Azure path is untouched, so SMS keeps working when AiProvider is unset.
//  3. Day 5-6 auto-fallback (27/10): AiProvider "Fallback" registers a
//     FallbackResponder chain: Meta Model API first, then Baseten
//     (Muse Spark Open via a MetaResponder bound to the "Baseten" section),
//     then Fireworks (Muse Spark Open via a MetaResponder bound to the
//     "Fireworks" section), then Modal (Muse Spark Open via a MetaResponder
//     bound to the "Modal" section, 30/10). Azure OpenAI is temporarily out
//     of the chain (30/10): its code and default branch are untouched, so
//     restoring it is one line in the chain array below.
//     If Meta throws or returns empty, the chain tries Baseten, then Fireworks,
//     then Modal;
//     if all fail, the chain returns empty and the orchestrator falls back
//     to its static reply, exactly as it does for a single failed provider today.

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
        services.Configure<FireworksOptions>(configuration.GetSection(FireworksOptions.SectionName));
        services.Configure<ModalOptions>(configuration.GetSection(ModalOptions.SectionName));
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
            services.AddScoped<MetaResponder>();
            services.AddScoped<IAiResponder>(static serviceProvider =>
            {
                var meta = serviceProvider.GetRequiredService<MetaResponder>();
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
                var fireworksConfig = serviceProvider.GetRequiredService<IOptions<FireworksOptions>>().Value;
                var fireworksMeta = new MetaOptions
                {
                    BaseUrl = fireworksConfig.BaseUrl,
                    ApiKey = fireworksConfig.ApiKey,
                    Model = fireworksConfig.Model,
                    ReasoningEffort = fireworksConfig.ReasoningEffort,
                    MaxOutputTokens = fireworksConfig.MaxOutputTokens,
                    SystemPrompt = fireworksConfig.SystemPrompt,
                };
                var fireworks = new MetaResponder(
                    Options.Create(fireworksMeta),
                    serviceProvider.GetService<ILogger<MetaResponder>>());
                var modalConfig = serviceProvider.GetRequiredService<IOptions<ModalOptions>>().Value;
                var modalMeta = new MetaOptions
                {
                    BaseUrl = modalConfig.BaseUrl,
                    ApiKey = modalConfig.ApiKey,
                    Model = modalConfig.Model,
                    ReasoningEffort = modalConfig.ReasoningEffort,
                    MaxOutputTokens = modalConfig.MaxOutputTokens,
                    SystemPrompt = modalConfig.SystemPrompt,
                };
                var modal = new MetaResponder(
                    Options.Create(modalMeta),
                    serviceProvider.GetService<ILogger<MetaResponder>>());
                var logger = serviceProvider.GetService<ILogger<FallbackResponder>>();
                return new FallbackResponder([meta, baseten, fireworks, modal], logger);
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
