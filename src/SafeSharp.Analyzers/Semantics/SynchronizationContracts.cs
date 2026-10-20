using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using SafeSharp.Analyzers.Analysis;

namespace SafeSharp.Analyzers.Semantics;

internal enum GuardProtocol { Monitor, ThreadingLock, ReaderWriter }

/// <summary>A resolved, immutable member path; runtime receiver identities are supplied by dataflow.</summary>
internal sealed class GuardContract
{
    internal GuardContract(string text, int parameter, ImmutableArray<IFieldSymbol> fields,
        GuardProtocol protocol, bool write, Location location, string? error)
    { Text = text; Parameter = parameter; Fields = fields; Protocol = protocol; Write = write; Location = location; Error = error; }
    internal string Text { get; }
    // -1 is the receiver; -2 is a static field root.
    internal int Parameter { get; }
    internal ImmutableArray<IFieldSymbol> Fields { get; }
    internal GuardProtocol Protocol { get; }
    internal bool Write { get; }
    internal Location Location { get; }
    internal string? Error { get; }
    internal bool SamePath(GuardContract other) => Parameter == other.Parameter && Protocol == other.Protocol &&
        Fields.Length == other.Fields.Length && Fields.Zip(other.Fields, (left, right) =>
            SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition)).All(equal => equal);
}

/// <summary>Compilation-local, symbol-identity resolution; no syntax text is treated as a lock identity.</summary>
internal sealed class SynchronizationContracts
{
    private readonly Lazy<MethodSummaryProvider> summaries;
    private readonly INamedTypeSymbol? guardedBy;
    private readonly INamedTypeSymbol? requiresLock;
    private readonly ConcurrentDictionary<ISymbol, ImmutableArray<GuardContract>> members = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<IMethodSymbol, ImmutableArray<GuardContract>> methods = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<IMethodSymbol, ImmutableArray<GuardContract>> returns = new(SymbolEqualityComparer.Default);
    internal SynchronizationContracts(Compilation compilation)
    {
        Compilation = compilation;
        summaries = new Lazy<MethodSummaryProvider>(() => new MethodSummaryProvider(compilation));
        guardedBy = compilation.GetTypeByMetadataName("SafeSharp.GuardedByAttribute");
        requiresLock = compilation.GetTypeByMetadataName("SafeSharp.RequiresLockAttribute");
        Monitor = FrameworkType(compilation, "System.Threading.Monitor");
        ThreadingLock = FrameworkType(compilation, "System.Threading.Lock");
        ReaderWriter = FrameworkType(compilation, "System.Threading.ReaderWriterLockSlim");
    }
    private static INamedTypeSymbol? FrameworkType(Compilation compilation, string metadataName)
    {
        var type = compilation.GetTypeByMetadataName(metadataName);
        if (type is null || Same(type.ContainingAssembly, compilation.Assembly) || type.Locations.Any(location => location.IsInSource)) return null;
        return type.ContainingAssembly.Identity.Name is "mscorlib" or "System.Private.CoreLib" or "System.Runtime" or
            "netstandard" or "System.Threading" or "System.Core" ? type : null;
    }

    internal Compilation Compilation { get; }
    internal MethodSummaryProvider Summaries => summaries.Value;
    internal bool Available => guardedBy is not null || requiresLock is not null;
    internal INamedTypeSymbol? Monitor { get; }
    internal INamedTypeSymbol? ThreadingLock { get; }
    internal INamedTypeSymbol? ReaderWriter { get; }
    internal static bool Same(ISymbol? left, ISymbol? right) => SymbolEqualityComparer.Default.Equals(left, right);
    internal bool IsMonitor(IMethodSymbol method) => Same(method.ContainingType, Monitor);
    internal bool IsThreadingLock(IMethodSymbol method) => Same(method.ContainingType, ThreadingLock);
    internal bool IsReaderWriter(IMethodSymbol method) => Same(method.ContainingType, ReaderWriter);
    internal GuardProtocol Protocol(ITypeSymbol? type) => Same(type, ThreadingLock) ? GuardProtocol.ThreadingLock :
        Same(type, ReaderWriter) ? GuardProtocol.ReaderWriter : GuardProtocol.Monitor;

    internal ImmutableArray<GuardContract> Members(ISymbol member) => members.GetOrAdd(member, symbol =>
    {
        var result = Parse(symbol.GetAttributes(), guardedBy, symbol, null).ToBuilder();
        if (symbol is IPropertySymbol property)
            foreach (var inherited in PropertyBases(property))
                if (!Same(inherited, property))
                    foreach (var contract in Parse(inherited.GetAttributes(), guardedBy, inherited, null))
                        if (!result.Any(existing => existing.SamePath(contract))) result.Add(contract);
        return result.ToImmutable();
    });

    internal ImmutableArray<GuardContract> Returns(IMethodSymbol method) => returns.GetOrAdd(method, symbol =>
    {
        var result = Parse(symbol.GetReturnTypeAttributes(), guardedBy, symbol, symbol).ToBuilder();
        foreach (var inherited in Bases(symbol))
            foreach (var contract in Parse(inherited.GetReturnTypeAttributes(), guardedBy, inherited, inherited))
                if (!result.Any(existing => existing.SamePath(contract))) result.Add(contract);
        return result.ToImmutable();
    });

