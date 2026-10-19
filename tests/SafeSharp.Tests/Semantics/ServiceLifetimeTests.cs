namespace SafeSharp.Tests.Semantics;

public sealed class ServiceLifetimeTests
{
    private const string Imports = "using System; using SafeSharp; using Microsoft.Extensions.DependencyInjection; ";

    [Fact]
    public Task Attributed_singleton_cannot_capture_scoped_service() => SemanticsAssert.DiagnosticAsync("using SafeSharp; [ServiceLifetime(ServiceLifetime.Scoped)] class Scoped {} [ServiceLifetime(ServiceLifetime.Singleton)] class [|Root|] { public Root(Scoped scoped) {} }", "SS0026");

    [Fact]
    public async Task Transient_bridge_does_not_hide_scoped_lifetime()
    {
        var diagnostics = await SemanticsAssert.AnalyzeAsync("using SafeSharp; [ServiceLifetime(ServiceLifetime.Scoped)] class Scoped {} [ServiceLifetime(ServiceLifetime.Transient)] class Bridge { public Bridge(Scoped scoped) {} } [ServiceLifetime(ServiceLifetime.Singleton)] class Root { public Root(Bridge bridge) {} }");
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SS0026", diagnostic.Id);
        Assert.Equal("Root -> Bridge -> Scoped", diagnostic.Properties["SafeSharp.DependencyPath"]);
        Assert.Equal(2, diagnostic.AdditionalLocations.Count);
    }

    [Fact]
    public Task Scoped_owner_can_capture_scoped_dependency() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; [ServiceLifetime(ServiceLifetime.Scoped)] class Scoped {} [ServiceLifetime(ServiceLifetime.Scoped)] class Root { public Root(Scoped scoped) {} }");

    [Fact]
    public Task Transient_dependency_without_scoped_descendants_is_safe() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; [ServiceLifetime(ServiceLifetime.Transient)] class Transient {} [ServiceLifetime(ServiceLifetime.Singleton)] class Root { public Root(Transient transient) {} }");

    [Fact]
    public Task Unresolvable_constructor_is_not_selected() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; class Unknown {} [ServiceLifetime(ServiceLifetime.Scoped)] class Scoped {} [ServiceLifetime(ServiceLifetime.Singleton)] class Root { public Root() {} public Root(Scoped scoped, Unknown unknown) {} }");

    [Fact]
    public Task Private_constructor_is_not_container_injection() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; [ServiceLifetime(ServiceLifetime.Scoped)] class Scoped {} [ServiceLifetime(ServiceLifetime.Singleton)] class Root { public Root() {} private Root(Scoped scoped) {} }");

    [Fact]
    public Task Constructor_cycles_terminate() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; [ServiceLifetime(ServiceLifetime.Singleton)] class A { public A(B b) {} } [ServiceLifetime(ServiceLifetime.Transient)] class B { public B(A a) {} }");

    [Fact]
    public Task Generic_registration_maps_interface_to_implementation() => SemanticsAssert.DiagnosticAsync(Imports + "interface IDep {} class Dep : IDep {} class Root { public Root(IDep dep) {} } class Setup { void M(IServiceCollection services) { services.AddScoped<IDep, Dep>(); [|services.AddSingleton<Root>()|]; } }" + ContainerStubs, "SS0026");

    [Fact]
    public Task Generic_registration_finds_transient_chain() => SemanticsAssert.DiagnosticAsync(Imports + "class Dep {} class Bridge { public Bridge(Dep dep) {} } class Root { public Root(Bridge bridge) {} } class Setup { void M(IServiceCollection services) { services.AddScoped<Dep>(); services.AddTransient<Bridge>(); [|services.AddSingleton<Root>()|]; } }" + ContainerStubs, "SS0026");

    [Fact]
    public Task Enumerable_injection_retains_scoped_items() => SemanticsAssert.DiagnosticAsync(Imports + "interface IDep {} class Dep : IDep {} class Root { public Root(System.Collections.Generic.IEnumerable<IDep> deps) {} } class Setup { void M(IServiceCollection services) { services.AddScoped<IDep, Dep>(); [|services.AddSingleton<Root>()|]; } }" + ContainerStubs, "SS0026");

