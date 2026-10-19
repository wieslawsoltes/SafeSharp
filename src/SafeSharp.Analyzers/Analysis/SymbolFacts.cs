using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SafeSharp.Analyzers.Analysis;

internal static class SymbolFacts
{
    public static bool HasAttribute(ISymbol? symbol, string name) =>
        symbol is not null && ContractSources(symbol).Any(source => DirectAttribute(source, name) is not null);

    public static bool HasReturnAttribute(IMethodSymbol method, string name) =>
        ContractSources(method).OfType<IMethodSymbol>().Any(source => source.GetReturnTypeAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "SafeSharp." + name + "Attribute"));

    public static AttributeData? Attribute(ISymbol symbol, string name) =>
        ContractSources(symbol).Select(source => DirectAttribute(source, name)).FirstOrDefault(attribute => attribute is not null);

    public static AttributeData? DirectAttribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "SafeSharp." + name + "Attribute");

    public static IEnumerable<ISymbol> ContractSources(ISymbol symbol)
    {
        yield return symbol;
        if (symbol is IParameterSymbol parameter && parameter.ContainingSymbol is IMethodSymbol owner)
        {
            foreach (var source in ContractSources(owner).OfType<IMethodSymbol>().Where(source => !SymbolEqualityComparer.Default.Equals(source, owner)))
                if (parameter.Ordinal < source.Parameters.Length) yield return source.Parameters[parameter.Ordinal];
        }
        else if (symbol is IMethodSymbol method)
        {
            if (method.AssociatedSymbol is { } associated) yield return associated;
            for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
                yield return overridden;
            foreach (var @interface in method.ContainingType.AllInterfaces)
            foreach (var member in @interface.GetMembers().OfType<IMethodSymbol>())
                if (method.ContainingType.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation &&
                    SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, method.OriginalDefinition))
                    yield return member;
        }
        else if (symbol is INamedTypeSymbol type)
        {
            for (var parent = type.BaseType; parent is not null; parent = parent.BaseType) yield return parent;
        }
    }

    public static bool Implements(ITypeSymbol? type, string name) =>
        type?.ToDisplayString() == name || type?.AllInterfaces.Any(i => i.ToDisplayString() == name) == true ||
        (type is ITypeParameterSymbol parameter && parameter.ConstraintTypes.Any(t => Implements(t, name)));

    public static bool IsDisposable(ITypeSymbol? type) => Implements(type, "System.IDisposable") || Implements(type, "System.IAsyncDisposable");

    public static bool IsTracked(ITypeSymbol? type) => IsDisposable(type) || HasAttribute(type, "Owned") || HasAttribute(type, "NoSuspend") || IsMemoryView(type);

    public static bool IsMemoryView(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named || named.ContainingNamespace.ToDisplayString() != "System") return false;
        return named.Name is "Memory" or "ReadOnlyMemory" or "Span" or "ReadOnlySpan";
    }

    public static bool IsReadOnlyView(ITypeSymbol? type) => type?.Name is "ReadOnlyMemory" or "ReadOnlySpan";

    public static IOperation Unwrap(IOperation operation)
    {
        while (true)
        {
            if (operation is IConversionOperation conversion) operation = conversion.Operand;
            else if (operation is IParenthesizedOperation parenthesized) operation = parenthesized.Operand;
            else return operation;
        }
    }

    public static bool IsDispose(IMethodSymbol method) => method.Parameters.Length == 0 &&
        (method.Name == "Dispose" || method.ExplicitInterfaceImplementations.Any(m => m.Name == "Dispose")) &&
        (Implements(method.ContainingType, "System.IDisposable") || method.ContainingType.IsRefLikeType);

    public static bool IsDisposeAsync(IMethodSymbol method) => method.Parameters.Length == 0 &&
        (method.Name == "DisposeAsync" || method.ExplicitInterfaceImplementations.Any(m => m.Name == "DisposeAsync")) &&
        (Implements(method.ContainingType, "System.IAsyncDisposable") || method.ContainingType.IsRefLikeType);

    public static bool IsServiceResolution(IMethodSymbol method) =>
        (method.Name is "GetService" or "GetRequiredService" or "GetKeyedService" or "GetRequiredKeyedService") &&
        (method.ContainingNamespace.ToDisplayString().StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal) ||
         method.ContainingType.ToDisplayString() == "System.IServiceProvider");

    public static bool IsScopeFactory(IMethodSymbol method) =>
        (method.Name is "CreateScope" or "CreateAsyncScope") &&
        method.ContainingNamespace.ToDisplayString().StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal);

    public static bool IsKnownOwnedFactory(IMethodSymbol method) => IsScopeFactory(method) ||
        (method.ContainingType.ToDisplayString() == "System.IO.File" &&
         method.Name is "Open" or "OpenRead" or "OpenWrite" or "Create" or "OpenText" or "CreateText" or "AppendText") ||
        (method.ContainingType.ToDisplayString() == "System.Threading.CancellationTokenSource" && method.Name == "CreateLinkedTokenSource") ||
        (method.ContainingType.ToDisplayString() == "System.Net.Http.HttpClient" && method.Name is "GetStreamAsync" or "Send" or "SendAsync") ||
        (method.ContainingNamespace.ToDisplayString() == "System.Buffers" && method.Name == "Rent" && IsDisposable(method.ReturnType));

    public static bool IsPoolRent(IMethodSymbol method) => method.Name is "Rent" or "Get" && IsPool(method.ContainingType);
    public static bool IsPoolReturn(IMethodSymbol method) => method.Name == "Return" && IsPool(method.ContainingType);
    private static bool IsPool(INamedTypeSymbol type) =>
        type.Name == "ArrayPool" && type.ContainingNamespace.ToDisplayString() == "System.Buffers" ||
        type.Name == "ObjectPool" && type.ContainingNamespace.ToDisplayString() == "Microsoft.Extensions.ObjectPool";

    public static bool IsGenerated(SyntaxTree tree, CancellationToken cancellationToken)
    {
        var path = tree.FilePath;
        if (path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)) return true;
        var text = tree.GetText(cancellationToken);
        return text.ToString(new Microsoft.CodeAnalysis.Text.TextSpan(0, Math.Min(text.Length, 2048)))
            .IndexOf("<auto-generated", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool IsInsideUsing(SyntaxNode syntax) =>
        syntax.AncestorsAndSelf().Any(a => a is UsingStatementSyntax ||
            a is LocalDeclarationStatementSyntax local && !local.UsingKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None));
}
