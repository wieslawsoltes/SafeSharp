using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SafeSharp.Analyzers.Semantics;

internal static class ContractValidationAnalysis
{
    public static void Analyze(SymbolAnalysisContext context, ContractSymbols symbols)
    {
        var symbol = context.Symbol;
        if (symbol.IsImplicitlyDeclared) return;
        ValidateKinds(context, symbols, symbol, symbol.GetAttributes());
        if (symbol is IMethodSymbol method)
        {
            foreach (var parameter in method.Parameters)
                ValidateKinds(context, symbols, parameter, parameter.GetAttributes());
            ValidateKinds(context, symbols, method, method.GetReturnTypeAttributes(), isReturn: true);
            ValidateReturn(context, symbols, method, method.Parameters, method.IsStatic, method.ReturnsVoid, method.GetReturnTypeAttributes());
            if (method.IsStatic && symbols.Has(method, "Mutates"))
                Report(context, method, "Mutates describes an instance receiver and cannot annotate a static method");
            if (method.ReturnsVoid && symbols.Has(method, "MustUse"))
                Report(context, method, "MustUse requires a non-void result");
        }
        else if (symbol is IPropertySymbol property)
        {
            foreach (var parameter in property.Parameters)
                ValidateKinds(context, symbols, parameter, parameter.GetAttributes());
            ValidateReturn(context, symbols, property, property.Parameters, property.IsStatic, false, ImmutableArray<AttributeData>.Empty);
        }
        if (symbol is INamedTypeSymbol type)
        {
            if (symbols.HasOnType(type, "ThreadAffine") && symbols.HasOnType(type, "ThreadSafe"))
                Report(context, type, "ThreadAffine and ThreadSafe promise incompatible thread access contracts");
            var lifetime = symbols.Find(type.GetAttributes(), "ServiceLifetime");
            if (lifetime is not null && (lifetime.ConstructorArguments.Length != 1 || lifetime.ConstructorArguments[0].Value is not int value || value < 0 || value > 2))
                Report(context, type, "ServiceLifetime must be Transient, Scoped, or Singleton", lifetime);
        }
    }

    private static void ValidateKinds(SymbolAnalysisContext context, ContractSymbols symbols, ISymbol symbol, ImmutableArray<AttributeData> attributes, bool isReturn = false)
    {
        var owned = symbols.Find(attributes, "Owned") is not null || symbols.Find(attributes, "Consumes") is not null;
        var shared = symbols.Find(attributes, "Borrowed") is not null;
        var mutable = symbols.Find(attributes, "BorrowedMut") is not null;
        if ((owned ? 1 : 0) + (shared ? 1 : 0) + (mutable ? 1 : 0) > 1)
            Report(context, symbol, (isReturn ? "return value has" : "declaration has") + " conflicting owned, shared-borrow, or mutable-borrow contracts");
    }

    private static void ValidateReturn(SymbolAnalysisContext context, ContractSymbols symbols, ISymbol symbol, ImmutableArray<IParameterSymbol> parameters, bool isStatic, bool isVoid, ImmutableArray<AttributeData> returnAttributes)
    {
        var owned = symbols.Has(symbol, "ReturnsOwned") || symbols.Find(returnAttributes, "Owned") is not null;
        var borrow = symbols.Find(symbol.GetAttributes(), "ReturnsBorrowed");
        var shared = symbols.Find(returnAttributes, "Borrowed") is not null;
        var mutable = symbols.Find(returnAttributes, "BorrowedMut") is not null;
        if (owned && (borrow is not null || shared || mutable))
            Report(context, symbol, "a result cannot be both owned and borrowed", borrow);
        if (isVoid && (owned || borrow is not null || shared || mutable))
            Report(context, symbol, "a void-returning method cannot declare a result ownership contract", borrow);
        if (borrow is null) return;
        var source = borrow.ConstructorArguments.Length > 0 ? borrow.ConstructorArguments[0].Value as string : null;
        var isMutable = borrow.ConstructorArguments.Length > 1 && borrow.ConstructorArguments[1].Value is true;
        if (string.IsNullOrEmpty(source) || (source == "this" ? isStatic : !parameters.Any(p => p.Name == source)))
            Report(context, symbol, "ReturnsBorrowed must name an existing parameter or an instance receiver named 'this'", borrow);
        if ((shared && isMutable) || (mutable && !isMutable))
            Report(context, symbol, "ReturnsBorrowed mutability disagrees with the return-value attribute", borrow);
        var sourceParameter = parameters.FirstOrDefault(p => p.Name == source);
        if (sourceParameter is not null && isMutable && symbols.Has(sourceParameter, "Borrowed"))
            Report(context, symbol, "an exclusive returned loan cannot be derived from a shared-borrow parameter", borrow);
    }

    private static void Report(SymbolAnalysisContext context, ISymbol symbol, string reason, AttributeData? attribute = null)
    {
        var location = attribute?.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
            ?? symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is not null)
            context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.InvalidContract, location, symbol.Name, reason));
    }
}
