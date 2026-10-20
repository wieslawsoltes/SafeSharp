using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Analysis;

namespace SafeSharp.Analyzers.Semantics;

/// <summary>
/// Checks persistent retention with a finite may-origin domain. Diagnostics are emitted only after
/// convergence; a source occurrence is evidence, not a fabricated concrete execution path.
/// Rust's corresponding boundary is the region requirement on a place receiving a loan:
/// https://rustc-dev-guide.rust-lang.org/borrow-check/region-inference.html.
/// </summary>
internal sealed class BorrowedEscapeAnalysis
{
    private readonly ControlFlowGraph graph;
    private readonly IMethodSymbol method;
    private readonly Compilation compilation;
    private readonly MethodSummaryProvider summaries;
    private readonly CancellationToken cancellationToken;
    private readonly int maximumSteps;
    private readonly Dictionary<ISymbol, string> symbolKeys;
    private readonly Dictionary<CaptureId, string> captureKeys = new();
    private readonly Dictionary<IOperation, Value> evaluatedReceivers = new();
    private readonly Dictionary<IArgumentOperation, ImmutableHashSet<string>> argumentTargets = new();
    private readonly Dictionary<string, Storage> storage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Escape> escapes;
    private readonly Dictionary<string, Diagnostic> diagnostics = new(StringComparer.Ordinal);
    private readonly State history;
    private State? recording;
    private Value? initializerReceiver;
    private Value? conditionalReceiver;
    private bool historyChanged;
    private int steps;

    private BorrowedEscapeAnalysis(ControlFlowGraph graph, IMethodSymbol method, Compilation compilation, MethodSummaryProvider summaries,
        int maximumSteps, CancellationToken cancellationToken, Dictionary<ISymbol, string> symbolKeys, State history, Dictionary<string, Escape> escapes)
    {
        this.graph = graph; this.method = method; this.compilation = compilation; this.summaries = summaries;
        this.maximumSteps = maximumSteps; this.cancellationToken = cancellationToken;
        this.symbolKeys = symbolKeys; this.history = history;
        this.escapes = escapes;
    }

    internal static bool IsRelevant(ImmutableArray<IOperation> blocks, CancellationToken cancellationToken)
    {
        foreach (var block in blocks)
        foreach (var operation in ContractSymbols.Walk(block, descendFunctions: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation is IParameterReferenceOperation parameter && Restricted(parameter.Parameter)) return true;
        }
        return false;
    }

