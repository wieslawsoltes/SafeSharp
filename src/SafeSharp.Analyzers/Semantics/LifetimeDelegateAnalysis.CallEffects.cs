using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Analysis;

namespace SafeSharp.Analyzers.Semantics;

internal static partial class LifetimeDelegateAnalysis
{
    /// <summary>Compilation-scoped immutable evidence invalidation, scanned once per callable.</summary>
    internal sealed class CallEffectsProvider
    {
        private readonly ConcurrentDictionary<IMethodSymbol, Lazy<CallEffectSummary>> cache = new(SymbolEqualityComparer.Default);

        internal CallEffectSummary Get(IInvocationOperation invocation, IMethodSymbol method, Compilation compilation, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cache.GetOrAdd(method, _ => new Lazy<CallEffectSummary>(() =>
            {
                IOperation root = invocation;
                while (root.Parent is { } parent && root is not IAnonymousFunctionOperation and not ILocalFunctionOperation) root = parent;
                return new CallEffectScanner(root, limit, cancellationToken).Scan();
            })).Value;
        }
    }

    internal sealed class CallEffectSummary
    {
        private readonly ImmutableDictionary<ISymbol, ImmutableArray<CallEffect>> slots;
        private readonly ImmutableDictionary<ISymbol, ImmutableArray<CallEffect>> contents;
        private readonly bool nonlocalBranches;
        internal CallEffectSummary(ImmutableDictionary<ISymbol, ImmutableArray<CallEffect>> slots,
            ImmutableDictionary<ISymbol, ImmutableArray<CallEffect>> contents, bool nonlocalBranches, int steps)
        { this.slots = slots; this.contents = contents; this.nonlocalBranches = nonlocalBranches; Steps = steps; }
        internal int Steps { get; }
        internal bool InvalidatesSlot(ISymbol symbol, IInvocationOperation invocation) => Invalidates(slots, symbol, invocation);
        internal bool InvalidatesContents(ISymbol symbol, IInvocationOperation invocation) => Invalidates(contents, symbol, invocation);

