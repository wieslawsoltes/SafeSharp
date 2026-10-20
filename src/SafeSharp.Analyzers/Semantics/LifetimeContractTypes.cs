using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SafeSharp.Core.Regions;

namespace SafeSharp.Analyzers.Semantics;

internal sealed class LifetimeTypeScope
{
    internal LifetimeTypeScope(string key, string parent, RegionVariance variance)
    { Key = key; Parent = parent; Variance = variance; }
    internal string Key { get; }
    internal string Parent { get; }
    internal RegionVariance Variance { get; }
}

internal sealed class LifetimeTypePosition
{
    internal LifetimeTypePosition(int parameter, string path, string name, string scope, string regionScope,
        RegionVariance variance, ITypeSymbol type, bool input, Location location, ImmutableArray<LifetimeTypeScope> scopes)
    {
        Parameter = parameter; Path = path; Name = name; Scope = scope; RegionScope = regionScope;
        Variance = variance; Type = type; IsInput = input; Location = location; Scopes = scopes;
    }
    internal int Parameter { get; }
    internal string Path { get; }
    internal string Name { get; }
    internal string Scope { get; }
    internal string RegionScope { get; }
    internal string Region => Identity(RegionScope, Name);
    internal RegionVariance Variance { get; }
    internal ITypeSymbol Type { get; }
    internal bool IsInput { get; }
    internal Location Location { get; }
    internal ImmutableArray<LifetimeTypeScope> Scopes { get; }
    internal static string Identity(string scope, string name) => scope.Length == 0 ? name : "$type:" + scope + ":" + name;
}

internal sealed class LifetimeTypeBound
{
    internal LifetimeTypeBound(ITypeSymbol type, string region, string scope, Location location, bool implied)
    { Type = type; Region = region; Scope = scope; Location = location; IsImplied = implied; }
    internal ITypeSymbol Type { get; }
    internal string Region { get; }
    internal string Scope { get; }
    internal Location Location { get; }
    internal bool IsImplied { get; }
}

/// <summary>Bounded parsing of explicit borrowed type paths using Roslyn's resolved type symbols.</summary>
/// <remarks>
/// Generic variance is read from the declared type parameters; callable parameters reverse it,
/// writable storage is invariant, and each callable introduces its own binder. No C# syntax is
/// reparsed and no variance of an arbitrary nominal container is guessed.
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_type_ir/src/relate.rs
/// </remarks>
internal static class LifetimeTypePath
{
    // Attribute parsing happens before per-tree solver options are available. This fixed syntax
    // limit prevents hostile metadata strings from creating unbounded recursive type walks.
    internal const int MaximumSegments = 64;

