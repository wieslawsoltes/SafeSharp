using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SafeSharp.Analyzers.Semantics;

internal enum ConcurrencyCapability { Send, Sync }

internal sealed class ConcurrencyCheck
{
    public static readonly ConcurrencyCheck Success = new(null, "", null);
    public ConcurrencyCheck(string? reason, string path, ISymbol? evidence, ImmutableArray<ISymbol> evidencePath = default)
    {
        Reason = reason; Path = path; Evidence = evidence;
        EvidencePath = evidencePath.IsDefault ? evidence is null ? ImmutableArray<ISymbol>.Empty : ImmutableArray.Create(evidence) : evidencePath;
    }
    public bool Satisfied => Reason is null;
    public string? Reason { get; }
    public string Path { get; }
    public ISymbol? Evidence { get; }
    public ImmutableArray<ISymbol> EvidencePath { get; }
    public ConcurrencyCheck Through(ISymbol symbol) => Satisfied ? this : new(Reason,
        symbol.Name + (Path.Length == 0 ? "" : " -> " + Path), Evidence ?? symbol, EvidencePath.Insert(0, symbol));
}

/// <summary>Symbol-identity contracts, deliberately separate from Roslyn-independent ownership dataflow.</summary>
internal sealed class ConcurrencyContracts
{
    private readonly Dictionary<string, INamedTypeSymbol?> attributes = new(StringComparer.Ordinal);
    private readonly HashSet<ITypeSymbol> immutableValues = new(SymbolEqualityComparer.Default);
    public ConcurrencyContracts(Compilation compilation)
    {
        Compilation = compilation;
        foreach (var name in new[] { "Send", "Sync", "TransfersToThread", "SharesAcrossThreads", "ThreadAffine", "ThreadSafe", "Owned" })
            attributes[name] = compilation.GetTypeByMetadataName("SafeSharp." + name + "Attribute");
        var core = compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly;
        foreach (var name in new[] { "System.DateTime", "System.DateTimeOffset", "System.TimeSpan", "System.Guid" })
            if (compilation.GetTypeByMetadataName(name) is { } type && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, core))
                immutableValues.Add(type);
    }
    public Compilation Compilation { get; }
    public bool Available => attributes["Send"] is not null || attributes["Sync"] is not null;
    public bool Has(ISymbol symbol, string name) => attributes.TryGetValue(name, out var attribute) && attribute is not null &&
        symbol.GetAttributes().Any(value => SymbolEqualityComparer.Default.Equals(value.AttributeClass, attribute));
    public bool HasCapability(ITypeSymbol type, ConcurrencyCapability capability) => HasInHierarchy(type, capability.ToString());
    public bool Requires(ITypeParameterSymbol parameter, ConcurrencyCapability capability) => HasCapability(parameter, capability) ||
        capability == ConcurrencyCapability.Sync && HasInHierarchy(parameter, "ThreadSafe");
    public bool HasExplicitConcurrency(ITypeSymbol type) => HasCapability(type, ConcurrencyCapability.Send) || HasCapability(type, ConcurrencyCapability.Sync) || HasInHierarchy(type, "ThreadSafe");
    public bool HasInHierarchy(ITypeSymbol type, string name)
    {
        var pending = new Stack<ITypeSymbol>(); var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default); pending.Push(type);
        while (pending.Count != 0)
        {
            var item = pending.Pop(); if (!seen.Add(item)) continue;
            if (Has(item, name)) return true;
            if (item is ITypeParameterSymbol parameter)
            {
                foreach (var constraint in parameter.ConstraintTypes) pending.Push(constraint);
                if (parameter.ContainingSymbol is IMethodSymbol method)
                    foreach (var inherited in InheritedMethods(method))
                        if (parameter.Ordinal < inherited.TypeParameters.Length) pending.Push(inherited.TypeParameters[parameter.Ordinal]);
            }
            if (item is INamedTypeSymbol named)
            {
                if (named.BaseType is { } baseType) pending.Push(baseType);
                foreach (var contract in named.Interfaces) pending.Push(contract);
            }
        }
        return false;
    }
    public bool HasBoundary(IParameterSymbol parameter, string name)
    {
        if (Has(parameter, name)) return true;
        if (parameter.ContainingSymbol is not IMethodSymbol method) return false;
        foreach (var inherited in InheritedMethods(method))
            if (parameter.Ordinal < inherited.Parameters.Length && Has(inherited.Parameters[parameter.Ordinal], name)) return true;
        return false;
    }
    private static IEnumerable<IMethodSymbol> InheritedMethods(IMethodSymbol method)
    {
        for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod) yield return overridden;
        foreach (var contract in method.ContainingType.AllInterfaces)
        foreach (var member in contract.GetMembers().OfType<IMethodSymbol>())
            if (method.ContainingType.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation &&
                SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, method.OriginalDefinition)) yield return member;
    }
    public bool IsKnownImmutable(ITypeSymbol type) => type.TypeKind == TypeKind.Enum || immutableValues.Contains(type) || type.SpecialType is
        SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
        SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Char or
        SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal or SpecialType.System_String;
    public bool IsTaskRun(IMethodSymbol method) => method.Name == "Run" && method.IsStatic &&
        SymbolEqualityComparer.Default.Equals(method.ContainingType, Compilation.GetTypeByMetadataName("System.Threading.Tasks.Task"));
    public static int MaximumSteps(AnalyzerConfigOptionsProvider provider, SyntaxTree tree)
    {
        if ((provider.GetOptions(tree).TryGetValue("safesharp_max_contract_steps", out var value) || provider.GlobalOptions.TryGetValue("safesharp_max_contract_steps", out value)) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0) return Math.Min(parsed, 1000000);
        return 20000;
    }
}

