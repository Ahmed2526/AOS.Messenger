using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AOS.Messenger.Infrastructure.DependencyInjection;

/// <summary>
/// DI registration entry point for the Infrastructure layer.
/// Called from Web/Program.cs to register EF Core, Identity, and external service adapters.
/// </summary>
public static class InfrastructureServiceRegistration
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // EF Core DbContext, Identity, and external service registrations
        // will be added here as features are built.
        return services;
    }
}