    internal static void Analyze(ControlFlowGraph graph, IMethodSymbol method, Compilation compilation, MethodSummaryProvider summaries,
        AnalyzerConfigOptionsProvider options, Action<Diagnostic> report, CancellationToken cancellationToken)
    {
        var keys = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        var capturedHistory = new State();
        var escapes = new Dictionary<string, Escape>(StringComparer.Ordinal);
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var item in ControlFlowLowerer.EnumerateGraphs(graph, method, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(item.Method)) continue;
            var limit = ConcurrencyContracts.MaximumSteps(options, item.Graph.OriginalOperation.Syntax.SyntaxTree);
            var flow = new BorrowedEscapeAnalysis(item.Graph, item.Method, compilation, summaries, limit, cancellationToken, keys, capturedHistory, escapes);
            foreach (var diagnostic in flow.Run()) report(diagnostic);
        }
    }

    private ImmutableArray<Diagnostic> Run()
    {
        try
        {
            var initial = new State();
            // Nested functions see a conservative envelope of captured outer cells. Their
            // declaration is not a promise that a particular delegate invocation occurred.
            foreach (var pair in history.Values) initial.Values[pair.Key] = pair.Value;
            foreach (var pair in history.References) initial.References[pair.Key] = pair.Value;
            foreach (var pair in history.Escapes) initial.Escapes[pair.Key] = pair.Value;
            foreach (var parameter in method.Parameters)
            {
                Step();
                var key = SymbolKey(parameter);
                initial.Values[key] = parameter.RefKind == RefKind.Out ? Value.External : ParameterValue(parameter);
                if (parameter.RefKind is RefKind.Ref or RefKind.Out)
                    storage[key] = new Storage(parameter.Name, parameter.Locations);
            }
            foreach (var operation in Operations(graph).SelectMany(operation => ContractSymbols.Walk(operation)))
            {
                Step();
                if (operation is IParameterReferenceOperation parameter && !initial.Values.ContainsKey(SymbolKey(parameter.Parameter)))
                    initial.Values[SymbolKey(parameter.Parameter)] = ParameterValue(parameter.Parameter);
            }
            Remember(initial);
            var inputs = new Dictionary<int, State>();
            var outputs = new Dictionary<int, State>();
            var queue = new Queue<BasicBlock>();
            var queued = new HashSet<int>();
            var exceptionalRoots = graph.Blocks.Where(block => block.IsReachable && block.Predecessors.IsEmpty && block.Kind != BasicBlockKind.Entry).ToArray();
            void Enqueue(BasicBlock block) { if (block.IsReachable && queued.Add(block.Ordinal)) queue.Enqueue(block); }
            Enqueue(graph.Blocks[0]);
            foreach (var root in exceptionalRoots) Enqueue(root);
            while (queue.Count > 0)
            {
                Step();
                var block = queue.Dequeue(); queued.Remove(block.Ordinal);
                var incoming = block.Predecessors.Where(edge => outputs.ContainsKey(edge.Source.Ordinal) && Selected(edge))
                    .Select(edge => outputs[edge.Source.Ordinal]).ToList();
                if (block.Kind == BasicBlockKind.Entry) incoming.Add(initial);
                else if (block.Predecessors.IsEmpty || block.Predecessors.Any(edge => !edge.FinallyRegions.IsEmpty))
                    incoming.Add(history);
                if (incoming.Count == 0) continue;
                var state = Join(incoming);
                inputs[block.Ordinal] = Clone(state);
                historyChanged = false;
                Process(block, state);
                Remember(state);
                if (historyChanged)
                {
                    foreach (var root in exceptionalRoots) Enqueue(root);
                    foreach (var continuation in graph.Blocks.Where(candidate => candidate.Predecessors.Any(edge => !edge.FinallyRegions.IsEmpty)))
                        Enqueue(continuation);
                }
                if (outputs.TryGetValue(block.Ordinal, out var previous) && Equal(previous, state)) continue;
                outputs[block.Ordinal] = state;
                foreach (var successor in Successors(block)) Enqueue(successor);
            }
            foreach (var block in graph.Blocks.Where(block => inputs.ContainsKey(block.Ordinal)))
            {
                Step();
                recording = Clone(inputs[block.Ordinal]);
                Process(block, recording);
            }
            recording = null;
            return diagnostics.Values.OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
                .ThenBy(diagnostic => diagnostic.GetMessage(), StringComparer.Ordinal).ToImmutableArray();
        }
        catch (EscapeBudgetException)
        {
            return ImmutableArray.Create(Diagnostic.Create(ConcurrencyDiagnostics.IncompleteContractAnalysis,
                method.Locations.FirstOrDefault(), method.Name, "borrowed escape provenance steps/depth"));
        }
    }

    private void Process(BasicBlock block, State state)
    {
        foreach (var operation in block.Operations) Evaluate(operation, state, 0);
        if (block.BranchValue is not { } branch) return;
        var value = Evaluate(branch, state, 0);
        if (block.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return ||
            block.ConditionalSuccessor?.Semantics == ControlFlowBranchSemantics.Return)
            Return(value, branch, state);
    }

    private Value Evaluate(IOperation operation, State state, int depth)
    {
        Step();
        if (depth > 256) throw new EscapeBudgetException();
        switch (operation)
        {
            case IParameterReferenceOperation parameter: return Read(SymbolKey(parameter.Parameter), state);
            case ILocalReferenceOperation local: return Read(SymbolKey(local.Local), state);
            case IFlowCaptureReferenceOperation capture: return Read(CaptureKey(capture.Id), state);
            case IInstanceReferenceOperation instance:
                return instance.ReferenceKind == InstanceReferenceKind.ImplicitReceiver && initializerReceiver is not null ? initializerReceiver : Value.External;
            case IConditionalAccessInstanceOperation: return conditionalReceiver ?? Value.External;
            case INameOfOperation: return Value.Empty;
            case IInterpolatedStringOperation: return EvaluateDiscardedChildren(operation, state, depth);
            case IParenthesizedOperation parenthesized: return Evaluate(parenthesized.Operand, state, depth + 1);
            case IConversionOperation conversion:
            {
                var operand = Evaluate(conversion.Operand, state, depth + 1);
                return conversion.OperatorMethod is { } converter
                    ? CallResult(converter, Value.Empty, new Dictionary<int, Value> { [0] = operand }, operation, state)
                    : CanRetain(operation.Type) ? operand : Value.Empty;
            }
            case IUnaryOperation unary:
            {
                var operand = Evaluate(unary.Operand, state, depth + 1);
                return unary.OperatorMethod is { } unaryMethod
                    ? CallResult(unaryMethod, Value.Empty, new Dictionary<int, Value> { [0] = operand }, operation, state) : Value.Empty;
            }
            case IBinaryOperation binary:
            {
                var left = Evaluate(binary.LeftOperand, state, depth + 1);
                var right = Evaluate(binary.RightOperand, state, depth + 1);
                return binary.OperatorMethod is { } binaryMethod
                    ? CallResult(binaryMethod, Value.Empty, new Dictionary<int, Value> { [0] = left, [1] = right }, operation, state)
                    : operation.Type?.TypeKind == TypeKind.Delegate
                        ? Container(operation, binary.OperatorKind == BinaryOperatorKind.Subtract ? left : Merge(left, right), state) : Value.Empty;
            }
            case IFlowCaptureOperation capture:
            {
                var value = Evaluate(capture.Value, state, depth + 1);
                state.Values[CaptureKey(capture.Id)] = value;
                // A value capture is a snapshot, even when its operand is addressable. The
                // separate candidate map is consulted only if Roslyn later uses it as an lvalue.
                state.CaptureTargets[CaptureKey(capture.Id)] = Addresses(capture.Value, state, depth + 1, evaluate: false);
                return value;
            }
            case IVariableDeclaratorOperation declaration:
            {
                var value = declaration.Initializer is { } initializer ? Evaluate(initializer.Value, state, depth + 1) : Value.Empty;
                var key = SymbolKey(declaration.Symbol);
                if (declaration.Symbol.RefKind != RefKind.None && declaration.Initializer is { } refInitializer)
                    state.References[key] = Addresses(refInitializer.Value, state, depth + 1, evaluate: false);
                else Write(ImmutableHashSet.Create(StringComparer.Ordinal, key), value, operation, state);
                return value;
            }
            case ISimpleAssignmentOperation assignment:
            {
                var targets = Addresses(assignment.Target, state, depth + 1);
                var value = Evaluate(assignment.Value, state, depth + 1);
                if (assignment.IsRef && Slot(assignment.Target) is { } slot)
                    state.References[slot] = Addresses(assignment.Value, state, depth + 1, evaluate: false);
                else Write(targets, value, assignment.Value, state);
                return value;
            }
            case IDeconstructionAssignmentOperation deconstruction:
            {
                var value = Evaluate(deconstruction.Value, state, depth + 1);
                foreach (var target in AssignmentLeaves(deconstruction.Target))
                    Write(Addresses(target, state, depth + 1), value, deconstruction.Value, state);
                return value;
            }
            case ICompoundAssignmentOperation compound:
            {
                var targets = Addresses(compound.Target, state, depth + 1);
                var left = Union(targets.Select(target => Read(target, state)));
                var right = Evaluate(compound.Value, state, depth + 1);
                var value = compound.OperatorMethod is { } binary
                    ? CallResult(binary, Value.Empty, new Dictionary<int, Value> { [0] = left, [1] = right }, operation, state)
                    : operation.Type?.TypeKind == TypeKind.Delegate
                        ? Container(operation, compound.OperatorKind == BinaryOperatorKind.Subtract ? left : Merge(left, right), state) : Value.Empty;
                Write(targets, value, compound.Value, state);
                return value;
            }
            case IIncrementOrDecrementOperation increment:
            {
                var targets = Addresses(increment.Target, state, depth + 1);
                var before = Union(targets.Select(target => Read(target, state)));
                var after = increment.OperatorMethod is { } unary
                    ? CallResult(unary, Value.Empty, new Dictionary<int, Value> { [0] = before }, operation, state) : Value.Empty;
                Write(targets, after, operation, state);
                return increment.IsPostfix ? before : after;
            }
            case IFieldReferenceOperation field:
                return CanRetain(field.Type) ? ReadMember(field.Instance, field.Field, operation, state, depth) : EvaluateDiscardedChildren(operation, state, depth);
            case IPropertyReferenceOperation property:
            {
                var receiver = property.Instance is null ? Value.Empty : Evaluate(property.Instance, state, depth + 1);
                evaluatedReceivers[property] = receiver;
                var arguments = Arguments(property.Arguments, state, depth);
                if (!CanRetain(property.Type)) return Value.Empty;
                if (SymbolFacts.IsKnownReceiverView(property.Property, () => Step())) return receiver;
                var fromContents = Union(receiver.Containers.Select(key => Read(key, state)));
                return Merge(fromContents, property.Property.GetMethod is { } getter ? CallResult(getter, receiver, arguments, operation, state) : Value.External);
            }
            case IArrayElementReferenceOperation element:
            {
                var receiver = Evaluate(element.ArrayReference, state, depth + 1);
                evaluatedReceivers[element] = receiver;
                foreach (var index in element.Indices) Evaluate(index, state, depth + 1);
                return CanRetain(element.Type) ? Merge(new Value(receiver.Origins, emptyStrings, emptyStrings, receiver.ExternalStorage),
                    Union(receiver.Containers.Select(key => Read(key, state)))) : Value.Empty;
            }
            case IObjectCreationOperation creation:
            {
                var arguments = Arguments(creation.Arguments, state, depth);
                if (creation.Constructor is { } constructor) CheckConsumption(constructor, Value.Empty, arguments, operation, state);
                // ValueTask(Task<T>) wraps the task carrier; ValueTask(T) stores the result.
                if (creation.Constructor is { Parameters.Length: 1 } taskConstructor &&
                    taskConstructor.ContainingType.Name == "ValueTask" && taskConstructor.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" &&
                    taskConstructor.OriginalDefinition.Parameters[0].Type is INamedTypeSymbol { Name: "Task" } && arguments.TryGetValue(0, out var task))
                    return task;
                var contents = Union(creation.Arguments.Where(argument => argument.Parameter is null || !Restricted(argument.Parameter))
                    .Select(argument => argument.Parameter is { } parameter && arguments.TryGetValue(parameter.Ordinal, out var value) ? value : Value.Empty));
                var isPayload = creation.Constructor is { Parameters.Length: 1 } payloadConstructor &&
                    payloadConstructor.ContainingType is { Name: "ValueTask", Arity: 1 } &&
                    payloadConstructor.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" &&
                    payloadConstructor.OriginalDefinition.Parameters[0].Type is ITypeParameterSymbol;
                var result = Container(operation, contents, state, isPayload);
                if (creation.Initializer is not null)
                {
                    var previous = initializerReceiver; initializerReceiver = result;
                    Evaluate(creation.Initializer, state, depth + 1);
                    initializerReceiver = previous;
                }
                return result;
            }
            case IAnonymousObjectCreationOperation anonymous:
                return Container(operation, Union(anonymous.Initializers.Select(initializer => Evaluate(initializer is ISimpleAssignmentOperation assignment ? assignment.Value : initializer, state, depth + 1))), state);
            case IArrayCreationOperation array:
                foreach (var dimension in array.DimensionSizes) Evaluate(dimension, state, depth + 1);
                return Container(operation, array.Initializer is null ? Value.Empty : Evaluate(array.Initializer, state, depth + 1), state);
            case IArrayInitializerOperation array: return Union(array.ElementValues.Select(value => Evaluate(value, state, depth + 1)));
            case ITupleOperation tuple: return Container(operation, Union(tuple.Elements.Select(value => Evaluate(value, state, depth + 1))), state);
            case IInvocationOperation invocation:
            {
                var receiver = invocation.Instance is null ? Value.Empty : Evaluate(invocation.Instance, state, depth + 1);
                if (invocation.TargetMethod.MethodKind == MethodKind.LocalFunction &&
                    invocation.TargetMethod.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is { } localFunctionSyntax)
                    receiver = Capture(localFunctionSyntax, state);
                var arguments = Arguments(invocation.Arguments, state, depth);
                foreach (var argument in invocation.Arguments)
                {
                    if (argument.Parameter is not { } parameter || !arguments.TryGetValue(parameter.Ordinal, out var value)) continue;
                    if (parameter.RefKind is RefKind.Ref or RefKind.Out)
                    {
                        var summary = summaries.Get(invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod, cancellationToken);
                        var ordinal = invocation.TargetMethod.ReducedFrom is null ? parameter.Ordinal : parameter.Ordinal + 1;
                        if (summary.OwnedOutputParameters.ContainsKey(ordinal))
                            Write(argumentTargets.TryGetValue(argument, out var savedTargets) ? savedTargets : emptyStrings,
                                Container(argument.Value, Value.Empty, state), argument.Value, state);
                    }
                }
                if (invocation.TargetMethod.MethodKind is MethodKind.DelegateInvoke or MethodKind.LocalFunction)
                    InvalidateCapturedCells(receiver, state, invocation);
                foreach (var argument in arguments.Values.Where(value => !value.Captures.IsEmpty)) InvalidateCapturedCells(argument, state, invocation);
                return CallResult(invocation.TargetMethod, receiver, arguments, operation, state);
            }
            case IDelegateCreationOperation creation: return Evaluate(creation.Target, state, depth + 1);
            case IFlowAnonymousFunctionOperation lambda: return Capture(lambda.Syntax, state);
            case IAnonymousFunctionOperation lambda: return Capture(lambda.Syntax, state);
            case IMethodReferenceOperation reference:
                return reference.Method.MethodKind == MethodKind.LocalFunction && reference.Method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is { } methodSyntax
                    ? Capture(methodSyntax, state) : reference.Instance is null ? Value.Empty : Container(operation, Evaluate(reference.Instance, state, depth + 1), state);
            case ILocalFunctionOperation: return Value.Empty;
            case IEventAssignmentOperation assignment:
            {
                Evaluate(assignment.EventReference, state, depth + 1);
                var value = Evaluate(assignment.HandlerValue, state, depth + 1);
                if (assignment.Adds) EscapeValue(value, assignment.HandlerValue, "event '" + assignment.EventReference.Syntax + "'",
                    Enumerable.Empty<Location>(), state);
                return Value.Empty;
            }
            case IReturnOperation returned:
            {
                var value = returned.ReturnedValue is null ? Value.Empty : Evaluate(returned.ReturnedValue, state, depth + 1);
                if (returned.Kind == OperationKind.YieldReturn)
                    EscapeValue(value, returned.ReturnedValue ?? operation, "yield return", method.Locations, state);
                else Return(value, returned.ReturnedValue ?? operation, state);
                return value;
            }
            case IAwaitOperation awaited:
            {
                var carrier = Evaluate(awaited.Operation, state, depth + 1);
                return awaited.Operation.Type is INamedTypeSymbol carrierType && KnownAwaitable(carrierType)
                    ? Payload(carrier, awaited.Type, state) : CanRetain(awaited.Type) ? Value.External : Value.Empty;
            }
            case IConditionalAccessOperation conditional:
            {
                var receiver = Evaluate(conditional.Operation, state, depth + 1);
                var previous = conditionalReceiver; conditionalReceiver = receiver;
                var result = Evaluate(conditional.WhenNotNull, state, depth + 1);
                conditionalReceiver = previous;
                return result;
            }
            case IConditionalOperation conditional:
                Evaluate(conditional.Condition, state, depth + 1);
                return Merge(Evaluate(conditional.WhenTrue, state, depth + 1), conditional.WhenFalse is null ? Value.Empty : Evaluate(conditional.WhenFalse, state, depth + 1));
            case ICoalesceOperation coalesce:
                return Merge(Evaluate(coalesce.Value, state, depth + 1), Evaluate(coalesce.WhenNull, state, depth + 1));
            case IIsPatternOperation pattern:
            {
                var value = Evaluate(pattern.Value, state, depth + 1);
                foreach (var item in ContractSymbols.Walk(pattern.Pattern))
                {
                    Step();
                    var symbol = item switch { IDeclarationPatternOperation patternDeclaration => patternDeclaration.DeclaredSymbol, IRecursivePatternOperation recursive => recursive.DeclaredSymbol, _ => null };
                    if (symbol is not null) Write(ImmutableHashSet.Create(StringComparer.Ordinal, SymbolKey(symbol)), value, pattern.Value, state);
                }
                return Value.Empty;
            }
            default:
            {
                var children = Union(operation.ChildOperations.Select(child => Evaluate(child, state, depth + 1)));
                return operation.Type is null || CanRetain(operation.Type) ? children : Value.Empty;
            }
        }
    }

    private Value CallResult(IMethodSymbol target, Value receiver, Dictionary<int, Value> arguments, IOperation operation, State state)
    {
        Step();
        CheckConsumption(target, receiver, arguments, operation, state);
        if (!CanRetain(operation.Type)) return Value.Empty;
        var unreduced = target.ReducedFrom ?? target;
        if (unreduced.ContainingType is { Arity: 0, Name: "Task" or "ValueTask" } && unreduced.Name == "FromResult" &&
            unreduced.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks")
            return Container(operation, Union(arguments.Values), state, isPayload: true);
        if (KnownAwaitable(target.ContainingType))
        {
            if (target.Name is "ConfigureAwait" or "GetAwaiter" or "AsTask") return receiver;
            if (target.Name == "GetResult") return Payload(receiver, operation.Type, state);
        }
        var summary = summaries.Get(unreduced, cancellationToken);
        if (!summary.ReturnOrigins.IsEmpty)
        {
            var results = new List<Value>();
            foreach (var origin in summary.ReturnOrigins)
            {
                Step();
                var value = origin.Source == MethodOriginAnalysis.Fresh ? Container(operation, Value.Empty, state) :
                    origin.Source == MethodOriginAnalysis.Null ? Value.Empty : origin.Source == MethodOriginAnalysis.Receiver ? receiver : Value.External;
                foreach (var parameter in unreduced.Parameters)
                {
                    if (origin.Source != MethodOriginAnalysis.Parameter(parameter.Ordinal)) continue;
                    var ordinal = parameter.Ordinal - (target.ReducedFrom is null ? 0 : 1);
                    value = ordinal < 0 ? receiver : arguments.TryGetValue(ordinal, out var argument) ? argument : Value.External;
                    break;
                }
                for (var layer = 0; layer < origin.PayloadDepth; layer++)
                    value = Container(operation, value, state, isPayload: true, layer: layer + 1);
                results.Add(value);
            }
            return Union(results);
        }
        if (summary.ReturnsOwned) return Container(operation, Value.Empty, state);
        var values = new List<Value>();
        foreach (var source in summary.BorrowedSources)
        {
            Step();
            if (source == "this") { values.Add(receiver); continue; }
            var ordinal = unreduced.Parameters.FirstOrDefault(parameter => parameter.Name == source)?.Ordinal;
            if (ordinal is null) continue;
            if (target.ReducedFrom is not null && ordinal == 0) values.Add(receiver);
            else if (arguments.TryGetValue(ordinal.Value - (target.ReducedFrom is null ? 0 : 1), out var value)) values.Add(value);
        }
        // An opaque method is neither an inferred identity nor an inferred escape sink.
        if (values.Count == 0) return Value.External;
        var result = Union(values);
        // Payload summaries introduce exactly one carrier layer. A generic or nonasync task
        // identity already denotes the carrier itself and must preserve its existing contents.
        return summary.ReturnsAsyncPayload && summaries.AsyncResults.IsCarrier(target.ReturnType)
            ? Container(operation, result, state, isPayload: true) : result;
    }

    private Value Payload(Value carrier, ITypeSymbol? resultType, State state)
    {
        if (!CanRetain(resultType)) return Value.Empty;
        // A borrowed Task object does not establish the identity of its result. Only contents
        // explicitly stored by known carriers (Task.FromResult / ValueTask(T)) flow through await.
        return carrier.Payloads.IsEmpty ? Value.External : Union(carrier.Payloads.Select(key => Read(key, state)));
    }

    private static bool KnownAwaitable(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace.ToDisplayString();
        return ns == "System.Threading.Tasks" && type.Name is "Task" or "ValueTask" ||
            ns == "System.Runtime.CompilerServices" && type.Name is "TaskAwaiter" or "ValueTaskAwaiter" or
                "ConfiguredTaskAwaitable" or "ConfiguredValueTaskAwaitable" or "ConfiguredTaskAwaiter" or "ConfiguredValueTaskAwaiter";
    }

    private void CheckConsumption(IMethodSymbol target, Value receiver, Dictionary<int, Value> arguments, IOperation operation, State state)
    {
        var unreduced = target.ReducedFrom ?? target;
        foreach (var parameter in unreduced.Parameters)
        {
            Step();
            if (!SymbolFacts.HasAttribute(parameter, "Consumes") && !SymbolFacts.HasAttribute(parameter, "Owned")) continue;
            var index = parameter.Ordinal - (target.ReducedFrom is null ? 0 : 1);
            if (index >= 0 && !arguments.ContainsKey(index)) continue;
            var value = index < 0 ? receiver : arguments[index];
            var evidence = operation switch
            {
                IInvocationOperation invocation when index < 0 => invocation.Instance ?? operation,
                IInvocationOperation invocation => invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == index)?.Value ?? operation,
                IObjectCreationOperation creation => creation.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == index)?.Value ?? operation,
                IConversionOperation conversion => conversion.Operand,
                IUnaryOperation unary => unary.Operand,
                IBinaryOperation binary => index == 0 ? binary.LeftOperand : binary.RightOperand,
                ICompoundAssignmentOperation compound => index == 0 ? compound.Target : compound.Value,
                IIncrementOrDecrementOperation increment => increment.Target,
                _ => operation,
            };
            EscapeValue(value, evidence, "consuming parameter '" + parameter.Name + "'", parameter.Locations, state, onlyNoEscape: true);
        }
    }

    private Dictionary<int, Value> Arguments(ImmutableArray<IArgumentOperation> arguments, State state, int depth)
    {
        var values = new Dictionary<int, Value>();
        foreach (var argument in arguments)
        {
            Value value;
            if (argument.Parameter?.RefKind == RefKind.Out)
            {
                argumentTargets[argument] = Addresses(argument.Value, state, depth + 1);
                value = Value.Empty;
            }
            else
            {
                value = Evaluate(argument.Value, state, depth + 1);
                if (argument.Parameter?.RefKind == RefKind.Ref)
                    argumentTargets[argument] = Addresses(argument.Value, state, depth + 1, evaluate: false);
            }
            if (argument.Parameter is { } parameter) values[parameter.Ordinal] = values.TryGetValue(parameter.Ordinal, out var previous) ? Merge(previous, value) : value;
        }
        return values;
    }

    private Value ReadMember(IOperation? instance, ISymbol member, IOperation operation, State state, int depth)
    {
        var receiver = instance is null ? Value.External : Evaluate(instance, state, depth + 1);
        evaluatedReceivers[operation] = receiver;
        var values = receiver.Containers.Select(key => Read(key, state)).ToList();
        if (receiver.ExternalStorage) values.Add(Read(HeapKey(member), state));
        values.Add(new Value(receiver.Origins, emptyStrings, emptyStrings, receiver.ExternalStorage));
        return Union(values);
    }

    private Value EvaluateDiscardedChildren(IOperation operation, State state, int depth)
    { foreach (var child in operation.ChildOperations) Evaluate(child, state, depth + 1); return Value.Empty; }

    private ImmutableHashSet<string> Addresses(IOperation operation, State state, int depth, bool evaluate = true)
    {
        Step();
        if (depth > 256) throw new EscapeBudgetException();
        if (Slot(operation) is { } slot)
            return operation is IFlowCaptureReferenceOperation && state.CaptureTargets.TryGetValue(slot, out var candidates) && !candidates.IsEmpty
                ? candidates : Targets(slot, state);
        if (operation is IDeclarationExpressionOperation declaration) return Addresses(declaration.Expression, state, depth + 1, evaluate);
        if (operation is IConversionOperation { OperatorMethod: null } conversion) return Addresses(conversion.Operand, state, depth + 1, evaluate);
        if (operation is IParenthesizedOperation parentheses) return Addresses(parentheses.Operand, state, depth + 1, evaluate);
        IOperation? receiverOperation;
        ISymbol? member;
        IEnumerable<IOperation> indices = Enumerable.Empty<IOperation>();
        switch (operation)
        {
            case IFieldReferenceOperation field: receiverOperation = field.Instance; member = field.Field; break;
            case IPropertyReferenceOperation property:
                receiverOperation = property.Instance; member = property.Property;
                indices = property.Arguments.Select(argument => argument.Value);
                break;
            case IArrayElementReferenceOperation element:
                receiverOperation = element.ArrayReference; member = null;
                indices = element.Indices;
                break;
            default:
                if (evaluate) EvaluateDiscardedChildren(operation, state, depth);
                return emptyStrings;
        }
        var receiver = receiverOperation is null ? Value.External : evaluate ? Evaluate(receiverOperation, state, depth + 1)
            : evaluatedReceivers.TryGetValue(operation, out var evaluated) ? evaluated
            : Slot(receiverOperation) is { } receiverSlot ? Read(receiverSlot, state) : Value.External;
        evaluatedReceivers[operation] = receiver;
        if (evaluate) foreach (var index in indices) Evaluate(index, state, depth + 1);
        var result = receiver.Containers;
        if (receiver.ExternalStorage || result.IsEmpty)
        {
            var key = member is null ? "$external-array" : HeapKey(member);
            storage[key] = new Storage(member?.Name ?? "array element", member?.Locations ?? ImmutableArray<Location>.Empty);
            result = result.Add(key);
        }
        return result;
    }

    private void Write(ImmutableHashSet<string> targets, Value value, IOperation operation, State state)
    {
        foreach (var target in targets)
        {
            Step();
            if (storage.TryGetValue(target, out var destination)) EscapeValue(value, operation, destination.Name, destination.Locations, state);
            if (state.Escapes.TryGetValue(target, out var existingEscapes))
                foreach (var escaped in existingEscapes) Retain(value, operation, escapes[escaped], state);
            state.Values[target] = targets.Count > 1 || target.StartsWith("$container:", StringComparison.Ordinal)
                ? Merge(Read(target, state), value) : value;
            RememberValue(target, state.Values[target]);
        }
    }

    private Value Container(IOperation operation, Value contents, State state, bool isPayload = false, int layer = 0)
    {
        var key = "$container:" + operation.Syntax.SyntaxTree.FilePath + ":" + operation.Syntax.SpanStart + ":" + operation.Syntax.Span.Length + ":" + layer;
        state.Values[key] = Merge(Read(key, state), contents);
        RememberValue(key, state.Values[key]);
        var keys = ImmutableHashSet.Create(StringComparer.Ordinal, key);
        return new Value(emptyOrigins, keys, emptyStrings, false, isPayload ? keys : emptyStrings);
    }

    private Value Capture(SyntaxNode function, State state)
    {
        var captures = emptyStrings;
        var model = compilation.GetSemanticModel(function.SyntaxTree);
        var pending = new Stack<IOperation>();
        if (model.GetOperation(function, cancellationToken) is { } root) pending.Push(root);
        while (pending.Count > 0)
        {
            Step();
            var operation = pending.Pop();
            if (operation is INameOfOperation) continue;
            foreach (var child in operation.ChildOperations) pending.Push(child);
            ISymbol? symbol = operation switch
            {
                IParameterReferenceOperation parameterReference => parameterReference.Parameter,
                ILocalReferenceOperation localReference => localReference.Local,
                _ => null,
            };
            if (symbol is not (IParameterSymbol or ILocalSymbol) || symbol.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == function.SyntaxTree && function.Span.Contains(reference.Span))) continue;
            var key = SymbolKey(symbol);
            if (!state.Values.ContainsKey(key) && symbol is IParameterSymbol parameter) state.Values[key] = ParameterValue(parameter);
            captures = captures.Add(key);
        }
        return new Value(emptyOrigins, emptyStrings, captures, false);
    }

    private void InvalidateCapturedCells(Value callback, State state, IOperation invocation)
    {
        if (callback.Captures.IsEmpty) return;
        var possible = new Value(Resolve(callback, state), emptyStrings, emptyStrings, true);
        foreach (var capture in callback.Captures)
            Write(ImmutableHashSet.Create(StringComparer.Ordinal, capture), Merge(Read(capture, state), possible), invocation, state);
    }

    private void Return(Value value, IOperation operation, State state)
    {
        var indirect = Resolve(new Value(emptyOrigins, value.Containers, value.Captures, value.ExternalStorage), state);
        var returning = new Value(indirect.Union(value.Origins.Where(NoEscape)), value.Containers, value.Captures, false);
        var summary = summaries.Get(method, cancellationToken);
        var allowed = summary.BorrowedSources.Select(name => method.Parameters.FirstOrDefault(parameter => parameter.Name == name))
            .Where(parameter => parameter is not null && !NoEscape(parameter)).Cast<IParameterSymbol>().ToImmutableHashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
        EscapeValue(returning, operation, "return from '" + method.Name + "'", method.Locations, state, exempt: allowed);
    }

    private void EscapeValue(Value value, IOperation operation, string destination, IEnumerable<Location> related, State state,
        bool onlyNoEscape = false, ImmutableHashSet<IParameterSymbol>? exempt = null)
    {
        var id = operation.Syntax.SyntaxTree.FilePath + ":" + operation.Syntax.SpanStart + ":" + operation.Syntax.Span.Length + ":" + destination + ":" + onlyNoEscape;
        if (!escapes.TryGetValue(id, out var escaped))
            escapes[id] = escaped = new Escape(id, destination, operation.Syntax.GetLocation(), related.ToImmutableArray(), onlyNoEscape, exempt ?? emptyOrigins);
        Retain(value, operation, escaped, state);
    }

    private void Retain(Value value, IOperation operation, Escape escaped, State state)
    {
        Report(value, operation, escaped, state);
        var pending = new Stack<string>(value.Containers.Concat(value.Captures));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            Step();
            var key = pending.Pop();
            if (!visited.Add(key)) continue;
            state.Escapes[key] = (state.Escapes.TryGetValue(key, out var existing) ? existing : emptyStrings).Add(escaped.Id);
            var nested = Read(key, state);
            foreach (var dependency in nested.Containers.Concat(nested.Captures)) pending.Push(dependency);
        }
    }

    private void Report(Value value, IOperation operation, Escape escaped, State state)
    {
        if (recording is null) return;
        foreach (var origin in Resolve(value, state).Where(origin => !escaped.Exempt.Contains(origin) && (!escaped.OnlyNoEscape || NoEscape(origin))))
        {
            Step();
            var location = EvidenceLocation(operation.Syntax, origin);
            var key = origin.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ":" + origin.Locations.FirstOrDefault()?.SourceSpan.Start + ":" + location.SourceSpan + ":" + escaped.Destination;
            var properties = ImmutableDictionary<string, string?>.Empty
                .Add("SafeSharp.BorrowedSource", origin.Name).Add("SafeSharp.EscapeDestination", escaped.Destination)
                .Add("SafeSharp.EscapeAnalysis", "converged may-origin dataflow").Add("SafeSharp.Certainty", "Possible")
                .Add("SafeSharp.DoesNotEscape", NoEscape(origin) ? "true" : "false");
            var related = origin.Locations.Concat(escaped.Related).Append(escaped.Location)
                .Where(site => site.IsInSource && !site.Equals(location)).Distinct();
            diagnostics[key] = Diagnostic.Create(SemanticDiagnostics.StoredBorrowedParameter, location, related, properties,
                origin.Name, escaped.Destination);
        }
    }

    private Location EvidenceLocation(SyntaxNode value, IParameterSymbol origin)
    {
        var model = compilation.GetSemanticModel(value.SyntaxTree);
        foreach (var identifier in value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            Step();
            if (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, origin)) return identifier.GetLocation();
        }
        return value.GetLocation();
    }

    private ImmutableHashSet<IParameterSymbol> Resolve(Value value, State state)
    {
        var origins = value.Origins;
        var pending = new Stack<string>(value.Containers.Concat(value.Captures));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            Step();
            var key = pending.Pop();
            if (!visited.Add(key)) continue;
            var nested = Read(key, state);
            origins = origins.Union(nested.Origins);
            foreach (var dependency in nested.Containers.Concat(nested.Captures)) pending.Push(dependency);
        }
        return origins;
    }

    private string SymbolKey(ISymbol symbol)
    {
        if (!symbolKeys.TryGetValue(symbol, out var key)) symbolKeys[symbol] = key = "$symbol:" + symbolKeys.Count;
        return key;
    }
    private string CaptureKey(CaptureId capture)
    {
        if (!captureKeys.TryGetValue(capture, out var key)) captureKeys[capture] = key = "$capture:" + method.Locations.FirstOrDefault()?.SourceSpan.Start + ":" + captureKeys.Count;
        return key;
    }
    private string HeapKey(ISymbol member) => "$heap:" + SymbolKey(member);
    private string? Slot(IOperation operation) => operation switch
    {
        IParameterReferenceOperation parameter => SymbolKey(parameter.Parameter), ILocalReferenceOperation local => SymbolKey(local.Local),
        IFlowCaptureReferenceOperation capture => CaptureKey(capture.Id), IDeclarationExpressionOperation declaration => Slot(declaration.Expression), _ => null
    };
    private Value ParameterValue(IParameterSymbol parameter) => Restricted(parameter)
        ? new Value(ImmutableHashSet.Create<IParameterSymbol>(SymbolEqualityComparer.Default, parameter), emptyStrings, emptyStrings, true) : Value.External;
    private static bool Restricted(IParameterSymbol parameter) => SymbolFacts.HasAttribute(parameter, "Borrowed") || SymbolFacts.HasAttribute(parameter, "BorrowedMut") || NoEscape(parameter);
    private static bool NoEscape(IParameterSymbol parameter) => SymbolFacts.HasAttribute(parameter, "DoesNotEscape");
    private static bool CanRetain(ITypeSymbol? type) => type is not null && type.SpecialType != SpecialType.System_Void &&
        (type.IsReferenceType || type.TypeKind == TypeKind.TypeParameter || type is INamedTypeSymbol { IsValueType: true, SpecialType: SpecialType.None, TypeKind: not TypeKind.Enum });
    private static ImmutableHashSet<string> Targets(string key, State state) => state.References.TryGetValue(key, out var targets) ? targets : ImmutableHashSet.Create(StringComparer.Ordinal, key);
    private Value Read(string key, State state) => Union(Targets(key, state).Select(target => state.Values.TryGetValue(target, out var value) ? value : Value.Empty));
    private Value Union(IEnumerable<Value> values) => values.Aggregate(Value.Empty, Merge);
    private Value Merge(Value left, Value right)
    {
        Step(left.Origins.Count + right.Origins.Count + left.Containers.Count + right.Containers.Count + left.Captures.Count + right.Captures.Count + left.Payloads.Count + right.Payloads.Count + 1);
        return new Value(left.Origins.Union(right.Origins), left.Containers.Union(right.Containers), left.Captures.Union(right.Captures), left.ExternalStorage || right.ExternalStorage, left.Payloads.Union(right.Payloads));
    }
    private State Join(IEnumerable<State> states)
    {
        var result = new State();
        foreach (var state in states)
        {
            foreach (var pair in state.Values) result.Values[pair.Key] = Merge(result.Values.TryGetValue(pair.Key, out var previous) ? previous : Value.Empty, pair.Value);
            foreach (var pair in state.References) { Step(pair.Value.Count + 1); result.References[pair.Key] = (result.References.TryGetValue(pair.Key, out var previous) ? previous : emptyStrings).Union(pair.Value); }
            foreach (var pair in state.CaptureTargets) { Step(pair.Value.Count + 1); result.CaptureTargets[pair.Key] = (result.CaptureTargets.TryGetValue(pair.Key, out var previous) ? previous : emptyStrings).Union(pair.Value); }
            foreach (var pair in state.Escapes) { Step(pair.Value.Count + 1); result.Escapes[pair.Key] = (result.Escapes.TryGetValue(pair.Key, out var previous) ? previous : emptyStrings).Union(pair.Value); }
        }
        return result;
    }
    private State Clone(State state) { Step(state.Values.Count + state.References.Count + state.CaptureTargets.Count + state.Escapes.Count + 1); return state.Clone(); }
    private bool Equal(State left, State right) { Step(left.Values.Count + left.References.Count + left.CaptureTargets.Count + left.Escapes.Count + 1); return left.Equals(right); }
    private void Remember(State state)
    {
        foreach (var pair in state.Values) RememberValue(pair.Key, pair.Value);
        foreach (var pair in state.References)
        {
            Step(pair.Value.Count + 1);
            var previous = history.References.TryGetValue(pair.Key, out var known) ? known : emptyStrings;
            var combined = previous.Union(pair.Value);
            if (!previous.SetEquals(combined)) { history.References[pair.Key] = combined; historyChanged = true; }
        }
        foreach (var pair in state.Escapes)
        {
            Step(pair.Value.Count + 1);
            var previous = history.Escapes.TryGetValue(pair.Key, out var known) ? known : emptyStrings;
            var combined = previous.Union(pair.Value);
            if (!previous.SetEquals(combined)) { history.Escapes[pair.Key] = combined; historyChanged = true; }
        }
    }
    private void RememberValue(string key, Value value)
    {
        if (recording is not null) return;
        var previous = history.Values.TryGetValue(key, out var known) ? known : Value.Empty;
        var combined = Merge(previous, value);
        if (!previous.Equals(combined)) { history.Values[key] = combined; historyChanged = true; }
    }
    private void Step(int count = 1)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count > maximumSteps - steps) throw new EscapeBudgetException();
        steps += count;
    }
    private IEnumerable<IOperation> AssignmentLeaves(IOperation operation)
    {
        var pending = new Stack<IOperation>(); pending.Push(operation);
        while (pending.Count > 0)
        {
            Step();
            var current = pending.Pop();
            if (current is ITupleOperation tuple)
            { foreach (var element in tuple.Elements.Reverse()) pending.Push(element); }
            else yield return current;
        }
    }
    private static IEnumerable<IOperation> Operations(ControlFlowGraph graph) => graph.Blocks.SelectMany(block => block.Operations.Concat(block.BranchValue is null ? Enumerable.Empty<IOperation>() : new[] { block.BranchValue }));
    private static bool Selected(ControlFlowBranch edge) => edge.Source.BranchValue?.ConstantValue is not { HasValue: true, Value: bool condition }
        || edge.Source.ConditionKind == ControlFlowConditionKind.None || (condition == (edge.Source.ConditionKind == ControlFlowConditionKind.WhenTrue)) == edge.IsConditionalSuccessor;
    private static IEnumerable<BasicBlock> Successors(BasicBlock block)
    {
        if (block.FallThroughSuccessor is { Destination: { } fallthrough } next && Selected(next)) yield return fallthrough;
        if (block.ConditionalSuccessor is { Destination: { } conditional } alternative && Selected(alternative)) yield return conditional;
    }

    private static readonly ImmutableHashSet<IParameterSymbol> emptyOrigins = ImmutableHashSet.Create<IParameterSymbol>(SymbolEqualityComparer.Default);
    private static readonly ImmutableHashSet<string> emptyStrings = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    private sealed class Value : IEquatable<Value>
    {
        internal static readonly Value Empty = new(emptyOrigins, emptyStrings, emptyStrings, false);
        internal static readonly Value External = new(emptyOrigins, emptyStrings, emptyStrings, true);
        internal Value(ImmutableHashSet<IParameterSymbol> origins, ImmutableHashSet<string> containers, ImmutableHashSet<string> captures, bool externalStorage, ImmutableHashSet<string>? payloads = null)
        { Origins = origins; Containers = containers; Captures = captures; ExternalStorage = externalStorage; Payloads = payloads ?? emptyStrings; }
        internal ImmutableHashSet<IParameterSymbol> Origins { get; }
        internal ImmutableHashSet<string> Containers { get; }
        internal ImmutableHashSet<string> Captures { get; }
        internal ImmutableHashSet<string> Payloads { get; }
        internal bool ExternalStorage { get; }
        public bool Equals(Value? other) => other is not null && ExternalStorage == other.ExternalStorage && Origins.SetEquals(other.Origins) && Containers.SetEquals(other.Containers) && Captures.SetEquals(other.Captures) && Payloads.SetEquals(other.Payloads);
    }
    private sealed class State : IEquatable<State>
    {
        internal Dictionary<string, Value> Values { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, ImmutableHashSet<string>> References { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, ImmutableHashSet<string>> CaptureTargets { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, ImmutableHashSet<string>> Escapes { get; } = new(StringComparer.Ordinal);
        internal State Clone()
        {
            var clone = new State();
            foreach (var pair in Values) clone.Values[pair.Key] = pair.Value;
            foreach (var pair in References) clone.References[pair.Key] = pair.Value;
            foreach (var pair in CaptureTargets) clone.CaptureTargets[pair.Key] = pair.Value;
            foreach (var pair in Escapes) clone.Escapes[pair.Key] = pair.Value;
            return clone;
        }
        public bool Equals(State? other) => other is not null && Values.Count == other.Values.Count && References.Count == other.References.Count && CaptureTargets.Count == other.CaptureTargets.Count && Escapes.Count == other.Escapes.Count &&
            Values.All(pair => other.Values.TryGetValue(pair.Key, out var value) && pair.Value.Equals(value)) &&
            References.All(pair => other.References.TryGetValue(pair.Key, out var value) && pair.Value.SetEquals(value)) &&
            CaptureTargets.All(pair => other.CaptureTargets.TryGetValue(pair.Key, out var value) && pair.Value.SetEquals(value)) &&
            Escapes.All(pair => other.Escapes.TryGetValue(pair.Key, out var value) && pair.Value.SetEquals(value));
    }
    private sealed class Storage(string name, IEnumerable<Location> locations)
    { internal string Name { get; } = name; internal IEnumerable<Location> Locations { get; } = locations; }
    private sealed class Escape(string id, string destination, Location location, ImmutableArray<Location> related, bool onlyNoEscape, ImmutableHashSet<IParameterSymbol> exempt)
    {
        internal string Id { get; } = id;
        internal string Destination { get; } = destination;
        internal Location Location { get; } = location;
        internal ImmutableArray<Location> Related { get; } = related;
        internal bool OnlyNoEscape { get; } = onlyNoEscape;
        internal ImmutableHashSet<IParameterSymbol> Exempt { get; } = exempt;
    }
    private sealed class EscapeBudgetException : Exception { }
}
