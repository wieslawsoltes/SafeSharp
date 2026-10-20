using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Analysis;
using SafeSharp.Core.Regions;

namespace SafeSharp.Analyzers.Semantics;

/// <summary>
/// Finite provenance analysis for lifetime-bound returns. It preserves all possible source origins at
/// control-flow joins and follows only declared or inferred trustworthy result summaries across calls.
/// </summary>
internal sealed class LifetimeReturnFlow
{
    private readonly ControlFlowGraph graph;
    private readonly LifetimeContractModel contract;
    private readonly RegionSolution solution;
    private readonly LifetimeContractProvider contracts;
    private readonly MethodSummaryProvider summaries;
    private readonly RegionSolverOptions options;
    private readonly CancellationToken cancellationToken;
    private readonly int requiredSourceOrdinal;
    private readonly Dictionary<IParameterSymbol, string> capturedParameters = new(SymbolEqualityComparer.Default);
    private int steps;
    private ImmutableHashSet<RegionOrigin>? conditionalReceiver;
    private SourceControlFlowRouter? router;
    private SourceControlFlowRouter.Context? currentContext;
    private Action<SourceControlFlowRouter.Context, OriginState>? propagate;
    private const string ReturnedValue = "$returned-value";

    private LifetimeReturnFlow(ControlFlowGraph graph, LifetimeContractModel contract, RegionSolution solution,
        LifetimeContractProvider contracts, MethodSummaryProvider summaries, RegionSolverOptions options, CancellationToken cancellationToken)
    {
        this.graph = graph;
        this.contract = contract;
        this.solution = solution;
        this.contracts = contracts;
        this.summaries = summaries;
        this.options = options;
        this.cancellationToken = cancellationToken;
        requiredSourceOrdinal = contract.BorrowedSource == "this" ? -1 :
            contract.Method.Parameters.FirstOrDefault(parameter => parameter.Name == contract.BorrowedSource)?.Ordinal ?? -2;
    }

    internal static ImmutableArray<Diagnostic> Analyze(ControlFlowGraph graph, LifetimeContractModel contract, RegionSolution solution,
        LifetimeContractProvider contracts, MethodSummaryProvider summaries, RegionSolverOptions options, CancellationToken cancellationToken) =>
        new LifetimeReturnFlow(graph, contract, solution, contracts, summaries, options, cancellationToken).Run();

