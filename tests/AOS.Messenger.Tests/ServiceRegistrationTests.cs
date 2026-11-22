using AOS.Messenger.Application;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.Services;
using AOS.Messenger.Infrastructure;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AOS.Messenger.Tests;

public sealed class ServiceRegistrationTests
{
    [Fact]
    public void AddApplicationServices_ReturnsSameCollection_AndBuildsSuccessfully()
    {
        var services = new ServiceCollection();

        var result = services.AddApplicationServices();

        Assert.Same(services, result);

        // Verify application use-case services are registered
        AssertScopedService<IConversationService, ConversationService>(services);
        AssertScopedService<IMessageService, MessageService>(services);
        AssertScopedService<IBlockService, BlockService>(services);
        AssertScopedService<IProfileService, ProfileService>(services);
        AssertScopedService<IUserDiscoveryService, UserDiscoveryService>(services);
        AssertScopedService<IReceiptService, ReceiptService>(services);
        AssertScopedService<IPresenceService, PresenceService>(services);
        AssertScopedService<ITypingService, TypingService>(services);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider);
    }

    [Fact]
    public void AddInfrastructureServices_RegistersExpectedServices()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var result = services.AddInfrastructureServices(configuration);

        Assert.Same(services, result);

        // Verify descriptor for IHttpContextAccessor
        var httpAccessorDescriptor = services.FirstOrDefault(s => s.ServiceType == typeof(IHttpContextAccessor));
        Assert.NotNull(httpAccessorDescriptor);

        // Verify descriptor for ICurrentUser
        var currentUserDescriptor = services.FirstOrDefault(s => s.ServiceType == typeof(ICurrentUser));
        Assert.NotNull(currentUserDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, currentUserDescriptor.Lifetime);
        Assert.Equal(typeof(CurrentUser), currentUserDescriptor.ImplementationType);

        // Verify Phase 3 services
        AssertScopedService<IBlockPersistence, BlockPersistence>(services);
        AssertScopedService<IUserReader, UserReader>(services);
        AssertScopedService<IUserWriter, UserWriter>(services);

        // Verify Phase 4 services
        AssertScopedService<IConversationPersistence, ConversationPersistence>(services);

        // Verify Phase 5 services
        AssertScopedService<IMessagePersistence, MessagePersistence>(services);

        // Verify Phase 7 services
        AssertScopedService<IUserPresencePersistence, UserPresencePersistence>(services);

        // Verify resolution
        using var provider = services.BuildServiceProvider();
        var accessor = provider.GetService<IHttpContextAccessor>();
        Assert.NotNull(accessor);

        using var scope = provider.CreateScope();
        var currentUser = scope.ServiceProvider.GetService<ICurrentUser>();
        Assert.NotNull(currentUser);
        Assert.IsType<CurrentUser>(currentUser);
    }

    private static void AssertScopedService<TService, TImpl>(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(TService));
        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(typeof(TImpl), descriptor.ImplementationType);
    }
}
