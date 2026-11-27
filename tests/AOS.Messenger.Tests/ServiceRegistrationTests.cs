using AOS.Messenger.Application;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.Services;
using AOS.Messenger.Infrastructure;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using AOS.Messenger.Infrastructure.Storage;

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
        AssertScopedService<IMediaService, MediaService>(services);
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
        services.AddSingleton<IHostEnvironment>(new DevelopmentHostEnvironment());

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

        // Verify Phase 9 services
        AssertScopedService<IMediaPersistence, MediaPersistence>(services);
        Assert.NotNull(services.FirstOrDefault(s => s.ServiceType == typeof(IMediaStorage)));
        Assert.NotNull(services.FirstOrDefault(s => s.ServiceType == typeof(IMediaVerifier) && s.Lifetime == ServiceLifetime.Singleton));
        Assert.NotNull(services.FirstOrDefault(s => s.ServiceType == typeof(IValidateOptions<R2StorageOptions>)));
        Assert.NotNull(services.FirstOrDefault(s => s.ServiceType == typeof(IValidateOptions<AOS.Messenger.Application.Common.MediaOptions>)));
        Assert.NotNull(services.FirstOrDefault(s => s.ServiceType == typeof(MediaCleanupService) && s.Lifetime == ServiceLifetime.Singleton));

        // Verify resolution
        using var provider = services.BuildServiceProvider();
        Assert.IsType<LocalDiskMediaStorage>(provider.GetService<IMediaStorage>());
        Assert.IsType<MediaVerifier>(provider.GetService<IMediaVerifier>());
        var accessor = provider.GetService<IHttpContextAccessor>();
        Assert.NotNull(accessor);

        using var scope = provider.CreateScope();
        var currentUser = scope.ServiceProvider.GetService<ICurrentUser>();
        Assert.NotNull(currentUser);
        Assert.IsType<CurrentUser>(currentUser);

        // Fail-closed check: without IHostEnvironment and without R2 config, R2StorageOptions validation fails.
        var noEnvServices = new ServiceCollection();
        noEnvServices.AddInfrastructureServices(new ConfigurationBuilder().Build());
        using var noEnvProvider = noEnvServices.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => noEnvProvider.GetRequiredService<IOptions<R2StorageOptions>>().Value);
    }

    private sealed class DevelopmentHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "AOS.Messenger.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static void AssertScopedService<TService, TImpl>(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(TService));
        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(typeof(TImpl), descriptor.ImplementationType);
    }
}