    private ImmutableArray<Diagnostic> Run()
    {
        try
        {
            var initial = new OriginState();
            foreach (var parameter in contract.Method.Parameters)
            {
                Step();
                if (parameter.RefKind != RefKind.Out)
                    initial.Values[ParameterKey(parameter)] = One(new RegionOrigin(OriginKind.Parameter, "parameter:" + parameter.Ordinal,
                        parameter.Ordinal, contract.ParameterRegions[parameter.Ordinal], parameter.Locations.FirstOrDefault() ?? Location.None, parameter.Name,
                        sharedBorrow: SymbolFacts.HasAttribute(parameter, "Borrowed") && !SymbolFacts.HasAttribute(parameter, "BorrowedMut")));
            }
            if (!contract.Method.IsStatic)
                initial.Values["this"] = One(new RegionOrigin(OriginKind.Receiver, "this", -1, "this", contract.Method.Locations.FirstOrDefault() ?? Location.None, "this"));
            router = new SourceControlFlowRouter(graph);
            var inputs = new Dictionary<int, OriginState>();
            var queue = new Queue<int>();
            var queued = new HashSet<int>();
            propagate = (context, incoming) =>
            {
                if (router.IsBoundary(context)) return;
                var joined = inputs.TryGetValue(context.Id, out var previous) ? Join(new[] { previous, incoming }) : CloneState(incoming);
                if (previous is not null && StatesEqual(previous, joined)) return;
                inputs[context.Id] = joined;
                if (queued.Add(context.Id)) queue.Enqueue(context.Id);
            };
            propagate(router.Entry, initial);
            while (queue.Count > 0)
            {
                Step();
                var id = queue.Dequeue(); queued.Remove(id);
                currentContext = router[id];
                var block = graph.Blocks[currentContext.Ordinal];
                var state = CloneState(inputs[id]);
                foreach (var operation in block.Operations) Evaluate(operation, state, 0);
                if (block.BranchValue is { } branch)
                {
                    var origins = Evaluate(branch, state, 0);
                    if (block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return ||
                        block.ConditionalSuccessor?.Semantics == ControlFlowBranchSemantics.Return)
                        state.Values[ReturnedValue] = origins;
                }
                foreach (var outgoing in SourceControlFlowRouter.Branches(block))
                    foreach (var successor in router.Successors(currentContext, outgoing)) propagate(successor, state);
            }
            currentContext = null;

            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            foreach (var pair in inputs)
            {
                Step();
                var context = router[pair.Key];
                if (!router.IsNormalExit(context) || context.ReturnOperation is not { } returned) continue;
                var state = pair.Value;
                var origins = state.Values.TryGetValue(ReturnedValue, out var captured) ? captured : Unknown(returned, "missing return snapshot");
                origins = origins.SelectMany(origin => state.InvalidOrigins.Contains(origin.Key)
                    ? Unknown(returned, "a result invalidated by disposal or consumption before normal completion") : One(origin)).ToImmutableHashSet();
                if (!contract.Method.IsAsync && IsFrameworkAwaitable(contract.Method.ReturnType))
                    origins = AwaitedOrigins(origins, returned);
                var location = ReturnLocation(returned);
                Step(origins.Count);
                var invalid = origins.Where(origin => !Satisfies(origin)).OrderBy(origin => origin.Key, StringComparer.Ordinal).ToArray();
                if (invalid.Length == 0) continue;
                Step(contract.RegionNames.Length + contract.Constraints.Length + contract.DeclarationLocations.Length);
                var message = string.Join("; ", invalid.Select(DescribeViolation).Distinct(StringComparer.Ordinal));
                var properties = ImmutableDictionary<string, string?>.Empty
                    .Add("SafeSharp.RequiredRegion", contract.ReturnRegion)
                    .Add("SafeSharp.ReturnBorrowedSource", contract.BorrowedSource)
                    .Add("SafeSharp.RegionNames", string.Join(", ", solution.Components.SelectMany(component => component.Members).OrderBy(name => name, StringComparer.Ordinal)))
                    .Add("SafeSharp.RegionComponents", string.Join("; ", solution.Components.Select(component => string.Join(", ", component.Members))))
                    .Add("SafeSharp.SourceRegions", string.Join(", ", origins.Select(origin => origin.Region ?? "<" + origin.Kind + ">").Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal)))
                    .Add("SafeSharp.RegionConstraints", string.Join("; ", contract.Constraints.Select(constraint => constraint.Longer + " : " + constraint.Shorter)))
                    .Add("SafeSharp.RegionProof", string.Join("; ", origins.Where(origin => origin.Region is not null && contract.ReturnRegion is not null)
                        .Select(origin => solution.GetProof(origin.Region!, contract.ReturnRegion!)).Where(proof => proof is not null)
                        .Select(proof => proof!.IsReflexive ? proof.Longer + " : " + proof.Shorter + " (reflexive)" : string.Join(" -> ", proof.Steps.Select(edge => edge.Longer).Concat(new[] { proof.Shorter })))));
                var related = origins.Select(origin => origin.Location).Concat(contract.DeclarationLocations)
                    .Where(site => site.IsInSource && site != location).Distinct();
                diagnostics.Add(Diagnostic.Create(LifetimeContractAnalyzer.ReturnViolation, location, related, properties,
                    contract.Method.Name, message));
            }
            return diagnostics.GroupBy(diagnostic => diagnostic.Id + ":" + diagnostic.Location.SourceSpan + ":" + diagnostic.GetMessage(), StringComparer.Ordinal)
                .Select(group => group.First()).OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start).ThenBy(diagnostic => diagnostic.GetMessage(), StringComparer.Ordinal).ToImmutableArray();
        }
        catch (Exception error) when (error is LifetimeFlowBudgetException or AnalysisBudgetException)
        {
            return ImmutableArray.Create(Diagnostic.Create(LifetimeContractAnalyzer.BudgetExceeded, contract.Method.Locations.FirstOrDefault(),
                contract.Method.Name, "return-provenance steps/depth"));
        }
    }

    private void Fault(OriginState state)
    {
        if (router is null || currentContext is null || propagate is null || !router.HasExceptionalTarget(currentContext)) return;
        foreach (var successor in router.Exceptions(currentContext)) propagate(successor, state);
    }

    private static void Invalidate(OriginState state, ImmutableHashSet<RegionOrigin> origins)
    {
        foreach (var origin in origins)
            if (origin.Kind != OriginKind.Null) state.InvalidOrigins = state.InvalidOrigins.Add(origin.Key);
    }

    private ImmutableHashSet<RegionOrigin> Evaluate(IOperation operation, OriginState state, int depth)
    {
        Step();
        if (depth > 256) throw new LifetimeFlowBudgetException();
        switch (operation)
        {
            case IConversionOperation conversion:
            {
                var operand = Evaluate(conversion.Operand, state, depth + 1);
                if (conversion.OperatorMethod is not { } operatorMethod)
                {
                    if (!conversion.IsImplicit && !conversion.IsTryCast) Fault(state);
                    return operand;
                }
                // User-defined conversion executes arbitrary code. Only its declared/inferred
                // returned-source summary can establish the result's lifetime, never the cast syntax.
                if (conversion.Operand.Type?.TypeKind == TypeKind.Delegate) InvalidateCapturedStorage(state, conversion);
                Fault(state);
                return ResolveCall(conversion, operatorMethod, Unknown(conversion, "static conversion"),
                    new Dictionary<int, ImmutableHashSet<RegionOrigin>> { [0] = operand });
            }
            case IParenthesizedOperation parenthesized: return Evaluate(parenthesized.Operand, state, depth + 1);
            case IParameterReferenceOperation parameter: return Read(state, ParameterKey(parameter.Parameter), operation);
            case ILocalReferenceOperation local: return Read(state, LocalKey(local.Local), operation);
            case IInstanceReferenceOperation: return Read(state, "this", operation);
            case IFlowCaptureReferenceOperation capture: return Read(state, CaptureKey(capture.Id), operation);
            case IFlowCaptureOperation capture:
            {
                var origins = Evaluate(capture.Value, state, depth + 1);
                state.Values[CaptureKey(capture.Id)] = origins;
                state.CaptureTargets[CaptureKey(capture.Id)] = ArgumentTargets(state, capture.Value);
                return origins;
            }
            case IVariableDeclaratorOperation declaration:
            {
                var origins = declaration.Initializer is { } initializer ? Evaluate(initializer.Value, state, depth + 1) : Unknown(operation, "uninitialized local");
                if (declaration.Symbol.RefKind != RefKind.None && declaration.Initializer is { } referenceInitializer)
                    BindReference(state, LocalKey(declaration.Symbol), referenceInitializer.Value);
                state.Values[LocalKey(declaration.Symbol)] = origins;
                return origins;
            }
            case ISimpleAssignmentOperation assignment:
            {
                var key = Key(assignment.Target);
                ImmutableHashSet<string>? addressed = assignment.Target is IFlowCaptureReferenceOperation ? ArgumentTargets(state, assignment.Target) : null;
                // Receiver/index expressions designate the storage before the RHS executes.
                // They may themselves assign locals or invoke callbacks with observable effects.
                if (key is null)
                {
                    if (assignment.Target is IInvocationOperation { TargetMethod.ReturnsByRef: true } or IPropertyReferenceOperation { Property.ReturnsByRef: true })
                    {
                        Evaluate(assignment.Target, state, depth + 1);
                        addressed = ArgumentTargets(state, assignment.Target);
                    }
                    else foreach (var child in assignment.Target.ChildOperations) Evaluate(child, state, depth + 1);
                }
                var origins = Evaluate(assignment.Value, state, depth + 1);
                if (key is not null)
                {
                    if (assignment.IsRef) BindReference(state, key, assignment.Value);
                    else if (addressed is not null) Store(state, addressed, origins);
                    else Store(state, key, origins);
                }
                else if (addressed is not null) Store(state, addressed, origins);
                return origins;
            }
            case IAssignmentOperation assignment:
            {
                Evaluate(assignment.Target, state, depth + 1);
                Evaluate(assignment.Value, state, depth + 1);
                var origins = Unknown(operation, "compound or deconstruction assignment has no proven result origin");
                StoreTargets(state, assignment.Target, origins);
                return origins;
            }
            case IIncrementOrDecrementOperation increment:
            {
                Evaluate(increment.Target, state, depth + 1);
                var origins = Unknown(operation, "increment or decrement has no proven result origin");
                StoreTargets(state, increment.Target, origins);
                return origins;
            }
            case IInvocationOperation invocation: return Invoke(invocation, state, depth + 1);
            case IPropertyReferenceOperation property:
            {
                var receiver = property.Instance is null ? Unknown(operation, "static property") : Evaluate(property.Instance, state, depth + 1);
                var arguments = new Dictionary<int, ImmutableHashSet<RegionOrigin>>();
                foreach (var argument in property.Arguments)
                {
                    var value = Evaluate(argument.Value, state, depth + 1);
                    if (argument.Parameter is { } parameter) arguments[parameter.Ordinal] = value;
                }
                if (property.Arguments.Any(argument => argument.Value.Type?.TypeKind == TypeKind.Delegate)) InvalidateCapturedStorage(state, property);
                Fault(state);
                if (IsKnownReceiverView(property.Property)) return receiver;
                if (property.Property.GetMethod is { } getter)
                    return ResolveCall(property, getter, receiver, arguments);
                return Unknown(operation, "property has no trusted borrowing relation");
            }
            case IFieldReferenceOperation field:
            {
                var receiver = field.Instance is null ? Unknown(operation, "static field") : Evaluate(field.Instance, state, depth + 1);
                Fault(state);
                return SymbolFacts.HasAttribute(field.Field, "Owned") ? receiver : Unknown(operation, "field has no declared owner relation");
            }
            case IConditionalOperation conditional:
            {
                Evaluate(conditional.Condition, state, depth + 1);
                if (conditional.Condition.ConstantValue is { HasValue: true, Value: bool condition })
                    return condition ? Evaluate(conditional.WhenTrue, state, depth + 1) : conditional.WhenFalse is { } otherwise ? Evaluate(otherwise, state, depth + 1) : Unknown(operation, "missing conditional value");
                return MergeOrigins(Evaluate(conditional.WhenTrue, state, depth + 1), conditional.WhenFalse is { } alternate ? Evaluate(alternate, state, depth + 1) : Unknown(operation, "missing conditional value"));
            }
            case ICoalesceOperation coalesce: return MergeOrigins(Evaluate(coalesce.Value, state, depth + 1), Evaluate(coalesce.WhenNull, state, depth + 1));
            case IConditionalAccessOperation conditional:
            {
                var saved = conditionalReceiver;
                conditionalReceiver = Evaluate(conditional.Operation, state, depth + 1);
                var result = Evaluate(conditional.WhenNotNull, state, depth + 1);
                conditionalReceiver = saved;
                return MergeOrigins(result, One(RegionOrigin.Null));
            }
            case IConditionalAccessInstanceOperation: return conditionalReceiver ?? Unknown(operation, "unknown conditional receiver");
            case IAwaitOperation awaited:
            {
                var value = Evaluate(awaited.Operation, state, depth + 1);
                Fault(state);
                // Carrier lifetime does not establish GetResult's payload lifetime. The marker
                // comes only from a modeled producer or an explicit effective-result promise.
                return IsFrameworkAwaitable(awaited.Operation.Type, includeConfigured: true)
                    ? AwaitedOrigins(value, awaited) : Unknown(awaited, "custom awaiter has no modeled result lifetime");
            }
            case IObjectCreationOperation creation:
            {
                var replacements = new List<(ImmutableHashSet<string> Targets, IOperation Argument)>();
                ImmutableHashSet<RegionOrigin>? singleArgument = null;
                foreach (var argument in creation.Arguments)
                {
                    var value = Evaluate(argument.Value, state, depth + 1);
                    if (creation.Arguments.Length == 1) singleArgument = value;
                    if (argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out)
                        replacements.Add((ArgumentTargets(state, argument.Value), argument));
                }
                foreach (var replacement in replacements)
                    Store(state, replacement.Targets, Unknown(replacement.Argument, "constructor ref/out argument may replace the value"));
                if (creation.Arguments.Any(argument => argument.Value.Type?.TypeKind == TypeKind.Delegate))
                    InvalidateCapturedStorage(state, creation);
                Fault(state);
                if (creation.Initializer is { } objectInitializer) Evaluate(objectInitializer, state, depth + 1);
                if (creation.Type is INamedTypeSymbol { Name: "ValueTask", Arity: 1 } valueTask &&
                    valueTask.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" && singleArgument is not null &&
                    creation.Constructor is { Parameters.Length: 1 } constructor)
                {
                    if (SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, valueTask.TypeArguments[0]))
                        return WrapAwaitable(singleArgument);
                    if (IsFrameworkAwaitable(constructor.Parameters[0].Type)) return singleArgument;
                }
                return Local(operation, "locally created owner");
            }
            case IArrayCreationOperation array:
                foreach (var child in array.ChildOperations) Evaluate(child, state, depth + 1);
                Fault(state);
                return Local(operation, "locally created array");
            case ITupleOperation tuple: return Union(tuple.Elements.Select(element => Evaluate(element, state, depth + 1)), operation);
            case ILiteralOperation or IDefaultValueOperation when operation.ConstantValue is { HasValue: true, Value: null }: return One(RegionOrigin.Null);
            case IAnonymousFunctionOperation or ILocalFunctionOperation: return Unknown(operation, "delegate or local-function value");
            case IReturnOperation returned when returned.ReturnedValue is not null: return Evaluate(returned.ReturnedValue, state, depth + 1);
            default:
                foreach (var child in operation.ChildOperations) Evaluate(child, state, depth + 1);
                return Unknown(operation, "unmodeled value origin");
        }
    }

    private ImmutableHashSet<RegionOrigin> Invoke(IInvocationOperation invocation, OriginState state, int depth)
    {
        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        var receiver = invocation.Instance is null ? Unknown(invocation, "no receiver") : Evaluate(invocation.Instance, state, depth + 1);
        var arguments = new Dictionary<int, ImmutableHashSet<RegionOrigin>>();
        var referenceArguments = new Dictionary<int, ImmutableHashSet<string>>();
        var summary = summaries.Get(method, cancellationToken);
        var replacements = new List<(ImmutableHashSet<string> Targets, IOperation Argument, int? Ordinal)>();
        var offset = invocation.TargetMethod.ReducedFrom is null ? 0 : 1;
        if (offset == 1) arguments[0] = receiver;
        foreach (var argument in invocation.Arguments)
        {
            var value = Evaluate(argument.Value, state, depth + 1);
            if (argument.Parameter is { } parameter) arguments[parameter.Ordinal + offset] = value;
            if (argument.Parameter is { RefKind: not RefKind.None } referenceParameter)
            {
                var targets = ArgumentTargets(state, argument.Value);
                referenceArguments[referenceParameter.Ordinal + offset] = targets;
                if (referenceParameter.RefKind is RefKind.Out or RefKind.Ref && !summary.PreservedRefParameters.Contains(referenceParameter.Ordinal + offset))
                    replacements.Add((targets, argument, referenceParameter.Ordinal + offset));
            }
        }
        // C# evaluates every argument before entering the callee. A later argument assignment
        // must not overwrite the unknown postcondition of an earlier ref/out argument.
        foreach (var replacement in replacements)
        {
            var replaced = Unknown(replacement.Argument, "ref/out call may replace the value");
            if (replacement.Ordinal is { } ordinal) arguments[ordinal] = replaced;
            Store(state, replacement.Targets, replaced);
        }
        if (MayRunCapturedCode(invocation)) InvalidateCapturedStorage(state, invocation);
        if (SymbolFacts.IsDispose(method) || SymbolFacts.IsDisposeAsync(method)) Invalidate(state, receiver);
        foreach (var parameter in method.Parameters)
            if ((summary.DisposedParameters.Contains(parameter.Ordinal) || parameter.RefKind != RefKind.Out && (SymbolFacts.HasAttribute(parameter, "Consumes") || SymbolFacts.HasAttribute(parameter, "Owned"))) &&
                arguments.TryGetValue(parameter.Ordinal, out var consumed)) Invalidate(state, consumed);
        if (router is not null && currentContext is not null && router.HasExceptionalTarget(currentContext))
        {
            var exceptional = CloneState(state);
            foreach (var argument in invocation.Arguments)
                if (argument.Parameter is { RefKind: RefKind.Ref or RefKind.Out } parameter && referenceArguments.TryGetValue(parameter.Ordinal + offset, out var targets))
                    Store(exceptional, targets, Unknown(argument, "exceptional ref/out write"));
            Fault(exceptional);
        }
        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            var targets = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            if (summary.ReferenceReturn is { } returned)
            {
                foreach (var ordinal in returned.ParameterOrdinals)
                    targets = targets.Union(referenceArguments.TryGetValue(ordinal, out var actual) ? actual : ImmutableHashSet.Create(StringComparer.Ordinal, UnknownTarget));
                if (returned.IncludesUnknownStorage || returned.IncludesExternalStorage) targets = targets.Add(UnknownTarget);
            }
            if (targets.IsEmpty) targets = targets.Add(UnknownTarget);
            state.ReferenceResults[ReferenceKey(invocation)] = targets;
            return Union(targets.Select(target => state.Values.TryGetValue(target, out var origin) ? origin : Unknown(invocation, "unknown ref-return storage")), invocation);
        }
        if (method.ContainingType.ToDisplayString() == "SafeSharp.Ownership" && method.Name is "Borrow" or "BorrowMut" && arguments.TryGetValue(0, out var borrowed))
            return method.Name == "Borrow" ? SharedBorrow(borrowed) : borrowed;
        if (method.Name == "FromResult" && method.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" &&
            method.ContainingType is { Arity: 0, Name: "Task" or "ValueTask" } && IsFrameworkAwaitable(method.ReturnType) &&
            arguments.Count == 1 && arguments.TryGetValue(0, out var payload)) return WrapAwaitable(payload);
        if ((method.Name is "ConfigureAwait" or "AsTask") && IsFrameworkAwaitable(method.ContainingType) && invocation.Instance is not null)
            return receiver;
        return ResolveCall(invocation, method, receiver, arguments);
    }

    private ImmutableHashSet<RegionOrigin> ResolveCall(IOperation operation, IMethodSymbol method,
        ImmutableHashSet<RegionOrigin> receiver, IReadOnlyDictionary<int, ImmutableHashSet<RegionOrigin>> arguments)
    {
        var called = contracts.Get(method, cancellationToken);
        if (called.ReturnsOwned) return Local(operation, "owned factory result");
        if (!called.DeclarationIssues.IsEmpty) return Unknown(operation, "callee lifetime declaration is invalid");
        var source = called.BorrowedSource;
        ImmutableHashSet<RegionOrigin> ExplicitResult(ImmutableHashSet<RegionOrigin> value)
        {
            // A shared result contract narrows access even if its owner was exclusive. An
            // exclusive result cannot erase a shared restriction already present at the input.
            var result = called.MutableBorrow ? value : SharedBorrow(value);
            return IsFrameworkAwaitable(method.ReturnType) ? WrapAwaitable(result) : result;
        }
        if (source == "this") return ExplicitResult(receiver);
        if (source is not null && method.Parameters.FirstOrDefault(parameter => parameter.Name == source) is { } input && arguments.TryGetValue(input.Ordinal, out var origins)) return ExplicitResult(origins);
        if (called.ReturnRegion is { } returned)
        {
            var relation = called.Solve(options, cancellationToken);
            if (!relation.IsComplete) throw new LifetimeFlowBudgetException();
            var candidates = new List<ImmutableHashSet<RegionOrigin>>();
            for (var parameter = 0; parameter < called.ParameterRegions.Length; parameter++)
                if (called.ParameterRegions[parameter] is { } region && relation.Outlives(region, returned) && arguments.TryGetValue(parameter, out var value)) candidates.Add(value);
            if (!method.IsStatic && relation.Outlives("this", returned)) candidates.Add(receiver);
            return ExplicitResult(Union(candidates, operation));
        }
        // Explicit signatures already determine provenance. Source-body summary inference is
        // needed only for boundaries without a returned-source/lifetime contract.
        var summary = summaries.Get(method, cancellationToken);
        if (!summary.ReturnOrigins.IsEmpty)
        {
            var results = new List<ImmutableHashSet<RegionOrigin>>();
            foreach (var origin in summary.ReturnOrigins)
            {
                var value = origin.Source == MethodOriginAnalysis.Fresh ? Local(operation, "owned factory result") :
                    origin.Source == MethodOriginAnalysis.Receiver ? receiver : origin.Source == MethodOriginAnalysis.Null ? One(RegionOrigin.Null) :
                    arguments.FirstOrDefault(pair => MethodOriginAnalysis.Parameter(pair.Key) == origin.Source).Value ?? Unknown(operation, "unproven result origin");
                for (var layer = 0; layer < origin.PayloadDepth; layer++) value = WrapAwaitable(value);
                results.Add(value);
            }
            return Union(results, operation);
        }
        if (summary.ReturnsOwned) return Local(operation, "owned factory result");
        if (!summary.BorrowedSources.IsEmpty)
        {
            var candidates = summary.BorrowedSources.Select(candidate => candidate == "this" ? receiver :
                method.Parameters.FirstOrDefault(parameter => parameter.Name == candidate) is { } parameter && arguments.TryGetValue(parameter.Ordinal, out var value)
                    ? value : Unknown(operation, "returned source could not be mapped to an argument"));
            var inferred = Union(candidates, operation);
            return summary.ReturnsAsyncPayload && summaries.AsyncResults.IsCarrier(method.ReturnType) ? WrapAwaitable(inferred) : inferred;
        }
        return Unknown(operation, "call has no trusted returned-source contract");
    }

    private static bool IsFrameworkAwaitable(ITypeSymbol? type, bool includeConfigured = false) => type is INamedTypeSymbol { Arity: 1 } named &&
        (named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" && (named.Name is "Task" or "ValueTask") ||
         includeConfigured && named.ContainingNamespace.ToDisplayString() == "System.Runtime.CompilerServices" &&
             (named.Name is "ConfiguredTaskAwaitable" or "ConfiguredValueTaskAwaitable"));

    private ImmutableHashSet<RegionOrigin> WrapAwaitable(ImmutableHashSet<RegionOrigin> origins)
    {
        Step(origins.Count);
        var wrapped = ImmutableHashSet.CreateBuilder<RegionOrigin>();
        foreach (var origin in origins)
        {
            if (origin.AwaitableDepth >= 256) throw new LifetimeFlowBudgetException();
            wrapped.Add(origin.WithAwaitableDepth(origin.AwaitableDepth + 1));
        }
        return wrapped.ToImmutable();
    }

    private ImmutableHashSet<RegionOrigin> AwaitedOrigins(ImmutableHashSet<RegionOrigin> origins, IOperation operation)
    {
        Step(origins.Count);
        var result = ImmutableHashSet.CreateBuilder<RegionOrigin>();
        foreach (var origin in origins)
        {
            if (origin.AwaitableDepth > 0) result.Add(origin.WithAwaitableDepth(origin.AwaitableDepth - 1));
            else if (origin.Kind == OriginKind.Null) result.Add(origin);
            else result.UnionWith(Unknown(operation, "awaitable payload has no proven result lifetime"));
        }
        return result.ToImmutable();
    }

    private bool Satisfies(RegionOrigin origin)
    {
        if (origin.Kind == OriginKind.Null) return true;
        if (origin.Kind is OriginKind.Local or OriginKind.Unknown) return false;
        if (contract.MutableBorrow && origin.SharedBorrow) return false;
        if (contract.BorrowedSource is not null && origin.ParameterOrdinal != requiredSourceOrdinal) return false;
        return contract.ReturnRegion is null || origin.Region is { } region && solution.Outlives(region, contract.ReturnRegion);
    }

    private string DescribeViolation(RegionOrigin origin) => origin.Kind switch
    {
        OriginKind.Local => origin.Description + " cannot establish a caller-visible borrowed lifetime",
        OriginKind.Unknown => "the lifetime of " + origin.Description + " is not established",
        _ when contract.MutableBorrow && origin.SharedBorrow
            => "a shared borrow from '" + origin.Description + "' cannot establish an exclusive returned borrow",
        _ when contract.BorrowedSource is { } source && origin.ParameterOrdinal != requiredSourceOrdinal
            => "source '" + origin.Description + "' differs from promised borrowed source '" + source + "'",
        _ => "region '" + (origin.Region ?? "<undeclared>") + "' is not known to outlive '" + contract.ReturnRegion + "'",
    };

    private OriginState Join(IReadOnlyList<OriginState> incoming)
    {
        if (incoming.Count == 1) return CloneState(incoming[0]);
        foreach (var state in incoming) Step(state.Values.Count + state.RefTargets.Count);
        var joined = new OriginState();
        foreach (var key in incoming.SelectMany(state => state.Values.Keys).Distinct(StringComparer.Ordinal))
        {
            Step();
            var origins = ImmutableHashSet<RegionOrigin>.Empty;
            foreach (var state in incoming) origins = MergeOrigins(origins, state.Values.TryGetValue(key, out var values) ? values : One(RegionOrigin.Missing));
            joined.Values[key] = origins;
        }
        foreach (var key in incoming.SelectMany(state => state.RefTargets.Keys).Distinct(StringComparer.Ordinal))
        {
            Step();
            var targets = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            foreach (var state in incoming)
            {
                var next = state.RefTargets.TryGetValue(key, out var known) ? known : ImmutableHashSet.Create(StringComparer.Ordinal, UnknownTarget);
                Step(targets.Count + next.Count);
                targets = targets.Union(next);
            }
            joined.RefTargets[key] = targets;
        }
        foreach (var state in incoming) joined.InvalidOrigins = joined.InvalidOrigins.Union(state.InvalidOrigins);
        JoinTargetMap(incoming.Select(state => state.CaptureTargets).ToArray(), joined.CaptureTargets);
        JoinTargetMap(incoming.Select(state => state.ReferenceResults).ToArray(), joined.ReferenceResults);
        return joined;
    }

    private void JoinTargetMap(IReadOnlyList<Dictionary<string, ImmutableHashSet<string>>> incoming, Dictionary<string, ImmutableHashSet<string>> joined)
    {
        foreach (var key in incoming.SelectMany(map => map.Keys).Distinct(StringComparer.Ordinal))
        {
            var targets = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            foreach (var map in incoming)
            {
                Step();
                targets = targets.Union(map.TryGetValue(key, out var known) ? known : ImmutableHashSet.Create(StringComparer.Ordinal, UnknownTarget));
            }
            joined[key] = targets;
        }
    }

    private static bool MayRunCapturedCode(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (method.ContainingType.ToDisplayString() == "SafeSharp.Ownership" && (method.Name is "Move" or "Borrow" or "BorrowMut")) return false;
        return method.MethodKind == MethodKind.DelegateInvoke ||
            method.MethodKind == MethodKind.LocalFunction && !method.IsStatic ||
            invocation.Arguments.Any(argument => argument.Value.Type?.TypeKind == TypeKind.Delegate);
    }

    private void InvalidateCapturedStorage(OriginState state, IOperation operation)
    {
        Step(state.Values.Count);
        foreach (var key in state.Values.Keys.ToArray())
        {
            Step();
            state.Values[key] = Unknown(operation, "captured storage may be replaced by invoked code");
        }
    }

    private void StoreTargets(OriginState state, IOperation target, ImmutableHashSet<RegionOrigin> origins)
    {
        foreach (var place in AssignmentTargets(target))
            if (Key(place) is { } key) Store(state, key, origins);
    }

    private IEnumerable<IOperation> AssignmentTargets(IOperation target)
    {
        var pending = new Stack<IOperation>();
        pending.Push(target);
        while (pending.Count > 0)
        {
            Step();
            var current = SymbolFacts.Unwrap(pending.Pop());
            if (current is ITupleOperation tuple)
                foreach (var element in tuple.Elements) pending.Push(element);
            else if (current is IDeclarationExpressionOperation declaration) pending.Push(declaration.Expression);
            else yield return current;
        }
    }

    private static Location ReturnLocation(IOperation operation)
    {
        var syntax = operation.Syntax;
        foreach (var ancestor in syntax.AncestorsAndSelf())
        {
            if (ancestor is ReturnStatementSyntax { Expression: { } expression }) return expression.GetLocation();
            if (ancestor is LambdaExpressionSyntax { Body: ExpressionSyntax body }) return body.GetLocation();
            if (ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) break;
            if (ancestor is ArrowExpressionClauseSyntax arrow) return arrow.Expression.GetLocation();
        }
        return syntax.GetLocation();
    }

    private string ParameterKey(IParameterSymbol parameter)
    {
        if (SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol.OriginalDefinition, contract.Method.OriginalDefinition))
            return "parameter:" + parameter.Ordinal;
        // A local function's captured outer parameter can have the same ordinal as its own input.
        // Distinct symbol-keyed slots prevent a capture from silently acquiring the input's region.
        parameter = parameter.OriginalDefinition;
        if (!capturedParameters.TryGetValue(parameter, out var key))
        {
            key = "captured-parameter:" + capturedParameters.Count;
            capturedParameters.Add(parameter, key);
        }
        return key;
    }
    private static string LocalKey(ILocalSymbol local) => "local:" + local.Name + ":" + local.Locations.FirstOrDefault()?.SourceSpan.Start;
    private static string CaptureKey(CaptureId capture) => "capture:" + capture.GetHashCode();
    private string? Key(IOperation operation) => SymbolFacts.Unwrap(operation) switch
    {
        IParameterReferenceOperation parameter => ParameterKey(parameter.Parameter),
        ILocalReferenceOperation local => LocalKey(local.Local),
        IFlowCaptureReferenceOperation capture => CaptureKey(capture.Id),
        IDeclarationExpressionOperation declaration => Key(declaration.Expression),
        _ => null,
    };
    private const string UnknownTarget = "$unknown-reference-target";
    private ImmutableHashSet<RegionOrigin> Read(OriginState state, string key, IOperation operation) =>
        Union(Targets(state, key).Select(target => state.Values.TryGetValue(target, out var value) ? value : Unknown(operation, "value without known provenance")), operation);

    private static ImmutableHashSet<string> Targets(OriginState state, string key) => state.RefTargets.TryGetValue(key, out var targets)
        ? targets : ImmutableHashSet.Create(StringComparer.Ordinal, key);

    private void BindReference(OriginState state, string target, IOperation source) =>
        state.RefTargets[target] = ArgumentTargets(state, source);

    private static string ReferenceKey(IOperation operation) => "$reference-result:" + operation.Syntax.SpanStart;

    private ImmutableHashSet<string> ArgumentTargets(OriginState state, IOperation argument)
    {
        argument = SymbolFacts.Unwrap(argument);
        if (argument is IInvocationOperation { TargetMethod.ReturnsByRef: true } or IInvocationOperation { TargetMethod.ReturnsByRefReadonly: true } or
            IPropertyReferenceOperation { Property.ReturnsByRef: true } or IPropertyReferenceOperation { Property.ReturnsByRefReadonly: true })
            return state.ReferenceResults.TryGetValue(ReferenceKey(argument), out var result) ? result : ImmutableHashSet.Create(StringComparer.Ordinal, UnknownTarget);
        var key = Key(argument);
        if (key is not null && argument is IFlowCaptureReferenceOperation)
            return state.CaptureTargets.TryGetValue(key, out var captured) ? captured : ImmutableHashSet.Create(StringComparer.Ordinal, UnknownTarget);
        return key is null ? ImmutableHashSet.Create(StringComparer.Ordinal, UnknownTarget) : Targets(state, key);
    }

    private void Store(OriginState state, string key, ImmutableHashSet<RegionOrigin> origins) => Store(state, Targets(state, key), origins);

    private void Store(OriginState state, ImmutableHashSet<string> targets, ImmutableHashSet<RegionOrigin> origins)
    {
        if (targets.Contains(UnknownTarget))
        {
            Step(state.Values.Count);
            foreach (var tracked in state.Values.Keys.ToArray())
            {
                Step(state.Values[tracked].Count);
                state.Values[tracked] = state.Values[tracked].Add(RegionOrigin.Missing);
            }
            return;
        }
        foreach (var target in targets)
        {
            Step();
            state.Values[target] = targets.Count > 1 && state.Values.TryGetValue(target, out var previous) ? MergeOrigins(previous, origins) : origins;
        }
    }
    private ImmutableHashSet<RegionOrigin> Union(IEnumerable<ImmutableHashSet<RegionOrigin>> sets, IOperation operation)
    {
        var combined = sets.Aggregate(ImmutableHashSet<RegionOrigin>.Empty, MergeOrigins);
        return combined.IsEmpty ? Unknown(operation, "result has no input lifetime bound") : combined;
    }
    private ImmutableHashSet<RegionOrigin> MergeOrigins(ImmutableHashSet<RegionOrigin> first, ImmutableHashSet<RegionOrigin> second)
    {
        Step(first.Count + second.Count + 1);
        return first.Union(second);
    }
    private static ImmutableHashSet<RegionOrigin> One(RegionOrigin origin) => ImmutableHashSet.Create(origin);
    private ImmutableHashSet<RegionOrigin> SharedBorrow(ImmutableHashSet<RegionOrigin> origins)
    {
        Step(origins.Count);
        return origins.Select(origin => origin.WithSharedBorrow()).ToImmutableHashSet();
    }
    private static ImmutableHashSet<RegionOrigin> Unknown(IOperation operation, string description) => One(new RegionOrigin(OriginKind.Unknown,
        "unknown:" + operation.Syntax.SpanStart + ":" + description, -2, null, operation.Syntax.GetLocation(), description));
    private static ImmutableHashSet<RegionOrigin> Local(IOperation operation, string description) => One(new RegionOrigin(OriginKind.Local,
        "local:" + operation.Syntax.SpanStart, -2, null, operation.Syntax.GetLocation(), description));

    private void Step(int count = 1)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count > options.MaxSteps - steps) throw new LifetimeFlowBudgetException();
        steps += count;
    }

    private OriginState CloneState(OriginState state)
    {
        Step(state.Values.Count + state.RefTargets.Count);
        return state.Clone();
    }

    private bool StatesEqual(OriginState first, OriginState second)
    {
        foreach (var value in first.Values.Values) Step(value.Count + 1);
        foreach (var targets in first.RefTargets.Values) Step(targets.Count + 1);
        return first.Equals(second);
    }

    private bool IsKnownReceiverView(IPropertySymbol property) => SymbolFacts.IsKnownReceiverView(property, () => Step());

    private sealed class OriginState : IEquatable<OriginState>
    {
        internal Dictionary<string, ImmutableHashSet<RegionOrigin>> Values { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, ImmutableHashSet<string>> RefTargets { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, ImmutableHashSet<string>> CaptureTargets { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, ImmutableHashSet<string>> ReferenceResults { get; } = new(StringComparer.Ordinal);
        internal ImmutableHashSet<string> InvalidOrigins { get; set; } = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        internal OriginState Clone()
        {
            var copy = new OriginState();
            foreach (var pair in Values) copy.Values.Add(pair.Key, pair.Value);
            foreach (var pair in RefTargets) copy.RefTargets.Add(pair.Key, pair.Value);
            foreach (var pair in CaptureTargets) copy.CaptureTargets.Add(pair.Key, pair.Value);
            foreach (var pair in ReferenceResults) copy.ReferenceResults.Add(pair.Key, pair.Value);
            copy.InvalidOrigins = InvalidOrigins;
            return copy;
        }
        public bool Equals(OriginState? other) => other is not null && Values.Count == other.Values.Count && RefTargets.Count == other.RefTargets.Count &&
            Values.All(pair => other.Values.TryGetValue(pair.Key, out var value) && pair.Value.SetEquals(value)) &&
            RefTargets.All(pair => other.RefTargets.TryGetValue(pair.Key, out var value) && pair.Value.SetEquals(value)) &&
            CaptureTargets.Count == other.CaptureTargets.Count && CaptureTargets.All(pair => other.CaptureTargets.TryGetValue(pair.Key, out var value) && pair.Value.SetEquals(value)) &&
            ReferenceResults.Count == other.ReferenceResults.Count && ReferenceResults.All(pair => other.ReferenceResults.TryGetValue(pair.Key, out var value) && pair.Value.SetEquals(value)) &&
            InvalidOrigins.SetEquals(other.InvalidOrigins);
    }

    private enum OriginKind { Parameter, Receiver, Local, Unknown, Null }

    private sealed class RegionOrigin : IEquatable<RegionOrigin>
    {
        internal static readonly RegionOrigin Null = new(OriginKind.Null, "null", -2, null, Location.None, "null");
        internal static readonly RegionOrigin Missing = new(OriginKind.Unknown, "missing", -2, null, Location.None, "a value missing from one incoming path");
        internal RegionOrigin(OriginKind kind, string key, int parameterOrdinal, string? region, Location location, string description,
            int awaitableDepth = 0, bool sharedBorrow = false)
        { Kind = kind; Key = key; ParameterOrdinal = parameterOrdinal; Region = region; Location = location; Description = description; AwaitableDepth = awaitableDepth; SharedBorrow = sharedBorrow; }
        internal OriginKind Kind { get; }
        internal string Key { get; }
        internal int ParameterOrdinal { get; }
        internal string? Region { get; }
        internal Location Location { get; }
        internal string Description { get; }
        internal int AwaitableDepth { get; }
        internal bool SharedBorrow { get; }
        internal RegionOrigin WithAwaitableDepth(int depth) => new(Kind, Key, ParameterOrdinal, Region, Location, Description, depth, SharedBorrow);
        internal RegionOrigin WithSharedBorrow() => SharedBorrow ? this : new(Kind, Key, ParameterOrdinal, Region, Location, Description, AwaitableDepth, true);
        public bool Equals(RegionOrigin? other) => other is not null && Key == other.Key && AwaitableDepth == other.AwaitableDepth && SharedBorrow == other.SharedBorrow;
        public override bool Equals(object? obj) => obj is RegionOrigin other && Equals(other);
        public override int GetHashCode() => unchecked((StringComparer.Ordinal.GetHashCode(Key) * 397 ^ AwaitableDepth) * 397 ^ SharedBorrow.GetHashCode());
    }

    private sealed class LifetimeFlowBudgetException : Exception { }
}
