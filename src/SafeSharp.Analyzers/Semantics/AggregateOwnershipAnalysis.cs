using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SafeSharp.Analyzers.Semantics;

/// <summary>Checks aggregate release obligations at every modeled normal disposal exit.</summary>
internal static class AggregateOwnershipAnalysis
{
    public static void Analyze(SymbolAnalysisContext context, INamedTypeSymbol type, ContractSymbols symbols)
    {
        if (type.IsImplicitlyDeclared || type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return;
        var owned = Hierarchy(type).SelectMany(current => current.GetMembers()).Where(member => !member.IsStatic && !member.IsImplicitlyDeclared &&
            MemberType(member) is { } memberType && (symbols.Has(member, "Owned") || symbols.Has(memberType, "Owned"))).ToArray();
        var typeLocation = type.Locations.FirstOrDefault(location => location.IsInSource);
        if (type.TypeKind == TypeKind.Struct && (owned.Length != 0 || symbols.Has(type, "Owned")) && typeLocation is not null)
            context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.CopyableOwner, typeLocation, type.Name));
        var disposable = owned.Where(member => symbols.IsDisposable(MemberType(member))).Select(member => member.OriginalDefinition)
            .Distinct(SymbolEqualityComparer.Default).ToArray();
        if (disposable.Length == 0) return;
        if (!symbols.IsDisposable(type))
        {
            foreach (var member in disposable)
                if (member.Locations.FirstOrDefault(location => location.IsInSource) is { } location)
                    context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.MissingDisposalContract, location, type.Name, member.Name));
            return;
        }
        // An abstract disposal contract is checked at each concrete implementing owner.
        if (type.IsAbstract) return;
        if (disposable.Length > 512)
        {
            if (typeLocation is not null) context.ReportDiagnostic(Diagnostic.Create(ConcurrencyDiagnostics.IncompleteContractAnalysis,
                typeLocation, type.Name, "owned-member count"));
            return;
        }
        var maximumSteps = 20000;
        var options = typeLocation?.SourceTree is { } tree ? context.Options.AnalyzerConfigOptionsProvider.GetOptions(tree)
            : context.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
        if ((options.TryGetValue("safesharp_max_contract_steps", out var configured) || context.Options.AnalyzerConfigOptionsProvider.GlobalOptions
                .TryGetValue("safesharp_max_contract_steps", out configured)) && int.TryParse(configured, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed) && parsed > 0) maximumSteps = Math.Min(parsed, 1000000);
        var session = new AggregateCleanupSummary(type, disposable, symbols, maximumSteps, context.CancellationToken);
        var entrypoints = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var contract in new[] { symbols.Disposable, symbols.AsyncDisposable })
        {
            if (contract is null) continue;
            foreach (var contractMember in contract.GetMembers().OfType<IMethodSymbol>())
                if (type.FindImplementationForInterfaceMember(contractMember) is IMethodSymbol implementation) entrypoints.Add(session.ResolveOverride(implementation));
        }
        var failures = new Dictionary<ISymbol, List<KeyValuePair<IMethodSymbol, CleanupMethodSummary>>>(SymbolEqualityComparer.Default);
        foreach (var entrypoint in entrypoints.OrderBy(method => method.ToDisplayString(), StringComparer.Ordinal))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var summary = session.Analyze(entrypoint);
            foreach (var member in disposable)
            {
                if (summary.Released.Contains(member) && !summary.OutstandingNewMembers.Contains(member) && !summary.PendingMembers.Contains(member)) continue;
                if (!failures.TryGetValue(member, out var missing)) failures.Add(member, missing = new());
                missing.Add(new KeyValuePair<IMethodSymbol, CleanupMethodSummary>(entrypoint, summary));
            }
        }
        foreach (var failure in failures)
        {
            var location = ContractSymbols.Equal(failure.Key.ContainingType.OriginalDefinition, type.OriginalDefinition)
                ? failure.Key.Locations.FirstOrDefault(candidate => candidate.IsInSource) : typeLocation;
            if (location is null) continue;
            var properties = ImmutableDictionary<string, string?>.Empty
                .Add("SafeSharp.CleanupEntrypoints", string.Join("; ", failure.Value.Select(pair => pair.Key.ToDisplayString())))
                .Add("SafeSharp.CleanupOwner", type.ToDisplayString())
                .Add("SafeSharp.CleanupProof", "normalExitMustRelease");
            context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.OwnedFieldNotDisposed, location,
                additionalLocations: failure.Value.SelectMany(pair => pair.Value.Exits).Concat(failure.Key.Locations.Where(candidate => candidate.IsInSource)).Distinct(), properties: properties,
                messageArgs: new object[] { failure.Key.Name }));
        }
        if (session.BudgetExceeded && typeLocation is not null)
            context.ReportDiagnostic(Diagnostic.Create(ConcurrencyDiagnostics.IncompleteContractAnalysis, typeLocation,
                type.Name, "aggregate cleanup summary"));
    }
    private static IEnumerable<INamedTypeSymbol> Hierarchy(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType) yield return current;
    }
    private static ITypeSymbol? MemberType(ISymbol member) => member switch
    {
        IFieldSymbol field => field.Type,
        IPropertySymbol property when property.GetMethod is not null => property.Type,
        _ => null,
    };
}