        private bool Invalidates(ImmutableDictionary<ISymbol, ImmutableArray<CallEffect>> effects, ISymbol symbol, IInvocationOperation invocation)
        {
            if (!effects.TryGetValue(symbol, out var candidates)) return false;
            var loopEnd = invocation.Syntax.Span.End;
            for (var parent = invocation.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent is ILoopOperation) loopEnd = Math.Max(loopEnd, parent.Syntax.Span.End);
                if (parent is IAnonymousFunctionOperation or ILocalFunctionOperation) break;
            }
            foreach (var effect in candidates)
            {
                // Deferred effects precede source-ordered immediate effects. Once the query has
                // passed this invocation and its enclosing loops there are no relevant later
                // effects, so many calls do not repeatedly scan an entire method's suffix.
                if (!nonlocalBranches && !effect.Deferred && effect.Operation.Syntax.SpanStart >= loopEnd) return false;
                if (effect.CallSite is { } call && call.Syntax.SyntaxTree == invocation.Syntax.SyntaxTree && call.Syntax.Span == invocation.Syntax.Span) continue;
                if (nonlocalBranches || effect.Deferred || effect.Operation.Syntax.SpanStart < invocation.Syntax.SpanStart ||
                    invocation.Syntax.Span.Contains(effect.Operation.Syntax.Span) || loopEnd != invocation.Syntax.Span.End) return true;
            }
            return false;
        }
    }

    internal sealed class CallEffect
    {
        internal CallEffect(IOperation operation, IInvocationOperation? callSite, bool deferred)
        { Operation = operation; CallSite = callSite; Deferred = deferred; }
        internal IOperation Operation { get; }
        internal IInvocationOperation? CallSite { get; }
        internal bool Deferred { get; }
    }

    private sealed class CallEffectScanner
    {
        private readonly IOperation root;
        private readonly int limit;
        private readonly CancellationToken cancellationToken;
        private readonly Dictionary<ISymbol, ISymbol> parents = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ISymbol, List<CallEffect>> slots = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ISymbol, List<CallEffect>> contents = new(SymbolEqualityComparer.Default);
        private bool nonlocalBranches;
        private int steps;

        internal CallEffectScanner(IOperation root, int limit, CancellationToken cancellationToken)
        { this.root = root; this.limit = limit; this.cancellationToken = cancellationToken; }

        internal CallEffectSummary Scan()
        {
            var stack = new Stack<(IOperation Operation, bool Deferred)>();
            stack.Push((root, false));
            while (stack.Count != 0)
            {
                Step();
                var (operation, deferred) = stack.Pop();
                deferred |= operation != root && operation is IAnonymousFunctionOperation or ILocalFunctionOperation;
                var cause = new CallEffect(operation, operation as IInvocationOperation ?? operation.Parent as IInvocationOperation, deferred);
                switch (operation)
                {
                    case IAssignmentOperation assignment:
                        if (assignment.Target is ILocalReferenceOperation { Local.RefKind: not RefKind.None }) Content(assignment.Target, cause);
                        else Storage(assignment.Target, cause);
                        if (assignment is ISimpleAssignmentOperation simple)
                        {
                            Alias(simple.Target, simple.Value);
                            if (simple.IsRef) Content(simple.Value, cause);
                        }
                        break;
                    case IIncrementOrDecrementOperation increment:
                        Storage(increment.Target, cause);
                        break;
                    case IVariableDeclaratorOperation { Initializer.Value: { } value } declaration:
                        foreach (var symbol in Roots(value)) Union(declaration.Symbol, symbol);
                        if (declaration.Symbol.RefKind != RefKind.None) Content(value, cause);
                        break;
                    case IArgumentOperation argument when argument.Parameter is { RefKind: not RefKind.None }:
                        Storage(argument.Value, cause);
                        Content(argument.Value, cause);
                        break;
                    case IAddressOfOperation address:
                        Storage(address.Reference, cause);
                        Content(address.Reference, cause);
                        break;
                    case IInvocationOperation call:
                        if (call.Instance is { } receiver) Content(receiver, cause);
                        foreach (var argument in call.Arguments)
                        {
                            Step();
                            if (argument.Value.Type?.IsReferenceType == true && !ReadOnlyBoundary(argument.Parameter)) Content(argument.Value, cause);
                        }
                        break;
                    case IObjectCreationOperation creation:
                        foreach (var argument in creation.Arguments)
                        {
                            Step();
                            if (argument.Value.Type?.IsReferenceType == true && !ReadOnlyBoundary(argument.Parameter)) Content(argument.Value, cause);
                        }
                        break;
                    case IConversionOperation { Conversion.IsUserDefined: true } conversion:
                        Content(conversion.Operand, cause);
                        break;
                    case IPropertyReferenceOperation property:
                        // A property is executable code. No arbitrary accessor is assumed pure.
                        if (property.Instance is { } owner) Content(owner, cause);
                        break;
                    case IBranchOperation branch when branch.Syntax is GotoStatementSyntax:
                        nonlocalBranches = true;
                        break;
                }
                foreach (var child in operation.ChildOperations) stack.Push((child, deferred));
            }
            // Every possible reference-preserving local alias joins one conservative component.
            // Slot writes stay separate; content mutation through any component member invalidates
            // all source aliases without equating the values of reassigned local cells.
            var grouped = new Dictionary<ISymbol, List<CallEffect>>(SymbolEqualityComparer.Default);
            foreach (var pair in contents)
            {
                Step();
                var representative = Find(pair.Key);
                if (!grouped.TryGetValue(representative, out var list)) grouped.Add(representative, list = new List<CallEffect>());
                list.AddRange(pair.Value);
            }
            var expanded = ImmutableDictionary.CreateBuilder<ISymbol, ImmutableArray<CallEffect>>(SymbolEqualityComparer.Default);
            foreach (var symbol in parents.Keys.ToArray())
            {
                Step();
                if (grouped.TryGetValue(Find(symbol), out var list)) expanded[symbol] = Ordered(list);
            }
            return new CallEffectSummary(slots.ToImmutableDictionary(pair => pair.Key, pair => Ordered(pair.Value), SymbolEqualityComparer.Default),
                expanded.ToImmutable(), nonlocalBranches, steps);
        }

        private static ImmutableArray<CallEffect> Ordered(IEnumerable<CallEffect> effects) => effects.Distinct()
            .OrderByDescending(effect => effect.Deferred).ThenBy(effect => effect.Operation.Syntax.SpanStart)
            .ThenBy(effect => effect.Operation.Syntax.Span.Length).ToImmutableArray();

        private static bool ReadOnlyBoundary(IParameterSymbol? parameter) => parameter is not null &&
            SymbolFacts.HasAttribute(parameter, "Borrowed") && SymbolFacts.HasAttribute(parameter, "DoesNotEscape");

        private void Storage(IOperation target, CallEffect cause)
        {
            Step();
            switch (target)
            {
                case IParameterReferenceOperation parameter: Add(slots, parameter.Parameter, cause); break;
                case ILocalReferenceOperation local: Add(slots, local.Local, cause); break;
                case IParenthesizedOperation parentheses: Storage(parentheses.Operand, cause); break;
                case IConversionOperation conversion: Storage(conversion.Operand, cause); break;
                case ITupleOperation tuple:
                    foreach (var element in tuple.Elements) Storage(element, cause);
                    break;
                default: Content(target, cause); break;
            }
        }

        private void Content(IOperation value, CallEffect cause)
        {
            foreach (var symbol in Roots(value)) { Find(symbol); Add(contents, symbol, cause); }
        }

        private void Alias(IOperation target, IOperation value)
        {
            var targets = Roots(target).ToArray();
            foreach (var source in Roots(value))
            foreach (var destination in targets) { Step(); Union(source, destination); }
        }

        private IEnumerable<ISymbol> Roots(IOperation value)
        {
            var pending = new Stack<IOperation>();
            pending.Push(value);
            while (pending.Count != 0)
            {
                Step();
                switch (pending.Pop())
                {
                    case ILocalReferenceOperation local: yield return local.Local; break;
                    case IParameterReferenceOperation parameter: yield return parameter.Parameter; break;
                    case IParenthesizedOperation parentheses: pending.Push(parentheses.Operand); break;
                    case IConversionOperation conversion: pending.Push(conversion.Operand); break;
                    case IArrayElementReferenceOperation element: pending.Push(element.ArrayReference); break;
                    case IFieldReferenceOperation { Instance: { } owner }: pending.Push(owner); break;
                    case IPropertyReferenceOperation { Instance: { } owner }: pending.Push(owner); break;
                    case IConditionalOperation conditional:
                        pending.Push(conditional.WhenTrue);
                        if (conditional.WhenFalse is { } alternative) pending.Push(alternative);
                        break;
                }
            }
        }

        private ISymbol Find(ISymbol symbol)
        {
            Step();
            if (!parents.TryGetValue(symbol, out var parent)) { parents.Add(symbol, symbol); return symbol; }
            var representative = parent;
            while (!SymbolEqualityComparer.Default.Equals(parents[representative], representative)) { Step(); representative = parents[representative]; }
            parents[symbol] = representative;
            return representative;
        }

        private void Union(ISymbol first, ISymbol second)
        {
            var a = Find(first);
            var b = Find(second);
            if (!SymbolEqualityComparer.Default.Equals(a, b)) parents[b] = a;
        }

        private static void Add(Dictionary<ISymbol, List<CallEffect>> map, ISymbol symbol, CallEffect cause)
        {
            if (!map.TryGetValue(symbol, out var list)) map.Add(symbol, list = new List<CallEffect>());
            list.Add(cause);
        }

        private void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (steps++ >= limit) throw new BindingBudgetException("direct-call mutation-summary steps");
        }
    }
}
