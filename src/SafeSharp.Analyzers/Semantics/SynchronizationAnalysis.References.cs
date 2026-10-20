using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.FlowAnalysis;
using SafeSharp.Analyzers.Analysis;

namespace SafeSharp.Analyzers.Semantics;

internal sealed partial class SynchronizationAnalysis
{
    private readonly Dictionary<IOperation, int> operationSlots = new();

    private int OperationSlot(IOperation operation)
    {
        if (!operationSlots.TryGetValue(operation, out var slot)) operationSlots.Add(operation, slot = nextSlot++);
        return slot;
    }

    private GuardStorage Address(IOperation operation, GuardValue evaluated, SynchronizationState state)
    {
        operation = SymbolFacts.Unwrap(operation);
        switch (operation)
        {
            case ILocalReferenceOperation local: return state.Address(Slot(local.Local));
            case IParameterReferenceOperation parameter: return state.Address(Slot(parameter.Parameter));
            case IFlowCaptureReferenceOperation capture:
                return state.CaptureTargets.TryGetValue(Slot(capture.Id), out var captured) ? captured : External(evaluated);
            case IDeclarationExpressionOperation declaration: return Address(declaration.Expression, evaluated, state);
            case IInvocationOperation:
                return state.CaptureTargets.TryGetValue(OperationSlot(operation), out var returned) ? returned : External(evaluated);
            case ISimpleAssignmentOperation { IsRef: true } assignment: return Address(assignment.Target, evaluated, state);
            // An external mutable cell is not a frozen object identity. Keeping its obligations
            // still checks a ref alias to guarded int/struct storage after the lexical lock ends.
            default: return External(evaluated);
        }
        static GuardStorage External(GuardValue value) => new(ImmutableHashSet<int>.Empty,
            new GuardValue(0, value.Obligations));
    }

    private void BindReference(IOperation target, GuardStorage storage, SynchronizationState state)
    {
        if (storage.Targets.Count > 256) throw new AnalysisBudgetException("Synchronization reference target budget exceeded 256.");
        switch (SymbolFacts.Unwrap(target))
        {
            case ILocalReferenceOperation local: state.References[Slot(local.Local)] = storage; break;
            case IParameterReferenceOperation parameter: state.References[Slot(parameter.Parameter)] = storage; break;
            case IFlowCaptureReferenceOperation capture: state.CaptureTargets[Slot(capture.Id)] = storage; break;
        }
    }

    private void AssignArgument(IArgumentOperation argument, GuardValue value, IOperation operation,
        SynchronizationState state, Dictionary<int, GuardStorage> addresses)
    {
        if (argument.Parameter is not { } parameter || !addresses.TryGetValue(parameter.Ordinal, out var storage))
        { Assign(argument.Value, value, operation, state, false); return; }
        Validate(state.Read(storage), argument.Value, state, true);
        if (storage.External is not null)
            Escape(value, argument, state, "a caller-visible reference may retain the protected value", storage.External.Obligations);
        state.Write(storage, value);
    }

    private void PrepareAcquisitionFlag(IInvocationOperation call, GuardValue[] values, SynchronizationState state,
        Dictionary<int, GuardStorage> addresses)
    {
        if (!contracts.IsMonitor(call.TargetMethod) || call.TargetMethod.Name is not ("Enter" or "TryEnter")) return;
        var key = new GuardKey(values.FirstOrDefault()?.Identity ?? 0, GuardLockKind.Monitor);
        var before = state.Get(key);
        var fact = new GuardCondition(key, before, before.Acquire(call.Syntax.GetLocation()));
        foreach (var argument in call.Arguments)
        {
            if (argument.Parameter is not { RefKind: RefKind.Ref, Type.SpecialType: SpecialType.System_Boolean } parameter) continue;
            var implicitFalse = call.IsImplicit && argument.Value is ILocalReferenceOperation { Local.IsImplicitlyDeclared: true };
            if (values[parameter.Ordinal].Boolean != false && !implicitFalse) continue;
            // The compiler's lockTaken=false initialization is omitted from the public CFG.
            // Preserve the same relational fact on failure and success so finally merges can
            // prove: taken => this acquisition. No exception edge receives a guessed success.
            AssignArgument(argument, new GuardValue(0, boolean: false, condition: fact), call, state, addresses);
        }
    }

    private void InvalidateCapturedCells(SynchronizationState state)
    {
        foreach (var symbol in mutableCapturedLocals)
            if (slots.TryGetValue(symbol, out var slot))
            {
                var old = state.Get(slot);
                // A callback can replace the receiver even when its invocation later throws.
                // Protected provenance must survive that uncertainty, while lock identity and
                // saved TryEnter truth cannot survive it.
                state.Assign(slot, new GuardValue(0, old.Obligations));
            }
    }

    private GuardValue ReferenceCallResult(IInvocationOperation call, GuardValue[] values,
        Dictionary<int, GuardStorage> addresses, GuardValue result, SynchronizationState state)
    {
        var summary = contracts.Summaries.Get(call.TargetMethod, cancellationToken);
        GuardStorage? storage = null;
        if (summary.ReferenceReturn is { } returned)
        {
            foreach (var ordinal in returned.ParameterOrdinals)
                if (addresses.TryGetValue(ordinal, out var argument)) storage = storage is null ? argument : storage.Join(argument);
            if (returned.IncludesExternalStorage || returned.IncludesUnknownStorage)
                storage = (storage ?? new GuardStorage(ImmutableHashSet<int>.Empty)).Join(new GuardStorage(ImmutableHashSet<int>.Empty, result));
        }
        else
        {
            storage = new GuardStorage(ImmutableHashSet<int>.Empty, result);
            foreach (var argument in addresses.Values) storage = storage.Join(argument);
        }
        storage ??= new GuardStorage(ImmutableHashSet<int>.Empty, result);
        if (!result.Obligations.IsEmpty)
            storage = storage.Join(new GuardStorage(ImmutableHashSet<int>.Empty, result));
        if (storage.Targets.Count > 256) throw new AnalysisBudgetException("Synchronization reference target budget exceeded 256.");
        state.CaptureTargets[OperationSlot(call)] = storage;
        return state.Read(storage);
    }
}