    [Fact]
    public Task Typeof_registration_is_recognized() => SemanticsAssert.DiagnosticAsync(Imports + "interface IDep {} class Dep : IDep {} class Root { public Root(IDep dep) {} } class Setup { void M(IServiceCollection services) { services.AddScoped(typeof(IDep), typeof(Dep)); [|services.AddSingleton(typeof(Root))|]; } }" + ContainerStubs, "SS0026");

    [Fact]
    public Task Direct_factory_resolution_is_an_ownership_edge() => SemanticsAssert.DiagnosticAsync(Imports + "class Dep {} class Root { public Root(Dep dep) {} } class Setup { void M(IServiceCollection services) { services.AddScoped<Dep>(); [|services.AddSingleton<Root>(sp => new Root(sp.GetRequiredService<Dep>()))|]; } }" + ContainerStubs, "SS0026");

    [Fact]
    public Task Factory_does_not_imply_constructor_injection() => SemanticsAssert.NoDiagnosticAsync(Imports + "class Dep {} class Root { public Root() {} public Root(Dep dep) {} } class Setup { void M(IServiceCollection services) { services.AddScoped<Dep>(); services.AddSingleton<Root>(sp => new Root()); } }" + ContainerStubs);

    [Fact]
    public Task Alternate_singleton_factory_is_not_hidden_by_implementation_deduplication() => SemanticsAssert.DiagnosticAsync(Imports + "class Dep {} class Root { public Root() {} public Root(Dep dep) {} } class Setup { void M(IServiceCollection services) { services.AddScoped<Dep>(); services.AddSingleton<Root>(sp => new Root()); [|services.AddSingleton<Root>(sp => new Root(sp.GetRequiredService<Dep>()))|]; } }" + ContainerStubs, "SS0026");

    [Fact]
    public Task Alternate_transient_factory_is_not_hidden_by_implementation_deduplication() => SemanticsAssert.DiagnosticAsync(Imports + "class Dep {} class Bridge { public Bridge() {} public Bridge(Dep dep) {} } class Root { public Root(Bridge bridge) {} } class Setup { void M(IServiceCollection services) { services.AddScoped<Dep>(); services.AddTransient<Bridge>(sp => new Bridge()); services.AddTransient<Bridge>(sp => new Bridge(sp.GetRequiredService<Dep>())); [|services.AddSingleton<Root>()|]; } }" + ContainerStubs, "SS0026");

    [Fact]
    public Task Instance_registration_does_not_imply_constructor_injection() => SemanticsAssert.NoDiagnosticAsync(Imports + "class Dep {} class Root { public Root() {} public Root(Dep dep) {} } class Setup { void M(IServiceCollection services) { services.AddScoped<Dep>(); services.AddSingleton(new Root()); } }" + ContainerStubs);

    [Fact]
    public Task Explicit_scope_for_temporary_work_does_not_capture_service() => SemanticsAssert.NoDiagnosticAsync(Imports + "class Dep { public void Work() {} } class Root {} class Setup { void M(IServiceCollection services) { services.AddScoped<Dep>(); services.AddSingleton<Root>(sp => { using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope(); scope.ServiceProvider.GetRequiredService<Dep>().Work(); return new Root(); }); } }" + ContainerStubs);

    [Fact]
    public Task Scope_factory_constructor_dependency_is_a_lifetime_boundary() => SemanticsAssert.NoDiagnosticAsync(Imports + "[SafeSharp.ServiceLifetime(SafeSharp.ServiceLifetime.Singleton)] class Root { public Root(IServiceScopeFactory scopes) {} }" + ContainerStubs);

    [Fact]
    public Task Namesake_registration_extensions_do_not_match() => SemanticsAssert.NoDiagnosticAsync("using Other; class Dep {} class Root { public Root(Dep dep) {} } class Setup { void M(ServiceCollection services) { services.AddScoped<Dep>(); services.AddSingleton<Root>(); } } namespace Other { class ServiceCollection {} static class ServiceCollectionServiceExtensions { public static ServiceCollection AddScoped<T>(this ServiceCollection s) => s; public static ServiceCollection AddSingleton<T>(this ServiceCollection s) => s; } }");