    internal static LifetimeTypePosition? Parse(IMethodSymbol method, int parameter, string? name, string? path,
        Location location, ImmutableArray<LifetimeDeclarationIssue>.Builder issues, CancellationToken cancellationToken)
    {
        LifetimeTypePosition? Invalid(string message)
        { issues.Add(new LifetimeDeclarationIssue(location, message)); return null; }
        if (name is null || path is null) return Invalid("TypeLifetime requires non-null region and type path strings");
        if (path.Length > 1024 || name.Length > 1024) return Invalid("TypeLifetime syntax exceeds the bounded path/name length");
        var outer = 0;
        while (outer < name.Length && name[outer] == '^') outer++;
        var identifier = name.Substring(outer);
        if (identifier == "this" ? !LifetimeContractModel.HasReceiver(method) : !SyntaxFacts.IsValidIdentifier(identifier) || identifier.StartsWith("@", StringComparison.Ordinal))
            return Invalid("TypeLifetime names must be identifiers, optionally preceded by enclosing-binder '^' prefixes");
        if (parameter < 0 && method.ReturnsVoid) return Invalid("a void result cannot carry TypeLifetime metadata");
        var type = parameter < 0 ? method.ReturnType : method.Parameters[parameter].Type;
        var variance = parameter < 0 ? method.ReturnsByRef && !method.ReturnsByRefReadonly ? RegionVariance.Invariant : RegionVariance.Covariant :
            method.Parameters[parameter].RefKind is RefKind.Ref or RefKind.Out ? RegionVariance.Invariant : RegionVariance.Contravariant;
        var input = parameter >= 0;
        var scopeNames = new List<string> { string.Empty };
        var scopes = ImmutableArray.CreateBuilder<LifetimeTypeScope>();
        var currentPath = parameter < 0 ? "result" : "parameter" + parameter;
        var segments = path.Length == 0 ? Array.Empty<string>() : path.Split('.');
        if (segments.Length > MaximumSegments) return Invalid("TypeLifetime path exceeds the 64-segment limit");
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment == "element")
            {
                if (type is not IArrayTypeSymbol array) return Invalid("TypeLifetime 'element' requires an array type at '" + currentPath + "'");
                type = array.ElementType;
                variance = RegionVariance.Invariant; // Arrays expose writable element storage, including covariant CLR arrays.
            }
            else if (Index(segment, "arg", out var argument))
            {
                if (type is not INamedTypeSymbol named || argument >= named.TypeArguments.Length)
                    return Invalid("TypeLifetime generic argument is absent at '" + currentPath + "'");
                variance = RegionVarianceRules.Compose(variance, named.OriginalDefinition.TypeParameters[argument].Variance switch
                { VarianceKind.Out => RegionVariance.Covariant, VarianceKind.In => RegionVariance.Contravariant, _ => RegionVariance.Invariant });
                type = named.TypeArguments[argument];
            }
            else if (segment == "return" || Index(segment, "param", out _))
            {
                if (type is not INamedTypeSymbol { DelegateInvokeMethod: { } invoke })
                    return Invalid("TypeLifetime callable path requires a delegate type at '" + currentPath + "'");
                scopes.Add(new LifetimeTypeScope(currentPath, scopeNames[scopeNames.Count - 1], variance));
                scopeNames.Add(currentPath);
                if (segment == "return")
                {
                    if (invoke.ReturnsVoid) return Invalid("TypeLifetime cannot select a void delegate result");
                    variance = invoke.ReturnsByRef && !invoke.ReturnsByRefReadonly ? RegionVariance.Invariant : RegionVariance.Covariant;
                    type = invoke.ReturnType;
                    input = false;
                }
                else
                {
                    Index(segment, "param", out var ordinal);
                    if (ordinal >= invoke.Parameters.Length) return Invalid("TypeLifetime delegate parameter is absent at '" + currentPath + "'");
                    var selected = invoke.Parameters[ordinal];
                    variance = selected.RefKind is RefKind.Ref or RefKind.Out ? RegionVariance.Invariant : RegionVariance.Contravariant;
                    type = selected.Type;
                    input = true;
                }
            }
            else return Invalid("TypeLifetime path segment '" + segment + "' is invalid; use argN, element, paramN or return");
            currentPath += "." + segment;
        }
        if (outer >= scopeNames.Count) return Invalid("TypeLifetime refers beyond the enclosing lifetime binders");
        if (identifier == "this" && scopeNames[scopeNames.Count - 1 - outer].Length != 0)
            return Invalid("the reserved 'this' lifetime belongs to the outer instance-method binder");
        return new LifetimeTypePosition(parameter, path, identifier, scopeNames[scopeNames.Count - 1], scopeNames[scopeNames.Count - 1 - outer],
            variance, type, input, location, scopes.ToImmutable());
    }

    private static bool Index(string segment, string prefix, out int index)
    {
        index = -1;
        if (!segment.StartsWith(prefix, StringComparison.Ordinal) || segment.Length == prefix.Length) return false;
        var suffix = segment.Substring(prefix.Length);
        return (suffix.Length == 1 || suffix[0] != '0') && suffix.All(character => character is >= '0' and <= '9') && int.TryParse(suffix, out index);
    }
}
