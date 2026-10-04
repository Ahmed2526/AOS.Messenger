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
        // Application services, validators, etc. will be registered here as features are built.
        return services;
    }
}