/// <summary>
/// Validates explicit structural contracts. Rust's Send and Sync inspire distinct transfer and
/// sharing capabilities, but C# does not enforce Rust's reference exclusivity or auto traits:
/// https://doc.rust-lang.org/nomicon/send-and-sync.html.
/// </summary>
internal sealed class ConcurrencyTypeAnalysis
{
    private readonly ConcurrencyContracts contracts;
    private readonly CancellationToken cancellationToken;
    private readonly HashSet<ITypeSymbol> sending = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ITypeSymbol> sharing = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ITypeSymbol, ConcurrencyCheck> sendFailures = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ITypeSymbol, ConcurrencyCheck> syncFailures = new(SymbolEqualityComparer.Default);
    private int remainingSteps;
    public ConcurrencyTypeAnalysis(ConcurrencyContracts contracts, int maximumSteps, CancellationToken cancellationToken)
    { this.contracts = contracts; remainingSteps = maximumSteps; this.cancellationToken = cancellationToken; }
    public bool BudgetExceeded { get; private set; }
    public void ExhaustBudget() { BudgetExceeded = true; remainingSteps = -1; }
    public bool Step()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (--remainingSteps >= 0) return true;
        BudgetExceeded = true; return false;
    }
    public ConcurrencyCheck Check(ITypeSymbol type, ConcurrencyCapability capability)
    {
        if (!Step()) return Failure(type, "the structural analysis budget was exhausted");
        if (contracts.HasInHierarchy(type, "ThreadAffine")) return Failure(type, "a ThreadAffine value cannot cross this thread boundary");
        if (contracts.IsKnownImmutable(type)) return ConcurrencyCheck.Success;
        if (type.TypeKind is TypeKind.Dynamic or TypeKind.Pointer or TypeKind.FunctionPointer || type is INamedTypeSymbol { IsRefLikeType: true })
            return Failure(type, "dynamic, native pointer, and ref-like values have no supported concurrency proof");
        if (type is IArrayTypeSymbol array)
            return capability == ConcurrencyCapability.Send ? Check(array.ElementType, capability)
                : Failure(type, "array elements remain mutable through shared references");
        if (type is ITypeParameterSymbol)
            return contracts.HasCapability(type, capability) || capability == ConcurrencyCapability.Sync && contracts.HasInHierarchy(type, "ThreadSafe")
                ? ConcurrencyCheck.Success : Failure(type, "the generic parameter lacks a " + capability + " contract or constraint");
        if (type is not INamedTypeSymbol named) return Failure(type, "the type has no supported concurrency contract");
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return Check(named.TypeArguments[0], capability);
        if (named.IsTupleType)
        {
            foreach (var element in named.TupleElements)
            { var check = Check(element.Type, capability); if (!check.Satisfied) return check.Through(element); }
            return ConcurrencyCheck.Success;
        }
        var synchronized = contracts.HasInHierarchy(named, "ThreadSafe");
        if (!contracts.HasCapability(named, capability) && !(capability == ConcurrencyCapability.Sync && synchronized))
            return Failure(type, "the type has no explicit " + capability + " contract");
        var failures = capability == ConcurrencyCapability.Send ? sendFailures : syncFailures;
        if (failures.TryGetValue(type, out var prior)) return prior;
        var active = capability == ConcurrencyCapability.Send ? sending : sharing;
        if (active.Count >= 128) { BudgetExceeded = true; return Failure(type, "the recursive type depth budget was exhausted"); }
        if (!active.Add(type)) return ConcurrencyCheck.Success;
        try
        {
            var result = CheckShape(named, capability, synchronized);
            // Do not cache coinductive successes: a recursive dependency may still fail.
            if (!result.Satisfied) failures[type] = result;
            return result;
        }
        finally { active.Remove(type); }
    }
    private ConcurrencyCheck CheckShape(INamedTypeSymbol type, ConcurrencyCapability capability, bool synchronized)
    {
        for (var index = 0; index < type.TypeParameters.Length; index++)
        foreach (var requirement in new[] { ConcurrencyCapability.Send, ConcurrencyCapability.Sync })
        {
            if (!contracts.Requires(type.TypeParameters[index], requirement)) continue;
            var argument = Check(type.TypeArguments[index], requirement);
            if (!argument.Satisfied) return argument.Through(type.TypeParameters[index]);
        }
        if (type.TypeKind == TypeKind.Interface || type.DeclaringSyntaxReferences.IsEmpty) return ConcurrencyCheck.Success;
        for (INamedTypeSymbol? current = type; current is not null && current.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum); current = current.BaseType)
        {
            if (!Step()) return Failure(type, "the structural analysis budget was exhausted");
            if (current.DeclaringSyntaxReferences.IsEmpty)
            {
                if (!contracts.HasCapability(current, capability) && !(capability == ConcurrencyCapability.Sync && contracts.HasInHierarchy(current, "ThreadSafe")))
                    return Failure(current, "an external base type has no explicit " + capability + " contract");
                continue;
            }
            foreach (var field in current.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsStatic).OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                if (!Step()) return Failure(type, "the structural analysis budget was exhausted");
                var member = field.AssociatedSymbol ?? field;
                var affinity = FindAffinity(field.Type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
                if (affinity is not null) return Failure(affinity, "a contained ThreadAffine value cannot satisfy this contract").Through(member);
                if (capability == ConcurrencyCapability.Sync && synchronized) continue; // Explicitly trusted synchronization.
                var immutableSlot = field.IsReadOnly || field.AssociatedSymbol is IPropertySymbol { SetMethod: { IsInitOnly: true } };
                if (capability == ConcurrencyCapability.Sync && !immutableSlot)
                    return new ConcurrencyCheck("shared instance storage must be readonly or init-only, or have a trusted ThreadSafe contract", member.Name, member);
                var component = Check(field.Type, capability);
                if (!component.Satisfied) return component.Through(member);
                if (capability == ConcurrencyCapability.Send && field.Type.IsReferenceType && !contracts.Has(member, "Owned") && !contracts.Has(field, "Owned") && !contracts.Has(field.Type, "Owned"))
                {
                    var shareable = Check(field.Type, ConcurrencyCapability.Sync);
                    if (!shareable.Satisfied) return new ConcurrencyCheck("a mutable reference component requires an Owned field or a Sync component for exclusive transfer", member.Name, member);
                }
            }
        }
        return ConcurrencyCheck.Success;
    }
    private ITypeSymbol? FindAffinity(ITypeSymbol type, HashSet<ITypeSymbol> visited)
    {
        if (!Step() || !visited.Add(type)) return null;
        if (visited.Count > 128) { BudgetExceeded = true; return null; }
        if (contracts.HasInHierarchy(type, "ThreadAffine")) return type;
        if (type is IArrayTypeSymbol array) return FindAffinity(array.ElementType, visited);
        if (type is ITypeParameterSymbol parameter)
        {
            foreach (var constraint in parameter.ConstraintTypes)
                if (FindAffinity(constraint, visited) is { } affineConstraint) return affineConstraint;
            return null;
        }
        if (type is not INamedTypeSymbol named) return null;
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return FindAffinity(named.TypeArguments[0], visited);
        if (named.IsTupleType)
        {
            foreach (var element in named.TupleElements)
                if (FindAffinity(element.Type, visited) is { } affineElement) return affineElement;
            return null;
        }
        if (named.DeclaringSyntaxReferences.IsEmpty) return null;
        foreach (var field in named.GetMembers().OfType<IFieldSymbol>().Where(field => !field.IsStatic))
            if (FindAffinity(field.Type, visited) is { } affine) return affine;
        return named.BaseType is { } baseType ? FindAffinity(baseType, visited) : null;
    }
    private static ConcurrencyCheck Failure(ITypeSymbol type, string reason) => new(reason, type.ToDisplayString(), type);
}