    [Fact]
    public Task Aliased_canonical_registration_extensions_match() => SemanticsAssert.DiagnosticAsync(Imports + "class Dep {} class Root { public Root(Dep dep) {} } class Setup { void M(IServiceCollection services) { ServiceCollectionServiceExtensions.AddScoped<Dep>(services); [|ServiceCollectionServiceExtensions.AddSingleton<Root>(services)|]; } }" + ContainerStubs, "SS0026");

    [Fact]
    public Task Open_generic_registration_substitutes_dependency_arguments() => SemanticsAssert.DiagnosticAsync(Imports + "interface IRepository<T> {} class Repository<T> : IRepository<T> {} class Root { public Root(IRepository<string> repo) {} } class Setup { void M(IServiceCollection services) { services.AddScoped(typeof(IRepository<>), typeof(Repository<>)); [|services.AddSingleton<Root>()|]; } }" + ContainerStubs, "SS0026");

    [Fact]
    public Task Expanding_generic_dependency_graph_reports_incompleteness() => SemanticsAssert.DiagnosticAsync(Imports + "class Node<T> { public Node(Node<System.Collections.Generic.List<T>> next) {} } class Root { public Root(Node<int> node) {} } class Setup { void M(IServiceCollection services) { services.AddTransient(typeof(Node<>), typeof(Node<>)); [|services.AddSingleton<Root>()|]; } }" + ContainerStubs, "SS0029");

    [Fact]
    public async Task Diagnostics_are_deterministic_across_concurrent_runs()
    {
        const string source = "using SafeSharp; [ServiceLifetime(ServiceLifetime.Scoped)] class Scoped {} [ServiceLifetime(ServiceLifetime.Transient)] class Bridge { public Bridge(Scoped scoped) {} } [ServiceLifetime(ServiceLifetime.Singleton)] class Root { public Root(Bridge bridge) {} }";
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SemanticsAssert.AnalyzeAsync(source)));
        var expected = string.Join("\n", runs[0].Select(d => d.ToString()));
        Assert.All(runs, diagnostics => Assert.Equal(expected, string.Join("\n", diagnostics.Select(d => d.ToString()))));
    }

    private const string ContainerStubs = """

namespace Microsoft.Extensions.DependencyInjection
{
    public interface IServiceCollection { }
    public interface IServiceScope : System.IDisposable { System.IServiceProvider ServiceProvider { get; } }
    public interface IServiceScopeFactory { IServiceScope CreateScope(); }
    public static class ServiceCollectionServiceExtensions
    {
        public static IServiceCollection AddSingleton<T>(this IServiceCollection services) where T : class => services;
        public static IServiceCollection AddSingleton<T, TImpl>(this IServiceCollection services) where T : class where TImpl : class, T => services;
        public static IServiceCollection AddSingleton<T>(this IServiceCollection services, T instance) where T : class => services;
        public static IServiceCollection AddSingleton<T>(this IServiceCollection services, System.Func<System.IServiceProvider, T> factory) where T : class => services;
        public static IServiceCollection AddScoped<T>(this IServiceCollection services) where T : class => services;
        public static IServiceCollection AddScoped<T, TImpl>(this IServiceCollection services) where T : class where TImpl : class, T => services;
        public static IServiceCollection AddTransient<T>(this IServiceCollection services) where T : class => services;
        public static IServiceCollection AddTransient<T>(this IServiceCollection services, System.Func<System.IServiceProvider, T> factory) where T : class => services;
        public static IServiceCollection AddSingleton(this IServiceCollection services, System.Type serviceType) => services;
        public static IServiceCollection AddScoped(this IServiceCollection services, System.Type serviceType, System.Type implementationType) => services;
        public static IServiceCollection AddTransient(this IServiceCollection services, System.Type serviceType, System.Type implementationType) => services;
    }
    public static class ServiceProviderServiceExtensions
    {
        public static T GetRequiredService<T>(this System.IServiceProvider provider) where T : class => default!;
    }
}
""";
}
