using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SafeSharp.Analyzers.Semantics;

/// <summary>
/// Separates aggregate destruction obligations from intraprocedural loan analysis.
/// Rust Drop cannot be implemented for Copy types; C# has no equivalent negative capability:
/// https://doc.rust-lang.org/std/ops/trait.Drop.html#copy-and-drop-are-exclusive.
/// </summary>
internal static class AggregateOwnershipAnalysis
{
    public static void Analyze(SymbolAnalysisContext context, INamedTypeSymbol type, ContractSymbols symbols)
    {
        if (type.IsImplicitlyDeclared || (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)) return;
        var owned = new List<ISymbol>();
        foreach (var member in type.GetMembers())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (member.IsStatic || member.IsImplicitlyDeclared) continue;
            var memberType = GetMemberType(member);
            if (memberType is not null && (symbols.Has(member, "Owned") || symbols.Has(memberType, "Owned"))) owned.Add(member);
        }
        var typeLocation = type.Locations.FirstOrDefault(l => l.IsInSource);
        if (type.TypeKind == TypeKind.Struct && (owned.Count > 0 || symbols.Has(type, "Owned")) && typeLocation is not null)
            context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.CopyableOwner, typeLocation, type.Name));

        var disposableMembers = owned.Where(m => symbols.IsDisposable(GetMemberType(m))).ToArray();
        if (disposableMembers.Length == 0) return;
        if (!symbols.IsDisposable(type))
        {
            foreach (var member in disposableMembers)
            {
                var location = member.Locations.FirstOrDefault(l => l.IsInSource);
                if (location is not null) context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.MissingDisposalContract, location, type.Name, member.Name));
            }
            return;
        }

        var released = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var contract in new[] { symbols.Disposable, symbols.AsyncDisposable })
        {
            if (contract is null) continue;
            foreach (var contractMember in contract.GetMembers().OfType<IMethodSymbol>())
                if (type.FindImplementationForInterfaceMember(contractMember) is IMethodSymbol implementation)
                    VisitMethod(implementation, type, symbols, released, visited, context.CancellationToken);
        }
        foreach (var member in disposableMembers)
        {
            if (released.Contains(member)) continue;
            var location = member.Locations.FirstOrDefault(l => l.IsInSource);
            if (location is not null) context.ReportDiagnostic(Diagnostic.Create(SemanticDiagnostics.OwnedFieldNotDisposed, location, member.Name));
        }
    }

    private static void VisitMethod(IMethodSymbol method, INamedTypeSymbol owner, ContractSymbols symbols, HashSet<ISymbol> released, HashSet<ISymbol> visited, CancellationToken cancellationToken)
    {
        var pending = new Stack<IMethodSymbol>();
        pending.Push(method);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentMethod = pending.Pop();
            if (!visited.Add(currentMethod.OriginalDefinition)) continue;
            foreach (var reference in currentMethod.DeclaringSyntaxReferences)
            {
                var syntax = reference.GetSyntax(cancellationToken);
                var operation = symbols.Compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax, cancellationToken);
                if (operation is null) continue;
                foreach (var item in ContractSymbols.Walk(operation))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item is not IInvocationOperation invocation) continue;
                    var instance = ResolveConditionalReceiver(invocation);
                    var member = instance switch
                    {
                        IFieldReferenceOperation field => (ISymbol)field.Field,
                        IPropertyReferenceOperation property => property.Property,
                        _ => null,
                    };
                    if (member is not null && IsDisposalCall(invocation, instance?.Type, symbols, out var asynchronous)
                        && (!asynchronous || IsObservedAsyncDisposal(invocation)))
                        released.Add(member);
                    if (invocation.TargetMethod.MethodKind == MethodKind.LocalFunction
                        || (instance is IInstanceReferenceOperation && IsInHierarchy(owner, invocation.TargetMethod.ContainingType)))
                        pending.Push(ResolveVirtualTarget(invocation, owner));
                }
            }
        }
    }

    private static bool IsDisposalCall(IInvocationOperation invocation, ITypeSymbol? receiverType, ContractSymbols symbols, out bool asynchronous)
    {
        asynchronous = false;
        if (invocation.Arguments.Length != 0 || receiverType is null) return false;
        var method = invocation.TargetMethod;
        foreach (var contract in new[] { symbols.Disposable, symbols.AsyncDisposable })
        {
            if (contract is null || !symbols.Implements(receiverType, contract)) continue;
            foreach (var member in contract.GetMembers().OfType<IMethodSymbol>())
            {
                var implementation = (receiverType as INamedTypeSymbol)?.FindImplementationForInterfaceMember(member);
                if (ContractSymbols.Equal(method.OriginalDefinition, member.OriginalDefinition)
                    || ContractSymbols.Equal(method.OriginalDefinition, implementation?.OriginalDefinition))
                {
                    asynchronous = ContractSymbols.Equal(contract, symbols.AsyncDisposable);
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsObservedAsyncDisposal(IInvocationOperation invocation)
    {
        IOperation current = invocation;
        while (current.Parent is { } parent)
        {
            if (parent is IAwaitOperation || parent is IReturnOperation) return true;
            if (parent is IConversionOperation || parent is IParenthesizedOperation || parent is IConditionalAccessOperation)
            {
                current = parent;
                continue;
            }
            if (parent is IInvocationOperation wrapper && ReferenceEquals(wrapper.Instance, current)
                && wrapper.TargetMethod.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks"
                && (wrapper.TargetMethod.Name == "ConfigureAwait" || wrapper.TargetMethod.Name == "AsTask"))
            {
                current = parent;
                continue;
            }
            return false;
        }
        return false;
    }

    private static IOperation? ResolveConditionalReceiver(IInvocationOperation invocation)
    {
        var instance = invocation.Instance is null ? null : ContractSymbols.Unwrap(invocation.Instance);
        if (instance is not IConditionalAccessInstanceOperation) return instance;
        for (var current = invocation.Parent; current is not null; current = current.Parent)
            if (current is IConditionalAccessOperation conditional) return ContractSymbols.Unwrap(conditional.Operation);
        return null;
    }

    private static IMethodSymbol ResolveVirtualTarget(IInvocationOperation invocation, INamedTypeSymbol owner)
    {
        if (!invocation.IsVirtual) return invocation.TargetMethod;
        foreach (var candidate in owner.GetMembers(invocation.TargetMethod.Name).OfType<IMethodSymbol>())
            for (var overridden = candidate.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
                if (ContractSymbols.Equal(overridden.OriginalDefinition, invocation.TargetMethod.OriginalDefinition)) return candidate;
        return invocation.TargetMethod;
    }

    private static bool IsInHierarchy(INamedTypeSymbol type, INamedTypeSymbol candidate)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            if (ContractSymbols.Equal(current, candidate)) return true;
        return false;
    }

    private static ITypeSymbol? GetMemberType(ISymbol member) => member switch
    {
        IFieldSymbol field => field.Type,
        IPropertySymbol property when property.GetMethod is not null => property.Type,
        _ => null,
    };
}