    internal ImmutableArray<GuardContract> Required(IMethodSymbol method) => methods.GetOrAdd(method, symbol =>
    {
        var result = Declared(symbol).ToBuilder();
        if (symbol.AssociatedSymbol is IPropertySymbol property)
            foreach (var contract in Members(property))
                result.Add(new GuardContract(contract.Text, contract.Parameter, contract.Fields, contract.Protocol,
                    symbol.MethodKind == MethodKind.PropertySet, contract.Location, contract.Error));
        foreach (var inherited in Bases(symbol))
            foreach (var contract in Declared(inherited))
                if (!result.Any(existing => existing.SamePath(contract) && existing.Write == contract.Write)) result.Add(contract);
        return result.ToImmutable();
    });

    internal ImmutableArray<GuardContract> Declared(IMethodSymbol method) => Parse(
        method.MethodKind == MethodKind.DelegateInvoke ? method.ContainingType.GetAttributes() : method.GetAttributes(),
        requiresLock, method, method);

    internal IEnumerable<IPropertySymbol> PropertyBases(IPropertySymbol property)
    {
        var seen = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default) { property };
        if ((property.GetMethod ?? property.SetMethod) is { } accessor)
            foreach (var inherited in SymbolFacts.ContractSources(accessor).OfType<IPropertySymbol>())
                if (seen.Add(inherited)) yield return inherited;
    }

    internal IEnumerable<IMethodSymbol> Bases(IMethodSymbol method)
    {
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { method };
        for (var parent = method.OverriddenMethod; parent is not null; parent = parent.OverriddenMethod)
            if (seen.Add(parent)) yield return parent;
        foreach (var source in SymbolFacts.ContractSources(method).OfType<IMethodSymbol>())
            if (seen.Add(source)) yield return source;
    }

    private ImmutableArray<GuardContract> Parse(ImmutableArray<AttributeData> attributes, INamedTypeSymbol? attributeType,
        ISymbol owner, IMethodSymbol? method)
    {
        if (attributeType is null) return ImmutableArray<GuardContract>.Empty;
        var result = ImmutableArray.CreateBuilder<GuardContract>();
        foreach (var attribute in attributes)
        {
            if (!Same(attribute.AttributeClass, attributeType)) continue;
            var text = attribute.ConstructorArguments.FirstOrDefault().Value as string ?? "";
            var write = attribute.ConstructorArguments.Length < 2 || attribute.ConstructorArguments[1].Value is not int access || access != 0;
            var location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? owner.Locations.FirstOrDefault() ?? Location.None;
            var fields = ImmutableArray.CreateBuilder<IFieldSymbol>();
            var parameter = -1;
            ITypeSymbol? type = owner.ContainingType;
            string? error = null;
            var pieces = text.Split('.');
            var index = 0;
            if (string.IsNullOrWhiteSpace(text) || pieces.Length > 32 || pieces.Any(piece => piece.Length == 0 || piece != piece.Trim()))
                error = "a guard must be a nonempty path of at most 32 stable fields";
            else
            {
                if (pieces[0] == "this")
                {
                    index = 1;
                    if (owner.IsStatic) error = "a static member has no this guard";
                }
                else if (method?.Parameters.FirstOrDefault(value => value.Name == pieces[0]) is { } argument)
                {
                    parameter = argument.Ordinal; type = argument.Type; index = 1;
                    if (argument.RefKind != RefKind.None) error = "a by-reference parameter is not a stable guard root; pass a by-value snapshot";
                }
                for (; error is null && index < pieces.Length; index++)
                {
                    var field = FindField(type, pieces[index]);
                    if (field is null) { error = "guard component '" + pieces[index] + "' is not a field"; break; }
                    if (!field.IsReadOnly || field.IsVolatile || field.Type.IsValueType)
                    { error = "guard fields must be readonly reference fields"; break; }
                    if (field.IsStatic)
                    {
                        if (fields.Count != 0 || parameter >= 0 || index != 0)
                        { error = "a static guard field must be the first component"; break; }
                        parameter = -2;
                    }
                    else if (owner.IsStatic && parameter == -1)
                    { error = "a static member cannot use an instance guard field"; break; }
                    fields.Add(field); type = field.Type;
                }
                if (error is null && (type is null || !type.IsReferenceType || type.TypeKind is TypeKind.Dynamic or TypeKind.TypeParameter))
                    error = "a guard must have a known reference type; boxing and dynamic locks are not stable capabilities";
            }
            result.Add(new GuardContract(text, parameter, fields.ToImmutable(), Protocol(type), write, location, error));
        }
        return result.ToImmutable();
    }

    private static IFieldSymbol? FindField(ITypeSymbol? type, string name)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (current.GetMembers(name).FirstOrDefault() is { } member) return member as IFieldSymbol;
        return null;
    }
}
