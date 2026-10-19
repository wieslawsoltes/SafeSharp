using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace SafeSharp.Analyzers.Semantics;

internal sealed class ContractSymbols
{
    private readonly Dictionary<string, INamedTypeSymbol?> attributes = new();

    public ContractSymbols(Compilation compilation)
    {
        Compilation = compilation;
        foreach (var name in new[] { "Owned", "Borrowed", "BorrowedMut", "Consumes", "ReturnsOwned", "ReturnsBorrowed", "Mutates", "DoesNotEscape", "MustUse", "NoSuspend", "ThreadAffine", "ThreadSafe", "ServiceLifetime" })
            attributes.Add(name, compilation.GetTypeByMetadataName("SafeSharp." + name + "Attribute"));
        Disposable = compilation.GetTypeByMetadataName("System.IDisposable");
        AsyncDisposable = compilation.GetTypeByMetadataName("System.IAsyncDisposable");
        Task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        ServiceCollectionExtensions = compilation.GetTypeByMetadataName("Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions");
        ServiceProviderExtensions = compilation.GetTypeByMetadataName("Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions");
        Enumerable = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
    }

    public Compilation Compilation { get; }
    public INamedTypeSymbol? Disposable { get; }
    public INamedTypeSymbol? AsyncDisposable { get; }
    public INamedTypeSymbol? Task { get; }
    public INamedTypeSymbol? ServiceCollectionExtensions { get; }
    public INamedTypeSymbol? ServiceProviderExtensions { get; }
    public INamedTypeSymbol? Enumerable { get; }

    public bool Has(ISymbol? symbol, string name) => symbol is not null && Find(symbol.GetAttributes(), name) is not null;

    public AttributeData? Find(ImmutableArray<AttributeData> source, string name)
    {
        if (!attributes.TryGetValue(name, out var expected) || expected is null) return null;
        foreach (var attribute in source)
            if (Equal(attribute.AttributeClass, expected)) return attribute;
        return null;
    }

    public bool HasOnType(ITypeSymbol? type, string name)
    {
        if (type is ITypeParameterSymbol parameter)
        {
            foreach (var constraint in parameter.ConstraintTypes)
                if (HasOnType(constraint, name)) return true;
        }
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (Has(current, name)) return true;
        return false;
    }

    public bool Implements(ITypeSymbol? type, INamedTypeSymbol? contract)
    {
        if (type is null || contract is null) return false;
        if (Equal(type, contract)) return true;
        foreach (var implemented in type.AllInterfaces)
            if (Equal(implemented, contract)) return true;
        if (type is ITypeParameterSymbol parameter)
            foreach (var constraint in parameter.ConstraintTypes)
                if (Implements(constraint, contract)) return true;
        return false;
    }

    public bool IsDisposable(ITypeSymbol? type) => Implements(type, Disposable) || Implements(type, AsyncDisposable);
    public bool IsTaskRun(IMethodSymbol method) => method.Name == "Run" && method.IsStatic && Equal(method.ContainingType, Task);
    public static bool Equal(ISymbol? left, ISymbol? right) => SymbolEqualityComparer.Default.Equals(left, right);

    public static IOperation Unwrap(IOperation operation)
    {
        while (true)
        {
            if (operation is IConversionOperation conversion) operation = conversion.Operand;
            else if (operation is IParenthesizedOperation parenthesized) operation = parenthesized.Operand;
            else return operation;
        }
    }

    public static IEnumerable<IOperation> Walk(IOperation root, bool descendFunctions = false)
    {
        var stack = new Stack<IOperation>();
        stack.Push(root);
        while (stack.Count != 0)
        {
            var current = stack.Pop();
            yield return current;
            if (!descendFunctions && current != root && (current is IAnonymousFunctionOperation || current is ILocalFunctionOperation)) continue;
            foreach (var child in current.ChildOperations) stack.Push(child);
        }
    }
}
