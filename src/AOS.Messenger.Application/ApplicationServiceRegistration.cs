using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AOS.Messenger.Application;

/// <summary>
/// DI registration entry point for the Application layer.
/// Called from Web/Program.cs to register application services.
/// </summary>
public static class ApplicationServiceRegistration
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IMessageService, MessageService>();
        services.AddScoped<IBlockService, BlockService>();
        services.AddScoped<IProfileService, ProfileService>();
        services.AddScoped<IUserDiscoveryService, UserDiscoveryService>();
        services.AddScoped<IReceiptService, ReceiptService>();
        services.AddScoped<IPresenceService, PresenceService>();
        services.AddScoped<ITypingService, TypingService>();

        return services;
    }
}
