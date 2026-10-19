using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using SafeSharp.Analyzers.Analysis;
using SafeSharp.Core;
using SafeSharp.Core.Regions;

namespace SafeSharp.Analyzers.Semantics;

internal sealed class LifetimeContractModel
{
    private readonly ConcurrentDictionary<(int Regions, int Constraints, int Steps), RegionSolution> solutions = new();
    internal LifetimeContractModel(IMethodSymbol method, ImmutableArray<string?> parameters, string? returned,
        ImmutableArray<RegionConstraint> constraints, ImmutableArray<LifetimeDeclarationIssue> issues,
        ImmutableArray<Location> locations, string? borrowedSource, bool mutableBorrow, bool returnsOwned)
    {
        Method = method;
        ParameterRegions = parameters;
        ReturnRegion = returned;
        Constraints = constraints;
        DeclarationIssues = issues;
        DeclarationLocations = locations;
        BorrowedSource = borrowedSource;
        MutableBorrow = mutableBorrow;
        ReturnsOwned = returnsOwned;
        var names = parameters.Where(name => name is not null).Select(name => name!).ToList();
        if (returned is not null) names.Add(returned);
        if (HasReceiver(method)) names.Add("this");
        RegionNames = names.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToImmutableArray();
    }

    internal IMethodSymbol Method { get; }
    internal ImmutableArray<string?> ParameterRegions { get; }
    internal string? ReturnRegion { get; }
    internal ImmutableArray<string> RegionNames { get; }
    internal ImmutableArray<RegionConstraint> Constraints { get; }
    internal ImmutableArray<LifetimeDeclarationIssue> DeclarationIssues { get; }
    internal ImmutableArray<Location> DeclarationLocations { get; }
    internal string? BorrowedSource { get; }
    internal bool MutableBorrow { get; }
    internal bool ReturnsOwned { get; }
    internal bool HasRegions => ReturnRegion is not null || ParameterRegions.Any(name => name is not null) || !Constraints.IsEmpty;
    internal static bool HasReceiver(IMethodSymbol method) => !method.IsStatic && method.MethodKind != MethodKind.DelegateInvoke;

    internal RegionSolution Solve(RegionSolverOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return solutions.GetOrAdd((options.MaxRegions, options.MaxConstraints, options.MaxSteps), _ =>
            new RegionConstraintSolver().Solve(new RegionProblem(RegionNames,
                Constraints.Where(constraint => RegionNames.Contains(constraint.Longer, StringComparer.Ordinal) && RegionNames.Contains(constraint.Shorter, StringComparer.Ordinal))), options, cancellationToken));
    }
}

internal sealed class LifetimeDeclarationIssue
{
    internal LifetimeDeclarationIssue(Location location, string message) { Location = location; Message = message; }
    internal Location Location { get; }
    internal string Message { get; }
}

/// <summary>Compilation-local contract resolution. Parameter and region renaming is positional, never text-only.</summary>
internal sealed class LifetimeContractProvider
{
    private readonly INamedTypeSymbol? lifetimeAttribute;
    private readonly INamedTypeSymbol? outlivesAttribute;
    private readonly ConcurrentDictionary<IMethodSymbol, LifetimeContractModel> cache = new(SymbolEqualityComparer.Default);

    internal LifetimeContractProvider(Compilation compilation)
    {
        lifetimeAttribute = compilation.GetTypeByMetadataName("SafeSharp.LifetimeAttribute");
        outlivesAttribute = compilation.GetTypeByMetadataName("SafeSharp.OutlivesAttribute");
    }

    internal bool Available => lifetimeAttribute is not null && outlivesAttribute is not null;

    internal LifetimeContractModel Get(IMethodSymbol method, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return cache.GetOrAdd(method.OriginalDefinition, symbol => Build(symbol, inherit: true, cancellationToken));
    }

    internal LifetimeContractModel GetDeclared(IMethodSymbol method, CancellationToken cancellationToken) =>
        Build(method, inherit: false, cancellationToken);

