using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SafeSharp.Analyzers.Semantics;

/// <summary>
/// Models lifetime outlives constraints along constructor/factory dependency edges.
/// The lifetime rule and constructor selection follow the documented .NET container model:
/// https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview.
/// Factory delegates are inspected only for resolutions from their own provider parameter;
/// a newly created scope is a distinct ownership boundary.
/// </summary>
internal sealed class ServiceLifetimeAnalysis
{
    private const int MaximumVisitedNodes = 4096;
    private const int MaximumPathLength = 64;
    private readonly ContractSymbols symbols;
    private readonly ConcurrentBag<INamedTypeSymbol> attributedTypes = new();
    private readonly ConcurrentBag<Registration> registrations = new();

    public ServiceLifetimeAnalysis(ContractSymbols symbols) => this.symbols = symbols;

    public void AddType(INamedTypeSymbol type)
    {
        if (!type.IsImplicitlyDeclared && symbols.Has(type, "ServiceLifetime")) attributedTypes.Add(type);
    }

    public void AddRegistration(IInvocationOperation invocation, CancellationToken cancellationToken)
    {
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        if (!ContractSymbols.Equal(method.ContainingType, symbols.ServiceCollectionExtensions)) return;
        var lifetime = method.Name switch { "AddTransient" => 0, "AddScoped" => 1, "AddSingleton" => 2, _ => -1 };
        if (lifetime < 0) return;
        INamedTypeSymbol? service = null;
        INamedTypeSymbol? implementation = null;
        var constructsType = false;
        if (method.TypeArguments.Length == 1 || method.TypeArguments.Length == 2)
        {
            service = method.TypeArguments[0] as INamedTypeSymbol;
            implementation = method.TypeArguments[method.TypeArguments.Length - 1] as INamedTypeSymbol;
            constructsType = method.Parameters.Length == 1;
        }
        else if (method.TypeArguments.Length == 0)
        {
            foreach (var argument in invocation.Arguments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ContractSymbols.Unwrap(argument.Value) is not ITypeOfOperation typeOf) continue;
                if (argument.Parameter?.Name == "serviceType") service = typeOf.TypeOperand as INamedTypeSymbol;
                if (argument.Parameter?.Name == "implementationType") implementation = typeOf.TypeOperand as INamedTypeSymbol;
            }
            if (service is not null)
            {
                constructsType = implementation is not null || method.Parameters.Length == 2;
                implementation ??= service;
            }
        }
        if (service is null || implementation is null) return;
        if (service.IsUnboundGenericType) service = service.OriginalDefinition;
        if (implementation.IsUnboundGenericType) implementation = implementation.OriginalDefinition;
        var dependencies = ImmutableArray.CreateBuilder<ITypeSymbol>();
        foreach (var argument in invocation.Arguments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = ContractSymbols.Unwrap(argument.Value);
            if (value is IDelegateCreationOperation creation) value = ContractSymbols.Unwrap(creation.Target);
            if (value is not IAnonymousFunctionOperation factory) continue;
            constructsType = false;
            foreach (var operation in ContractSymbols.Walk(factory.Body))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (operation is not IInvocationOperation resolution) continue;
                var target = resolution.TargetMethod.ReducedFrom ?? resolution.TargetMethod;
                if (!ContractSymbols.Equal(target.ContainingType, symbols.ServiceProviderExtensions)
                    || (target.Name != "GetRequiredService" && target.Name != "GetService" && target.Name != "GetServices")) continue;
                var receiver = resolution.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value ?? resolution.Instance;
                if (receiver is null || ContractSymbols.Unwrap(receiver) is not IParameterReferenceOperation provider
                    || !factory.Symbol.Parameters.Any(p => ContractSymbols.Equal(p, provider.Parameter))) continue;
                if (target.TypeArguments.Length == 1)
                {
                    var dependency = target.TypeArguments[0];
                    if (target.Name == "GetServices" && symbols.Enumerable is not null) dependency = symbols.Enumerable.Construct(dependency);
                    dependencies.Add(dependency);
                }
                else
                    foreach (var resolvedArgument in resolution.Arguments)
                        if (ContractSymbols.Unwrap(resolvedArgument.Value) is ITypeOfOperation requestedType) dependencies.Add(requestedType.TypeOperand);
            }
        }
        registrations.Add(new Registration(service, implementation, lifetime, invocation.Syntax.GetLocation(), constructsType, dependencies.ToImmutable()));
    }

    public void Analyze(CompilationAnalysisContext context)
    {
        var explicitRegistrations = registrations.ToArray();
        var all = new List<Registration>(explicitRegistrations);
        var explicitTypes = new HashSet<ITypeSymbol>(explicitRegistrations.Select(r => (ITypeSymbol)r.Service), SymbolEqualityComparer.Default);
        foreach (var type in attributedTypes)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (explicitTypes.Contains(type)) continue;
            var attribute = symbols.Find(type.GetAttributes(), "ServiceLifetime");
            if (attribute is null || attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not int lifetime || lifetime < 0 || lifetime > 2) continue;
            var location = type.Locations.FirstOrDefault(l => l.IsInSource);
            if (location is not null) all.Add(new Registration(type, type, lifetime, location, true, ImmutableArray<ITypeSymbol>.Empty));
        }
        all.Sort(CompareRegistrations);
        var byService = new Dictionary<ITypeSymbol, List<Registration>>(SymbolEqualityComparer.Default);
        foreach (var registration in all)
        {
            if (!byService.TryGetValue(registration.Service, out var group)) byService.Add(registration.Service, group = new List<Registration>());
            group.Add(registration);
        }
        var diagnosedRoots = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var incompleteRoots = new Dictionary<ITypeSymbol, Registration>(SymbolEqualityComparer.Default);
        foreach (var root in all)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (root.Lifetime != 2 || diagnosedRoots.Contains(root.Implementation)) continue;
            var path = FindScopedPath(root, byService, context.CancellationToken, out var exceededBudget);
            if (path is not null)
            {
                diagnosedRoots.Add(root.Implementation);
                var labels = path.Select(n => n.Implementation.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
                var related = path.Skip(1).Select(n => n.Location).Where(l => l.IsInSource).Distinct();
                var properties = ImmutableDictionary<string, string?>.Empty
                    .Add("SafeSharp.DependencyPath", string.Join(" -> ", labels))
                    .Add("SafeSharp.DependencyServices", string.Join(" -> ", path.Select(n => n.Service.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))))
                    .Add("SafeSharp.DependencyLifetimes", string.Join(" -> ", path.Select(n => n.Lifetime switch { 0 => "Transient", 1 => "Scoped", _ => "Singleton" })));
                context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.CaptiveScopedService, root.Location, related, properties,
                    root.Implementation.Name, string.Join(" -> ", labels)));
            }
            else if (exceededBudget)
                incompleteRoots[root.Implementation] = root;
        }
        var incomplete = incompleteRoots.Values.Where(r => !diagnosedRoots.Contains(r.Implementation)).ToList();
        incomplete.Sort(CompareRegistrations);
        foreach (var root in incomplete)
            context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.IncompleteServiceAnalysis, root.Location, root.Implementation.Name));
    }

    private List<Registration>? FindScopedPath(Registration root, Dictionary<ITypeSymbol, List<Registration>> byService, CancellationToken cancellationToken, out bool exceededBudget)
    {
        exceededBudget = false;
        var queue = new Queue<PathNode>();
        var visited = new Dictionary<ITypeSymbol, HashSet<Registration>>(SymbolEqualityComparer.Default);
        var visitedCount = 0;
        queue.Enqueue(new PathNode(root, null, 1));
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            if (current.Previous is not null && current.Registration.Lifetime == 1)
            {
                var path = new List<Registration>();
                for (PathNode? item = current; item is not null; item = item.Previous) path.Add(item.Registration);
                path.Reverse();
                return path;
            }
            if (!visited.TryGetValue(current.Registration.Implementation, out var visitedDefinitions))
                visited.Add(current.Registration.Implementation, visitedDefinitions = new HashSet<Registration>());
            if (!visitedDefinitions.Add(current.Registration.Definition)) continue;
            visitedCount++;
            if (visitedCount > MaximumVisitedNodes || current.Depth >= MaximumPathLength)
            {
                exceededBudget = true;
                continue;
            }
            foreach (var dependency in Dependencies(current.Registration, byService, cancellationToken))
                foreach (var target in Resolve(dependency, byService))
                    queue.Enqueue(new PathNode(target, current, current.Depth + 1));
        }
        return null;
    }

    private IEnumerable<ITypeSymbol> Dependencies(Registration registration, Dictionary<ITypeSymbol, List<Registration>> byService, CancellationToken cancellationToken)
    {
        if (!registration.ConstructsType) return registration.FactoryDependencies;
        var candidates = new List<IMethodSymbol>();
        var maximum = -1;
        foreach (var constructor in registration.Implementation.InstanceConstructors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (constructor.DeclaredAccessibility != Accessibility.Public) continue;
            if (!constructor.Parameters.All(p => p.HasExplicitDefaultValue || IsResolvable(p.Type, byService))) continue;
            if (constructor.Parameters.Length > maximum)
            {
                candidates.Clear();
                maximum = constructor.Parameters.Length;
            }
            if (constructor.Parameters.Length == maximum) candidates.Add(constructor);
        }
        return candidates.SelectMany(c => c.Parameters).Select(p => p.Type).Distinct<ITypeSymbol>(SymbolEqualityComparer.Default);
    }

    private bool IsResolvable(ITypeSymbol type, Dictionary<ITypeSymbol, List<Registration>> byService)
    {
        if (type is INamedTypeSymbol named && ContractSymbols.Equal(named.OriginalDefinition, symbols.Enumerable)) return true;
        var metadataName = type.ToDisplayString();
        if (metadataName == "System.IServiceProvider"
            || metadataName == "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"
            || metadataName == "Microsoft.Extensions.DependencyInjection.IServiceProviderIsService"
            || metadataName == "Microsoft.Extensions.DependencyInjection.IServiceProviderIsKeyedService") return true;
        return Resolve(type, byService).Any();
    }

    private IEnumerable<Registration> Resolve(ITypeSymbol service, Dictionary<ITypeSymbol, List<Registration>> byService)
    {
        if (service is INamedTypeSymbol enumerable && ContractSymbols.Equal(enumerable.OriginalDefinition, symbols.Enumerable)) service = enumerable.TypeArguments[0];
        if (byService.TryGetValue(service, out var exact))
        {
            foreach (var registration in exact) yield return registration;
            yield break;
        }
        if (service is not INamedTypeSymbol named || !named.IsGenericType || !byService.TryGetValue(named.OriginalDefinition, out var open)) yield break;
        foreach (var registration in open)
        {
            var implementation = registration.Implementation.OriginalDefinition;
            if (implementation.Arity != named.TypeArguments.Length) continue;
            yield return new Registration(named, implementation.Construct(named.TypeArguments.ToArray()), registration.Lifetime,
                registration.Location, registration.ConstructsType, registration.FactoryDependencies, registration.Definition);
        }
    }

    private static int CompareRegistrations(Registration left, Registration right)
    {
        var result = StringComparer.Ordinal.Compare(left.Location.SourceTree?.FilePath, right.Location.SourceTree?.FilePath);
        if (result == 0) result = left.Location.SourceSpan.Start.CompareTo(right.Location.SourceSpan.Start);
        if (result == 0) result = StringComparer.Ordinal.Compare(left.Service.ToDisplayString(), right.Service.ToDisplayString());
        return result;
    }

    private sealed class Registration
    {
        public Registration(INamedTypeSymbol service, INamedTypeSymbol implementation, int lifetime, Location location, bool constructsType, ImmutableArray<ITypeSymbol> factoryDependencies, Registration? definition = null)
        { Service = service; Implementation = implementation; Lifetime = lifetime; Location = location; ConstructsType = constructsType; FactoryDependencies = factoryDependencies; Definition = definition ?? this; }
        public INamedTypeSymbol Service { get; }
        public INamedTypeSymbol Implementation { get; }
        public int Lifetime { get; }
        public Location Location { get; }
        public bool ConstructsType { get; }
        public ImmutableArray<ITypeSymbol> FactoryDependencies { get; }
        public Registration Definition { get; }
    }

    private sealed class PathNode
    {
        public PathNode(Registration registration, PathNode? previous, int depth) { Registration = registration; Previous = previous; Depth = depth; }
        public Registration Registration { get; }
        public PathNode? Previous { get; }
        public int Depth { get; }
    }
}
