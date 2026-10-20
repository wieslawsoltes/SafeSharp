using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace SafeSharp.Analyzers.Semantics;

internal sealed class ConcurrencyCapture
{
    public ConcurrencyCapture(ISymbol symbol, ITypeSymbol type, string name, Location location, bool mutableCell, bool sharedStorage)
    { Symbol = symbol; Type = type; Name = name; Location = location; MutableCell = mutableCell; SharedStorage = sharedStorage; }
    public ISymbol Symbol { get; }
    public ITypeSymbol Type { get; }
    public string Name { get; }
    public Location Location { get; }
    public bool MutableCell { get; }
    public bool SharedStorage { get; }
}

/// <summary>Resolves visible callback targets and distinguishes captured storage from the type of its value.</summary>
internal sealed class ConcurrencyCaptureAnalysis
{
    private readonly ConcurrencyContracts contracts;
    private readonly ConcurrencyTypeAnalysis analysis;
    private readonly IOperation root;
    private readonly bool includeAnnotatedFields;
    private readonly ConcurrencyCapability? requiredCapability;
    private readonly HashSet<ILocalSymbol> resolving = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, ConcurrencyCapture> captures = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, bool> writtenCells = new(SymbolEqualityComparer.Default);
    private readonly HashSet<IMethodSymbol> callbackMethods = new(SymbolEqualityComparer.Default);
    public ConcurrencyCaptureAnalysis(ConcurrencyContracts contracts, ConcurrencyTypeAnalysis analysis, IInvocationOperation invocation, bool includeAnnotatedFields,
        ConcurrencyCapability? requiredCapability = null)
    {
        this.contracts = contracts; this.analysis = analysis; this.includeAnnotatedFields = includeAnnotatedFields; this.requiredCapability = requiredCapability;
        root = invocation;
        while (root.Parent is { } parent && parent is not IAnonymousFunctionOperation && parent is not ILocalFunctionOperation) root = parent;
    }
    public bool Unresolved { get; private set; }
    public bool HasOpaqueMethodTargets { get; private set; }
    public IEnumerable<IMethodSymbol> CallbackMethods => callbackMethods.OrderBy(method => method.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0)
        .ThenBy(method => method.ToDisplayString(), StringComparer.Ordinal);
    public IEnumerable<ConcurrencyCapture> Captures => captures.Values.OrderBy(value => value.Location.SourceSpan.Start).ThenBy(value => value.Name, StringComparer.Ordinal);
    public void Resolve(IOperation value)
    {
        if (!analysis.Step()) return;
        value = ContractSymbols.Unwrap(value);
        switch (value)
        {
            case IDelegateCreationOperation creation: Resolve(creation.Target); return;
            case IAnonymousFunctionOperation lambda: callbackMethods.Add(lambda.Symbol); Collect(lambda.Body, lambda.Syntax); return;
            case ILocalFunctionOperation function:
                callbackMethods.Add(function.Symbol); if (function.Body is { } localBody) Collect(localBody, function.Syntax); return;
            case IMethodReferenceOperation { Method.MethodKind: MethodKind.LocalFunction } localFunction:
                callbackMethods.Add(localFunction.Method);
                var syntax = localFunction.Method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                if (syntax is not null && contracts.Compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax) is ILocalFunctionOperation resolvedFunction)
                { Resolve(resolvedFunction); return; }
                Unresolved = true; return;
            case IMethodReferenceOperation method:
                HasOpaqueMethodTargets = true;
                if (method.Instance is { } receiver) CollectReceiver(receiver);
                return;
            case IConditionalOperation conditional:
                Resolve(conditional.WhenTrue); if (conditional.WhenFalse is { } alternative) Resolve(alternative); return;
            case ICoalesceOperation coalesce: Resolve(coalesce.Value); Resolve(coalesce.WhenNull); return;
            case IParameterReferenceOperation parameter when requiredCapability is { } capability && contracts.HasBoundary(parameter.Parameter,
                capability == ConcurrencyCapability.Send ? "TransfersToThread" : "SharesAcrossThreads"):
                return; // Forwarding preserves the incoming callback's checked precondition.
            case ILocalReferenceOperation local:
                if (resolving.Count >= 128) { analysis.ExhaustBudget(); return; }
                if (!resolving.Add(local.Local)) { Unresolved = true; return; }
                var found = false;
                foreach (var operation in ContractSymbols.Walk(root))
                {
                    if (!analysis.Step()) break;
                    if (operation is IVariableDeclaratorOperation declaration && SymbolEqualityComparer.Default.Equals(declaration.Symbol, local.Local) && declaration.Initializer is { } initializer)
                    { found = true; Resolve(initializer.Value); }
                    else if (operation is ISimpleAssignmentOperation assignment && ContractSymbols.Unwrap(assignment.Target) is ILocalReferenceOperation target &&
                        SymbolEqualityComparer.Default.Equals(target.Local, local.Local)) { found = true; Resolve(assignment.Value); }
                }
                resolving.Remove(local.Local); Unresolved |= !found; return;
            default: Unresolved = true; return;
        }
    }
    private void Collect(IOperation body, SyntaxNode scope)
    {
        foreach (var operation in ContractSymbols.Walk(body, descendFunctions: true))
        {
            if (!analysis.Step()) return;
            if (WithinNameOf(operation)) continue;
            switch (operation)
            {
                case ILocalReferenceOperation local when !DeclaredWithin(local.Local, scope):
                    Add(local.Local, local.Type, local.Local.Name, local.Syntax.GetLocation(), HasWrites(local.Local), false); break;
                case IParameterReferenceOperation parameter when !DeclaredWithin(parameter.Parameter, scope):
                    Add(parameter.Parameter, parameter.Type, parameter.Parameter.Name, parameter.Syntax.GetLocation(), HasWrites(parameter.Parameter), false); break;
                case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance, Type: { } type } instance:
                    Add(type, type, "this", instance.Syntax.GetLocation(), false, false); break;
                case IFieldReferenceOperation field when field.Field.IsStatic:
                    Add(field.Field, field.Type, field.Field.Name, field.Syntax.GetLocation(), !field.Field.IsReadOnly && !field.Field.IsConst, true); break;
                case IFieldReferenceOperation field when includeAnnotatedFields && field.Type is { } type && contracts.HasExplicitConcurrency(type) && UsesOuterValue(field.Instance, scope):
                    Add(field.Field, type, field.Field.Name, field.Syntax.GetLocation(), false, false); break;
                case IPropertyReferenceOperation property when property.Property.IsStatic:
                    Add(property.Property, property.Type, property.Property.Name, property.Syntax.GetLocation(), property.Property.SetMethod is not null, true); break;
            }
        }
    }
    private void CollectReceiver(IOperation receiver)
    {
        receiver = ContractSymbols.Unwrap(receiver);
        switch (receiver)
        {
            // A method group evaluates and stores its receiver value at delegate creation;
            // it does not capture the variable cell that previously held that receiver.
            case ILocalReferenceOperation local: Add(local.Local, local.Type, local.Local.Name, local.Syntax.GetLocation(), false, false); break;
            case IParameterReferenceOperation parameter: Add(parameter.Parameter, parameter.Type, parameter.Parameter.Name, parameter.Syntax.GetLocation(), false, false); break;
            case IFieldReferenceOperation field: Add(field.Field, field.Type, field.Field.Name, field.Syntax.GetLocation(), false, false); break;
            case IPropertyReferenceOperation property: Add(property.Property, property.Type, property.Property.Name, property.Syntax.GetLocation(), false, false); break;
            case IInstanceReferenceOperation { Type: { } type } instance: Add(type, type, "this", instance.Syntax.GetLocation(), false, false); break;
            default:
                if (receiver.Type is { } receiverType) Add(receiverType, receiverType, receiver.Syntax.ToString(), receiver.Syntax.GetLocation(), false, false);
                else Unresolved = true;
                break;
        }
    }
    private void Add(ISymbol symbol, ITypeSymbol? type, string name, Location location, bool mutableCell, bool sharedStorage)
    {
        if (type is null) return;
        if (!captures.TryGetValue(symbol, out var previous)) captures.Add(symbol, new(symbol, type, name, location, mutableCell, sharedStorage));
        else captures[symbol] = new(symbol, type, name,
            previous.Location.SourceSpan.Start <= location.SourceSpan.Start ? previous.Location : location,
            previous.MutableCell || mutableCell, previous.SharedStorage || sharedStorage);
    }
    private bool HasWrites(ISymbol symbol)
    {
        if (writtenCells.TryGetValue(symbol, out var written)) return written;
        return writtenCells[symbol] = FindWrites(symbol);
    }
    private bool FindWrites(ISymbol symbol)
    {
        foreach (var operation in ContractSymbols.Walk(root, descendFunctions: true))
        {
            if (!analysis.Step()) return true;
            var referenceAlias = operation switch
            {
                IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None, Initializer: { } initializer } => initializer.Value,
                ISimpleAssignmentOperation { IsRef: true } assignment => assignment.Value,
                _ => null,
            };
            if (referenceAlias is not null && DirectStorage(referenceAlias).Any(slot => SymbolEqualityComparer.Default.Equals(slot, symbol))) return true;
            var target = operation switch
            {
                IAssignmentOperation assignment => assignment.Target,
                IIncrementOrDecrementOperation increment => increment.Target,
                IArgumentOperation argument when argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out => argument.Value,
                IAddressOfOperation address => address.Reference,
                _ => null,
            };
            if (target is null) continue;
            foreach (var slot in DirectStorage(target)) if (SymbolEqualityComparer.Default.Equals(slot, symbol)) return true;
        }
        return false;
    }
    private static IEnumerable<ISymbol> DirectStorage(IOperation target)
    {
        target = ContractSymbols.Unwrap(target);
        if (target is ILocalReferenceOperation local) yield return local.Local;
        else if (target is IParameterReferenceOperation parameter) yield return parameter.Parameter;
        else if (target is ITupleOperation tuple)
            foreach (var element in tuple.Elements) foreach (var symbol in DirectStorage(element)) yield return symbol;
    }
    private static bool DeclaredWithin(ISymbol symbol, SyntaxNode scope) => symbol.DeclaringSyntaxReferences.Any(reference =>
        reference.SyntaxTree == scope.SyntaxTree && scope.Span.Contains(reference.Span));
    private static bool UsesOuterValue(IOperation? instance, SyntaxNode scope) => instance is not null && ContractSymbols.Walk(instance).Any(operation => operation switch
    {
        ILocalReferenceOperation local => !DeclaredWithin(local.Local, scope),
        IParameterReferenceOperation parameter => !DeclaredWithin(parameter.Parameter, scope),
        IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } => true,
        _ => false,
    });
    private static bool WithinNameOf(IOperation operation)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
            if (current is INameOfOperation) return true;
        return false;
    }
}