    internal IEnumerable<IMethodSymbol> Bases(IMethodSymbol method)
    {
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { method };
        // Inheritance follows the virtual slot, not alphabetical type names. A nearer override
        // can legally weaken an input requirement established by a more distant ancestor.
        for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
            if (seen.Add(overridden)) yield return overridden;
        var sources = SymbolFacts.ContractSources(method).Select(source => source switch
        {
            IMethodSymbol declaration => declaration,
            IPropertySymbol property when method.MethodKind == MethodKind.PropertyGet => property.GetMethod,
            IPropertySymbol property when method.MethodKind == MethodKind.PropertySet => property.SetMethod,
            _ => null,
        }).OfType<IMethodSymbol>().OrderBy(source => source.ToDisplayString(), StringComparer.Ordinal);
        foreach (var source in sources)
            if (seen.Add(source)) yield return source;
    }

    internal AttributeData? Lifetime(ISymbol symbol) => symbol.GetAttributes().FirstOrDefault(attribute => Is(attribute, lifetimeAttribute));
    internal AttributeData? ReturnLifetime(IMethodSymbol method) => method.GetReturnTypeAttributes().FirstOrDefault(attribute => Is(attribute, lifetimeAttribute));
    internal IEnumerable<AttributeData> Outlives(IMethodSymbol method) =>
        (method.MethodKind == MethodKind.DelegateInvoke ? method.ContainingType.GetAttributes() : method.GetAttributes())
        .Where(attribute => Is(attribute, outlivesAttribute));

    internal static RegionSolverOptions Options(AnalyzerConfigOptions options)
    {
        static int Read(AnalyzerConfigOptions values, string key, int fallback, int maximum) =>
            values.TryGetValue(key, out var text) && int.TryParse(text, out var parsed) && parsed > 0 ? Math.Min(parsed, maximum) : fallback;
        return new RegionSolverOptions(Read(options, "safesharp_max_regions", 256, 4096),
            Read(options, "safesharp_max_region_constraints", 4096, 100000), Read(options, "safesharp_max_region_steps", 100000, 10000000));
    }

