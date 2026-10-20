using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using SafeSharp.Core.Regions;

namespace SafeSharp.Analyzers.Semantics;

internal sealed partial class LifetimeContractProvider
{
    private IEnumerable<AttributeData> TypeLifetimes(IMethodSymbol method, int parameter) =>
        (parameter < 0 ? method.GetReturnTypeAttributes() : method.Parameters[parameter].GetAttributes())
        .Where(attribute => Is(attribute, typeLifetimeAttribute));

    private IEnumerable<AttributeData> TypeOutlives(IMethodSymbol method) =>
        (method.MethodKind == MethodKind.DelegateInvoke ? method.ContainingType.GetAttributes() : method.GetAttributes())
        .Where(attribute => Is(attribute, typeOutlivesAttribute));

    private string? TranslateScopedTypeRegion(string? name, IMethodSymbol declaration, IMethodSymbol method, string scope, CancellationToken cancellationToken)
    {
        if (name is null || SymbolEqualityComparer.Default.Equals(declaration, method)) return name;
        foreach (var position in Get(declaration, cancellationToken).TypePositions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position.RegionScope != scope || position.Name != name) continue;
            var attribute = TypeLifetimes(method, position.Parameter).FirstOrDefault(candidate => Argument(candidate, 1) == position.Path);
            if (Name(attribute) is { } translated && !translated.StartsWith("^", StringComparison.Ordinal)) return translated;
        }
        return name;
    }

    private (ImmutableArray<LifetimeTypePosition> Positions, ImmutableArray<LifetimeTypeBound> Bounds, ImmutableArray<RegionConstraint> Constraints) BuildTypes(
        IMethodSymbol method, IMethodSymbol[] inherited, ImmutableArray<string?> parameters, string? returned,
        Func<string?, IMethodSymbol, string?> translate, ImmutableArray<LifetimeDeclarationIssue>.Builder issues,
        ImmutableArray<Location>.Builder locations, CancellationToken cancellationToken)
    {
        var positions = ImmutableArray.CreateBuilder<LifetimeTypePosition>();
        var bounds = ImmutableArray.CreateBuilder<LifetimeTypeBound>();
        var constraints = ImmutableArray.CreateBuilder<RegionConstraint>();
        var known = new HashSet<string>(parameters.OfType<string>(), StringComparer.Ordinal);
        if (returned is not null) known.Add(returned);
        if (LifetimeContractModel.HasReceiver(method)) known.Add("this");
        for (var parameter = -1; parameter < method.Parameters.Length; parameter++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = method;
            var attributes = TypeLifetimes(method, parameter).ToArray();
            if (attributes.Length == 0)
                foreach (var declaration in inherited)
                {
                    if (parameter >= declaration.Parameters.Length) continue;
                    var candidates = TypeLifetimes(declaration, parameter).ToArray();
                    if (candidates.Length == 0) continue;
                    attributes = candidates; source = declaration; break;
                }
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attribute in attributes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var location = Site(attribute, parameter < 0 ? method : method.Parameters[parameter], cancellationToken);
                locations.Add(location);
                var parsed = LifetimeTypePath.Parse(method, parameter, Name(attribute), Argument(attribute, 1), location, issues, cancellationToken);
                if (parsed is null) continue;
                if (!paths.Add(parsed.Path))
                { issues.Add(new LifetimeDeclarationIssue(location, "a type position can declare only one lifetime: '" + parsed.Path + "'")); continue; }
                if (parsed.RegionScope.Length == 0 && translate(parsed.Name, source) is { } translated && translated != parsed.Name)
                    parsed = new LifetimeTypePosition(parsed.Parameter, parsed.Path, translated, parsed.Scope, parsed.RegionScope,
                        parsed.Variance, parsed.Type, parsed.IsInput, parsed.Location, parsed.Scopes);
                positions.Add(parsed);
                if (parsed.Scope == parsed.RegionScope) known.Add(parsed.Region);
            }
        }
        var byPath = positions.ToDictionary(position => (position.Parameter, position.Path));
        foreach (var position in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!known.Contains(position.Region))
                issues.Add(new LifetimeDeclarationIssue(position.Location, "TypeLifetime outer reference has no declaration in the enclosing binder: '" + position.Name + "'"));
            if (position.Scope != position.RegionScope) continue;
            // Only explicitly annotated borrowed contents imply well-formedness bounds. Crossing
            // a callable binder does not make its parameter/result lifetimes outlive the delegate object.
            var owner = position.Parameter < 0 ? returned : parameters[position.Parameter];
            if (owner is not null && position.Scope.Length == 0 && position.Path.Length != 0)
                constraints.Add(new RegionConstraint(position.Region, owner, Source(position.Location), "Borrowed type contents outlive their annotated owner."));
            var prefix = position.Path;
            while (prefix.Length != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var separator = prefix.LastIndexOf('.');
                prefix = separator < 0 ? string.Empty : prefix.Substring(0, separator);
                if (!byPath.TryGetValue((position.Parameter, prefix), out var ancestor) || ancestor.Scope != position.Scope || ancestor.RegionScope != position.RegionScope) continue;
                constraints.Add(new RegionConstraint(position.Region, ancestor.Region, Source(position.Location), "Nested borrowed type contents imply an outlives bound."));
            }
            if (position.IsInput && position.Type is ITypeParameterSymbol type)
                bounds.Add(new LifetimeTypeBound(type, position.Region, position.Scope, position.Location, true));
        }
        for (var parameter = 0; parameter < method.Parameters.Length; parameter++)
            if (parameters[parameter] is { } region && method.Parameters[parameter].Type is ITypeParameterSymbol type)
                bounds.Add(new LifetimeTypeBound(type, region, string.Empty, method.Parameters[parameter].Locations.FirstOrDefault() ?? Location.None, true));

        var boundSource = method;
        var explicitBounds = TypeOutlives(method).ToArray();
        if (explicitBounds.Length == 0)
            foreach (var declaration in inherited)
            {
                var candidates = TypeOutlives(declaration).ToArray();
                if (candidates.Length == 0) continue;
                explicitBounds = candidates; boundSource = declaration; break;
            }
        foreach (var attribute in explicitBounds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = Site(attribute, method, cancellationToken);
            locations.Add(location);
            var typeName = Argument(attribute, 0);
            var scope = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "Scope").Value.Value as string ?? string.Empty;
            var region = scope.Length == 0 ? translate(Argument(attribute, 1), boundSource) :
                TranslateScopedTypeRegion(Argument(attribute, 1), boundSource, method, scope, cancellationToken);
            var declared = TypeParameter(boundSource, typeName);
            ITypeSymbol? type = declared;
            if (declared is not null && !SymbolEqualityComparer.Default.Equals(boundSource, method))
            {
                if (declared.TypeParameterKind == TypeParameterKind.Method && declared.Ordinal < method.TypeParameters.Length)
                    type = method.TypeParameters[declared.Ordinal];
                else
                    // The interface/base argument may be concrete or structurally contain the
                    // implementing type's parameters. An unrelated C<U>.U is not I<int>.T merely
                    // because both occupy ordinal zero. Preserve the actual inherited argument.
                    for (var containing = boundSource.ContainingType; containing is not null; containing = containing.ContainingType)
                        if (SymbolEqualityComparer.Default.Equals(containing.OriginalDefinition, declared.ContainingSymbol.OriginalDefinition))
                        { type = containing.TypeArguments[declared.Ordinal]; break; }
            }
            if (type is null)
            { issues.Add(new LifetimeDeclarationIssue(location, "TypeOutlives must name a generic type parameter in the method or containing type")); continue; }
            if (scope.Length != 0 && !positions.Any(position => position.Scopes.Any(binder => binder.Key == scope)))
            { issues.Add(new LifetimeDeclarationIssue(location, "TypeOutlives scope must name a callable binder introduced by TypeLifetime paths")); continue; }
            var identity = region is null ? null : LifetimeTypePosition.Identity(scope, region);
            if (!ValidName(region, method) || identity is null || !known.Contains(identity))
            { issues.Add(new LifetimeDeclarationIssue(location, "TypeOutlives must name a region declared in the selected signature binder")); continue; }
            bounds.Add(new LifetimeTypeBound(type, identity, scope, location, false));
        }
        return (positions.ToImmutable(), bounds.ToImmutable(), constraints.ToImmutable());
    }

    private static ITypeParameterSymbol? TypeParameter(IMethodSymbol method, string? name)
    {
        var parameter = method.TypeParameters.FirstOrDefault(candidate => candidate.Name == name);
        if (parameter is not null) return parameter;
        for (var containing = method.ContainingType; containing is not null; containing = containing.ContainingType)
            if (containing.TypeParameters.FirstOrDefault(candidate => candidate.Name == name) is { } declared) return declared;
        return null;
    }
}
