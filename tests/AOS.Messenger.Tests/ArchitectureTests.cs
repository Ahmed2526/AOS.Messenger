using System.Reflection;
using System.Runtime.CompilerServices;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Common;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace AOS.Messenger.Tests;

/// <summary>
/// Architectural guardrail tests asserting the dependency direction rules, service layer locations,
/// and banned library/pattern constraints specified in AGENTS.md.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly DomainAssembly = typeof(Entity<>).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(NotFoundException).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(CurrentUser).Assembly;
    private static readonly Assembly WebAssembly = typeof(HomeController).Assembly;

    public static readonly Type[] UseCaseServiceInterfaces =
    [
        typeof(IConversationService),
        typeof(IMessageService),
        typeof(IMediaService),
        typeof(IAvatarService),
        typeof(IStickerService),
        typeof(IBlockService),
        typeof(IProfileService),
        typeof(IUserDiscoveryService),
        typeof(IReceiptService),
        typeof(IPresenceService),
        typeof(ITypingService)
    ];

    public static IEnumerable<object[]> GetUseCaseServiceInterfaces()
    {
        return UseCaseServiceInterfaces.Select(iface => new object[] { iface });
    }

    // =========================================================================
    // 1. Strict Project / Assembly Dependency Direction
    // =========================================================================

    [Fact]
    public void Domain_MustNotReference_AnyOtherProject()
    {
        var referencedAssemblies = DomainAssembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        Assert.DoesNotContain(referencedAssemblies, name => name.Contains("Application", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.Contains("Web", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.StartsWith("AOS.Messenger", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Domain_MustNotReference_EntityFrameworkCore_Or_AspNetCoreIdentity()
    {
        var referencedAssemblies = DomainAssembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        Assert.DoesNotContain(referencedAssemblies, name => name.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.Contains("Identity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Application_MustReferenceDomain_AndNotReference_Infrastructure_Or_Web()
    {
        var referencedAssemblies = ApplicationAssembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        Assert.Contains(referencedAssemblies, name => name.Equals("AOS.Messenger.Domain", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.Contains("Web", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Application_MustNotReference_EntityFrameworkCore()
    {
        var referencedAssemblies = ApplicationAssembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        Assert.DoesNotContain(referencedAssemblies, name => name.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Infrastructure_MustReference_ApplicationAndDomain_AndNotReference_Web()
    {
        var referencedAssemblies = InfrastructureAssembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        Assert.Contains(referencedAssemblies, name => name.Equals("AOS.Messenger.Application", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(referencedAssemblies, name => name.Equals("AOS.Messenger.Domain", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.Contains("Web", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Infrastructure_InternalsVisibleTo_MustOnlyTarget_TestsAssembly()
    {
        var attributes = InfrastructureAssembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .ToList();

        Assert.NotEmpty(attributes);
        Assert.All(attributes, attr =>
            Assert.Equal("AOS.Messenger.Tests", attr.AssemblyName, StringComparer.Ordinal));
    }

    [Fact]
    public void Web_MustReference_Application_Domain_AndInfrastructure()
    {
        var referencedAssemblies = WebAssembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        Assert.Contains(referencedAssemblies, name => name.Equals("AOS.Messenger.Application", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(referencedAssemblies, name => name.Equals("AOS.Messenger.Domain", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(referencedAssemblies, name => name.Equals("AOS.Messenger.Infrastructure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Controllers_MustBeSealed()
    {
        var controllerTypes = WebAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(controllerTypes);
        foreach (var controller in controllerTypes)
        {
            Assert.True(controller.IsSealed, $"Controller '{controller.FullName}' must be sealed.");
        }
    }

    [Fact]
    public void WebPresentationTypes_Controllers_MustNotDependOn_InfrastructureTypes()
    {
        var controllerTypes = WebAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(controllerTypes);
        foreach (var controller in controllerTypes)
        {
            AssertTypeHierarchyAndMembersNotFromInfrastructure(controller);
        }
    }

    [Fact]
    public void Hubs_MustBeSealed_And_HaveAuthorizeAttribute()
    {
        var hubTypes = WebAssembly.GetTypes()
            .Where(t => typeof(Hub).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(hubTypes);
        foreach (var hub in hubTypes)
        {
            Assert.True(hub.IsSealed, $"Hub '{hub.FullName}' must be sealed.");
            Assert.True(
                hub.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Length > 0,
                $"Hub '{hub.FullName}' must have [Authorize] attribute.");
        }
    }

    [Fact]
    public void Hubs_MustNotDependOn_InfrastructureTypes()
    {
        var hubTypes = WebAssembly.GetTypes()
            .Where(t => typeof(Hub).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(hubTypes);
        foreach (var hub in hubTypes)
        {
            AssertTypeHierarchyAndMembersNotFromInfrastructure(hub);
        }
    }

    [Fact]
    public void WebMiddleware_MustNotDependOn_InfrastructureTypes()
    {
        var middlewareTypes = WebAssembly.GetTypes()
            .Where(t => t.Namespace?.Contains("Middleware", StringComparison.OrdinalIgnoreCase) == true && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(middlewareTypes);
        foreach (var middleware in middlewareTypes)
        {
            AssertTypeHierarchyAndMembersNotFromInfrastructure(middleware);
        }
    }

    [Fact]
    public void AllWebPresentationTypes_ExceptProgram_MustNotDependOn_InfrastructureTypes()
    {
        var webTypes = WebAssembly.GetTypes()
            .Where(t => !IsCompilerGenerated(t) && !t.Name.Equals("Program", StringComparison.Ordinal) && !t.Name.StartsWith("<"))
            .ToList();

        Assert.NotEmpty(webTypes);
        foreach (var type in webTypes)
        {
            AssertTypeHierarchyAndMembersNotFromInfrastructure(type);
        }
    }

    // =========================================================================
    // 2. Service Layer Locations
    // =========================================================================

    [Theory]
    [MemberData(nameof(GetUseCaseServiceInterfaces))]
    public void UseCaseServices_MustResideIn_ApplicationAssembly(Type serviceInterface)
    {
        var appImplementations = ApplicationAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && serviceInterface.IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(appImplementations);
        foreach (var impl in appImplementations)
        {
            Assert.Equal(ApplicationAssembly, impl.Assembly);
            Assert.StartsWith("AOS.Messenger.Application", impl.Namespace);
        }

        var infraImplementations = InfrastructureAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && serviceInterface.IsAssignableFrom(t))
            .ToList();

        Assert.Empty(infraImplementations);
    }

    [Fact]
    public void Infrastructure_MustNotContain_AnyClassesImplementing_UseCaseServiceInterfaces()
    {
        foreach (var serviceInterface in UseCaseServiceInterfaces)
        {
            var infraImplementations = InfrastructureAssembly.GetTypes()
                .Where(t => serviceInterface.IsAssignableFrom(t))
                .ToList();

            Assert.Empty(infraImplementations);
        }
    }

    [Fact]
    public void AllUseCaseServiceImplementations_AcrossSolution_MustResideOnlyIn_Application()
    {
        var solutionAssemblies = new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly, WebAssembly };

        foreach (var serviceInterface in UseCaseServiceInterfaces)
        {
            var allImplementations = solutionAssemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsClass && !t.IsAbstract && serviceInterface.IsAssignableFrom(t))
                .ToList();

            Assert.NotEmpty(allImplementations);
            foreach (var impl in allImplementations)
            {
                Assert.True(
                    impl.Assembly == ApplicationAssembly,
                    $"Implementation '{impl.FullName}' of interface '{serviceInterface.Name}' must reside in Application assembly, but was found in '{impl.Assembly.GetName().Name}'."
                );
            }
        }
    }

    // =========================================================================
    // 3. Zero References to Banned Libraries and Patterns Across the Solution
    // =========================================================================

    [Theory]
    [InlineData("MediatR")]
    [InlineData("StackExchange.Redis")]
    [InlineData("RabbitMQ")]
    [InlineData("Confluent.Kafka")]
    public void SolutionAssemblies_MustNotReference_BannedLibraries(string bannedLibrary)
    {
        var solutionAssemblies = new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly, WebAssembly };

        foreach (var assembly in solutionAssemblies)
        {
            var referencedAssemblies = assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .ToList();

            Assert.DoesNotContain(
                referencedAssemblies,
                name => name.Contains(bannedLibrary, StringComparison.OrdinalIgnoreCase)
            );
        }
    }

    [Theory]
    [InlineData("MediatR")]
    [InlineData("StackExchange.Redis")]
    [InlineData("RabbitMQ")]
    [InlineData("Confluent.Kafka")]
    public void AppDomain_MustNotLoad_BannedLibraries(string bannedLibrary)
    {
        var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name!)
            .ToList();

        Assert.DoesNotContain(
            loadedAssemblies,
            name => name.Contains(bannedLibrary, StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void SolutionTypes_MustNotUse_GenericRepositoryPattern()
    {
        var solutionAssemblies = new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly, WebAssembly };
        var allTypes = solutionAssemblies.SelectMany(a => a.GetTypes()).ToList();

        foreach (var type in allTypes)
        {
            var isGenericRepo = (type.IsGenericTypeDefinition && type.Name.StartsWith("IRepository", StringComparison.OrdinalIgnoreCase))
                || type.Name.StartsWith("IRepository`", StringComparison.OrdinalIgnoreCase)
                || type.Name.Equals("IRepository", StringComparison.OrdinalIgnoreCase);

            Assert.False(
                isGenericRepo,
                $"Banned generic repository pattern detected: '{type.FullName}'. Generic repositories are prohibited by AGENTS.md."
            );
        }
    }

    [Fact]
    public void SolutionTypes_MustNotUse_UnitOfWorkPattern()
    {
        var solutionAssemblies = new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly, WebAssembly };
        var allTypes = solutionAssemblies.SelectMany(a => a.GetTypes()).ToList();

        foreach (var type in allTypes)
        {
            var isUnitOfWork = type.Name.Contains("UnitOfWork", StringComparison.OrdinalIgnoreCase);

            Assert.False(
                isUnitOfWork,
                $"Banned Unit of Work pattern detected: '{type.FullName}'. Ceremonial IUnitOfWork is prohibited by AGENTS.md."
            );
        }
    }

    [Fact]
    public void SolutionTypes_MustNotImplementOrDependOn_MediatR()
    {
        var solutionAssemblies = new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly, WebAssembly };
        var allTypes = solutionAssemblies.SelectMany(a => a.GetTypes()).ToList();

        foreach (var type in allTypes)
        {
            var implementsMediatR = type.GetInterfaces()
                .Any(i => i.Namespace?.StartsWith("MediatR", StringComparison.OrdinalIgnoreCase) == true);

            Assert.False(
                implementsMediatR,
                $"Type '{type.FullName}' implements banned MediatR interface. MediatR is prohibited by AGENTS.md."
            );
        }
    }

    [Fact]
    public void SolutionTypes_MustNotResideIn_BannedMessagingOrCacheNamespaces()
    {
        var solutionAssemblies = new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly, WebAssembly };
        var allTypes = solutionAssemblies.SelectMany(a => a.GetTypes()).ToList();

        foreach (var type in allTypes)
        {
            var ns = type.Namespace ?? string.Empty;
            Assert.False(ns.Contains("Redis", StringComparison.OrdinalIgnoreCase), $"Type '{type.FullName}' is in banned Redis namespace.");
            Assert.False(ns.Contains("RabbitMQ", StringComparison.OrdinalIgnoreCase), $"Type '{type.FullName}' is in banned RabbitMQ namespace.");
            Assert.False(ns.Contains("Kafka", StringComparison.OrdinalIgnoreCase), $"Type '{type.FullName}' is in banned Kafka namespace.");
        }
    }

    [Fact]
    public void ApplicationAssembly_MustNotReference_ApplicationDbContext()
    {
        var applicationTypes = ApplicationAssembly.GetTypes();
        foreach (var type in applicationTypes)
        {
            AssertTypeHierarchyAndMembersDoNotReference(type, typeof(ApplicationDbContext));
        }
    }

    [Fact]
    public void WebControllers_MustNotReference_ApplicationDbContext()
    {
        var controllerTypes = WebAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(controllerTypes);
        foreach (var controller in controllerTypes)
        {
            AssertTypeHierarchyAndMembersDoNotReference(controller, typeof(ApplicationDbContext));
        }
    }

    [Fact]
    public void WebHubs_MustNotReference_ApplicationDbContext()
    {
        var hubTypes = WebAssembly.GetTypes()
            .Where(t => typeof(Hub).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(hubTypes);
        foreach (var hub in hubTypes)
        {
            AssertTypeHierarchyAndMembersDoNotReference(hub, typeof(ApplicationDbContext));
        }
    }

    [Fact]
    public void AllWebTypesExceptProgram_MustNotReference_ApplicationDbContext()
    {
        var webTypes = WebAssembly.GetTypes()
            .Where(t => !IsCompilerGenerated(t) && !t.Name.Equals("Program", StringComparison.Ordinal) && !t.Name.StartsWith("<"))
            .ToList();

        Assert.NotEmpty(webTypes);
        foreach (var type in webTypes)
        {
            AssertTypeHierarchyAndMembersDoNotReference(type, typeof(ApplicationDbContext));
        }
    }

    // =========================================================================
    // Inspection Helpers
    // =========================================================================

    private static bool IsCompilerGenerated(Type type)
    {
        return type.GetCustomAttribute<CompilerGeneratedAttribute>() != null ||
               type.Name.StartsWith("<") ||
               (type.DeclaringType != null && IsCompilerGenerated(type.DeclaringType));
    }

    private static void AssertTypeHierarchyAndMembersNotFromInfrastructure(Type targetType)
    {
        if (targetType.BaseType != null && targetType.BaseType != typeof(object))
        {
            AssertTypeNotFromInfrastructure(targetType.BaseType, targetType.FullName!, "base class");
        }

        foreach (var iface in targetType.GetInterfaces())
        {
            AssertTypeNotFromInfrastructure(iface, targetType.FullName!, "interface");
        }

        foreach (var constructor in targetType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                AssertTypeNotFromInfrastructure(parameter.ParameterType, targetType.FullName!, $"constructor parameter '{parameter.Name}'");
            }
        }

        foreach (var field in targetType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            AssertTypeNotFromInfrastructure(field.FieldType, targetType.FullName!, $"field '{field.Name}'");
        }

        foreach (var prop in targetType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            AssertTypeNotFromInfrastructure(prop.PropertyType, targetType.FullName!, $"property '{prop.Name}'");
        }

        foreach (var method in targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            AssertTypeNotFromInfrastructure(method.ReturnType, targetType.FullName!, $"method '{method.Name}' return type");
            foreach (var parameter in method.GetParameters())
            {
                AssertTypeNotFromInfrastructure(parameter.ParameterType, targetType.FullName!, $"method '{method.Name}' parameter '{parameter.Name}'");
            }
        }
    }

    private static void AssertTypeNotFromInfrastructure(Type type, string targetName, string memberDesc)
    {
        if (type.HasElementType)
        {
            var elementType = type.GetElementType();
            if (elementType != null)
            {
                AssertTypeNotFromInfrastructure(elementType, targetName, memberDesc);
            }
        }

        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                AssertTypeNotFromInfrastructure(arg, targetName, memberDesc);
            }
        }

        Assert.False(
            type.Assembly == InfrastructureAssembly ||
            (type.Namespace?.StartsWith("AOS.Messenger.Infrastructure", StringComparison.OrdinalIgnoreCase) == true),
            $"Type '{targetName}' {memberDesc} has forbidden dependency on Infrastructure type '{type.FullName}'."
        );
    }

    private static void AssertTypeHierarchyAndMembersDoNotReference(Type targetType, Type bannedType)
    {
        if (targetType.BaseType != null && targetType.BaseType != typeof(object))
        {
            AssertTypeNotMatching(targetType.BaseType, targetType.FullName!, "base class", bannedType);
        }

        foreach (var iface in targetType.GetInterfaces())
        {
            AssertTypeNotMatching(iface, targetType.FullName!, "interface", bannedType);
        }

        foreach (var constructor in targetType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                AssertTypeNotMatching(parameter.ParameterType, targetType.FullName!, $"constructor parameter '{parameter.Name}'", bannedType);
            }
        }

        foreach (var field in targetType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            AssertTypeNotMatching(field.FieldType, targetType.FullName!, $"field '{field.Name}'", bannedType);
        }

        foreach (var prop in targetType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            AssertTypeNotMatching(prop.PropertyType, targetType.FullName!, $"property '{prop.Name}'", bannedType);
        }

        foreach (var method in targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            AssertTypeNotMatching(method.ReturnType, targetType.FullName!, $"method '{method.Name}' return type", bannedType);
            foreach (var parameter in method.GetParameters())
            {
                AssertTypeNotMatching(parameter.ParameterType, targetType.FullName!, $"method '{method.Name}' parameter '{parameter.Name}'", bannedType);
            }
        }
    }

    private static void AssertTypeNotMatching(Type type, string targetName, string memberDesc, Type bannedType)
    {
        if (type.HasElementType)
        {
            var elementType = type.GetElementType();
            if (elementType != null)
            {
                AssertTypeNotMatching(elementType, targetName, memberDesc, bannedType);
            }
        }

        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                AssertTypeNotMatching(arg, targetName, memberDesc, bannedType);
            }
        }

        Assert.False(
            type == bannedType || string.Equals(type.FullName, bannedType.FullName, StringComparison.OrdinalIgnoreCase),
            $"Type '{targetName}' {memberDesc} has forbidden dependency on banned type '{bannedType.FullName}'."
        );
    }
}