    private LifetimeContractModel Build(IMethodSymbol method, bool inherit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inherited = inherit ? Bases(method).ToArray() : Array.Empty<IMethodSymbol>();
        var parameters = ImmutableArray.CreateBuilder<string?>(method.Parameters.Length);
        var issues = ImmutableArray.CreateBuilder<LifetimeDeclarationIssue>();
        var locations = ImmutableArray.CreateBuilder<Location>();
        var direct = method.Parameters.Select(parameter => Lifetime(parameter)).ToArray();
        var directReturn = ReturnLifetime(method);
        var parameterAttributes = new AttributeData?[method.Parameters.Length];
        var parameterSources = new IMethodSymbol[method.Parameters.Length];
        for (var index = 0; index < method.Parameters.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            parameterAttributes[index] = direct[index];
            parameterSources[index] = method;
            if (direct[index] is not null) continue;
            foreach (var declaration in inherited)
                if (index < declaration.Parameters.Length && Lifetime(declaration.Parameters[index]) is { } candidate)
                { parameterAttributes[index] = candidate; parameterSources[index] = declaration; break; }
        }
        var translations = new Dictionary<IMethodSymbol, Dictionary<string, string>>(SymbolEqualityComparer.Default);

        string? Translate(string? name, IMethodSymbol declaration)
        {
            if (name is null || SymbolEqualityComparer.Default.Equals(declaration, method)) return name;
            if (!translations.TryGetValue(declaration, out var renamedRegions))
            {
                renamedRegions = new Dictionary<string, string>(StringComparer.Ordinal);
                var inputNames = new HashSet<string>(StringComparer.Ordinal);
                var inheritedContract = Get(declaration, cancellationToken);
                for (var index = 0; index < Math.Min(declaration.Parameters.Length, method.Parameters.Length); index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (inheritedContract.ParameterRegions[index] is not { } original) continue;
                    inputNames.Add(original);
                    if (original != "this" && !renamedRegions.ContainsKey(original) && Name(parameterAttributes[index]) is { } renamed)
                        renamedRegions.Add(original, renamed);
                }
                if (inheritedContract.ReturnRegion is { } originalReturn && originalReturn != "this" && !inputNames.Contains(originalReturn) && Name(directReturn) is { } returnName)
                    renamedRegions[originalReturn] = returnName;
                translations.Add(declaration, renamedRegions);
            }
            return renamedRegions.TryGetValue(name, out var replacement) ? replacement : name;
        }

        for (var index = 0; index < method.Parameters.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attribute = parameterAttributes[index];
            var source = parameterSources[index];
            var name = Translate(Name(attribute), source);
            parameters.Add(name);
            if (attribute is not null)
            {
                var location = Site(attribute, method.Parameters[index], cancellationToken);
                locations.Add(location);
                ValidateName(name, location, method, issues);
                if (new[] { method }.Concat(inherited).Any(declaration => index < declaration.Parameters.Length &&
                    (SymbolFacts.DirectAttribute(declaration.Parameters[index], "Owned") is not null || SymbolFacts.DirectAttribute(declaration.Parameters[index], "Consumes") is not null)))
                    issues.Add(new LifetimeDeclarationIssue(location, "a lifetime-bound parameter cannot also transfer ownership"));
            }
        }

        var returnAttribute = directReturn;
        var returnSource = method;
        if (returnAttribute is null)
            foreach (var declaration in inherited)
                if (ReturnLifetime(declaration) is { } candidate) { returnAttribute = candidate; returnSource = declaration; break; }
        var returned = Translate(Name(returnAttribute), returnSource);
        if (returnAttribute is not null)
        {
            var location = Site(returnAttribute, method, cancellationToken);
            locations.Add(location);
            ValidateName(returned, location, method, issues);
            if (method.ReturnsVoid) issues.Add(new LifetimeDeclarationIssue(location, "a void method cannot promise a returned lifetime"));
        }

        var returnsOwned = SymbolFacts.HasAttribute(method, "ReturnsOwned") || SymbolFacts.HasReturnAttribute(method, "Owned");
        if (returned is not null && returnsOwned)
            issues.Add(new LifetimeDeclarationIssue(Site(returnAttribute, method, cancellationToken), "a borrowed return lifetime conflicts with an owned return contract"));

        var names = new HashSet<string>(parameters.Where(name => name is not null).Select(name => name!), StringComparer.Ordinal);
        if (returned is not null) names.Add(returned);
        if (LifetimeContractModel.HasReceiver(method)) names.Add("this");
        var boundsSource = method;
        var bounds = Outlives(method).ToArray();
        if (bounds.Length == 0)
            foreach (var declaration in inherited)
            {
                var candidates = Outlives(declaration).ToArray();
                if (candidates.Length != 0) { bounds = candidates; boundsSource = declaration; break; }
            }
        var constraints = ImmutableArray.CreateBuilder<RegionConstraint>();
        foreach (var attribute in bounds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var longer = Translate(Argument(attribute, 0), boundsSource);
            var shorter = Translate(Argument(attribute, 1), boundsSource);
            var location = Site(attribute, method, cancellationToken);
            locations.Add(location);
            if (!ValidName(longer, method) || !ValidName(shorter, method))
            { issues.Add(new LifetimeDeclarationIssue(location, "outlives endpoints must be valid named regions")); continue; }
            if (!names.Contains(longer!) || !names.Contains(shorter!))
            { issues.Add(new LifetimeDeclarationIssue(location, "outlives constraint references a region absent from the signature: " + longer + " : " + shorter)); continue; }
            constraints.Add(new RegionConstraint(longer!, shorter!, Source(location), "Declared " + longer + " : " + shorter));
        }

        var borrowed = SymbolFacts.Attribute(method, "ReturnsBorrowed");
        var borrowedSource = Argument(borrowed, 0);
        if (borrowedSource is not null && borrowedSource != "this")
        {
            var declaration = new[] { method }.Concat(inherited).FirstOrDefault(candidate => SymbolFacts.DirectAttribute(candidate, "ReturnsBorrowed") is not null);
            var ordinal = declaration?.Parameters.FirstOrDefault(parameter => parameter.Name == borrowedSource)?.Ordinal;
            if (ordinal is { } index && index < method.Parameters.Length) borrowedSource = method.Parameters[index].Name;
        }
        var directBorrowedReturn = method.GetReturnTypeAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "SafeSharp.BorrowedAttribute");
        var directMutableReturn = method.GetReturnTypeAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "SafeSharp.BorrowedMutAttribute");
        var directSourceContract = SymbolFacts.DirectAttribute(method, "ReturnsBorrowed") ??
            (method.AssociatedSymbol is { } associated ? SymbolFacts.DirectAttribute(associated, "ReturnsBorrowed") : null);
        if (directBorrowedReturn is not null && directMutableReturn is not null && (returned is not null || borrowed is not null))
            issues.Add(new LifetimeDeclarationIssue(Site(directMutableReturn, method, cancellationToken), "a returned loan cannot be both Borrowed and BorrowedMut"));
        if (directSourceContract is not null && ((directBorrowedReturn is not null) != (directMutableReturn is not null)) &&
            (directMutableReturn is not null) != (directSourceContract.ConstructorArguments.Length > 1 && directSourceContract.ConstructorArguments[1].Value is true))
            issues.Add(new LifetimeDeclarationIssue(Site(directMutableReturn ?? directBorrowedReturn, method, cancellationToken),
                "the return attribute disagrees with the explicit ReturnsBorrowed mutability"));
        var mutable = directMutableReturn is not null || directBorrowedReturn is null &&
            (directSourceContract is not null
                ? directSourceContract.ConstructorArguments.Length > 1 && directSourceContract.ConstructorArguments[1].Value is true
                : borrowed?.ConstructorArguments.Length > 1 && borrowed.ConstructorArguments[1].Value is true || SymbolFacts.HasReturnAttribute(method, "BorrowedMut"));
        return new LifetimeContractModel(method, parameters.ToImmutable(), returned, constraints.ToImmutable(), issues.ToImmutable(),
            locations.Distinct().ToImmutableArray(), borrowedSource, mutable, returnsOwned);
    }

    private static void ValidateName(string? name, Location location, IMethodSymbol method, ImmutableArray<LifetimeDeclarationIssue>.Builder issues)
    {
        if (!ValidName(name, method)) issues.Add(new LifetimeDeclarationIssue(location,
            "lifetime names must be identifiers; the reserved 'this' region is available only on instance methods with an explicit receiver"));
    }

    private static bool ValidName(string? name, IMethodSymbol method) => name is not null &&
        (name == "this" ? LifetimeContractModel.HasReceiver(method) : SyntaxFacts.IsValidIdentifier(name) && !name.StartsWith("@", StringComparison.Ordinal));

    private static bool Is(AttributeData attribute, INamedTypeSymbol? type) => type is not null && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, type);
    internal static string? Name(AttributeData? attribute) => Argument(attribute, 0);
    internal static string? Argument(AttributeData? attribute, int index) => attribute is not null && index < attribute.ConstructorArguments.Length ? attribute.ConstructorArguments[index].Value as string : null;
    internal static Location Site(AttributeData? attribute, ISymbol fallback, CancellationToken cancellationToken) =>
        attribute?.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? fallback.Locations.FirstOrDefault() ?? Location.None;
    internal static SourceSite? Source(Location location)
    {
        if (!location.IsInSource) return null;
        var line = location.GetLineSpan();
        return new SourceSite(line.Path, location.SourceSpan.Start, location.SourceSpan.Length, line.StartLinePosition.Line, line.StartLinePosition.Character);
    }
}
