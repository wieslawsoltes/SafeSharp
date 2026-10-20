using Microsoft.CodeAnalysis;

namespace SafeSharp.Analyzers.Semantics;

internal static partial class LifetimeDelegateAnalysis
{
    private sealed partial class Binding
    {
        // Contract caches use original method definitions. An inherited I<List<U>> bound must
        // therefore retain List<U>, then substitute U from C<Resource>.M at the actual boundary.
        // Compare Roslyn symbols, never display names or ordinals from unrelated declarations.
        // Memoization shares repeated generic subtrees within this side of the comparison.
        private ITypeSymbol ResolveType(ITypeSymbol type, IMethodSymbol method, IMethodSymbol promisedMethod,
            bool alignMethodParameters, Dictionary<ITypeSymbol, ITypeSymbol> substitutions, int depth = 0)
        {
            TypeStep();
            if (substitutions.TryGetValue(type, out var cached)) return cached;
            if (depth > LifetimeTypePath.MaximumSegments) throw new BindingBudgetException("type-argument substitution depth");
            ITypeSymbol result = type;
            if (type is ITypeParameterSymbol parameter)
            {
                if (parameter.TypeParameterKind == TypeParameterKind.Method && parameter.Ordinal < method.TypeArguments.Length &&
                    SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol.OriginalDefinition, method.OriginalDefinition))
                    result = method.TypeArguments[parameter.Ordinal];
                else
                    for (var containing = method.ContainingType; containing is not null; containing = containing.ContainingType)
                    {
                        TypeStep();
                        if (!SymbolEqualityComparer.Default.Equals(containing.OriginalDefinition, parameter.ContainingSymbol.OriginalDefinition)) continue;
                        result = containing.TypeArguments[parameter.Ordinal];
                        break;
                    }
                if (alignMethodParameters && result is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } aligned &&
                    SymbolEqualityComparer.Default.Equals(aligned.ContainingSymbol.OriginalDefinition, method.OriginalDefinition) && aligned.Ordinal < promisedMethod.TypeParameters.Length)
                    result = promisedMethod.TypeParameters[aligned.Ordinal];
            }
            else if (type is IArrayTypeSymbol array)
            {
                var element = ResolveType(array.ElementType, method, promisedMethod, alignMethodParameters, substitutions, depth + 1);
                if (!SymbolEqualityComparer.Default.Equals(element, array.ElementType)) result = compilation.CreateArrayTypeSymbol(element, array.Rank);
            }
            else if (type is INamedTypeSymbol named)
            {
                var containing = named.ContainingType is { } owner
                    ? (INamedTypeSymbol)ResolveType(owner, method, promisedMethod, alignMethodParameters, substitutions, depth + 1) : null;
                var changed = !SymbolEqualityComparer.Default.Equals(containing, named.ContainingType);
                var arguments = new ITypeSymbol[named.TypeArguments.Length];
                for (var index = 0; index < arguments.Length; index++)
                {
                    arguments[index] = ResolveType(named.TypeArguments[index], method, promisedMethod, alignMethodParameters, substitutions, depth + 1);
                    changed |= !SymbolEqualityComparer.Default.Equals(arguments[index], named.TypeArguments[index]);
                }
                if (changed)
                {
                    var definition = containing is null || SymbolEqualityComparer.Default.Equals(containing, named.ContainingType)
                        ? named.ConstructedFrom : containing.GetTypeMembers(named.Name, named.Arity)
                            .First(candidate => SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, named.OriginalDefinition));
                    result = arguments.Length == 0 ? definition : definition.Construct(arguments);
                }
            }
            substitutions.Add(type, result);
            return result;
        }
    }
}
