using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Analysis;

namespace SafeSharp.Analyzers.Semantics;

internal sealed partial class SynchronizationAnalysis
{
    private readonly Dictionary<IOperation, LockStatementSyntax> lexicalLockOperations = new();
    private readonly Dictionary<int, (LockStatementSyntax Syntax, IOperation Operation)> lexicalLocks = new();

    private void DiscoverLexicalLocks()
    {
        // Roslyn 4.14's public CFG can expose C# 13 Lock statements as an Invalid placeholder,
        // although the bound ILockOperation and compilation are valid. Normalize only that
        // compiler-authenticated construct. An arbitrary Invalid operation never grants a lock.
        foreach (var block in graph.Blocks)
        {
            var operations = new Stack<IOperation>(block.Operations);
            while (operations.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = operations.Pop();
                if (operation is IInvalidOperation && operation.Parent is IExpressionStatementOperation { Syntax: LockStatementSyntax syntax } &&
                    contracts.Compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax, cancellationToken) is ILockOperation locked &&
                    SynchronizationContracts.Same(locked.LockedValue.Type, contracts.ThreadingLock))
                {
                    lexicalLockOperations[operation] = syntax;
                    lexicalLocks[syntax.SpanStart] = (syntax, operation);
                }
                foreach (var child in operation.ChildOperations) operations.Push(child);
            }
        }
    }

    private bool TryEnterLexicalLock(IInvalidOperation operation, SynchronizationState state, int depth, out GuardValue value)
    {
        value = GuardValue.Unknown;
        if (!lexicalLockOperations.TryGetValue(operation, out var syntax)) return false;
        foreach (var child in operation.ChildOperations) value = Evaluate(child, state, depth: depth + 1);
        MayThrow(state); // EnterScope grants its capability only after normal completion.
        var key = new GuardKey(value.Identity, GuardLockKind.ThreadingLock);
        var before = state.Get(key);
        Acquire(key, operation, state, false);
        state.LexicalScopes[syntax.SpanStart] = new LexicalGuard(key, before);
        return true;
    }

    private void CloseLexicalScopes(SynchronizationState state, IOperation? operation)
    {
        if (state.LexicalScopes.Count == 0) return;
        if (operation is not null && context?.Filter is { } filter && context.Ordinal >= filter.Region.FirstBlockOrdinal && context.Ordinal <= filter.Region.LastBlockOrdinal)
            return; // CLR filters execute before unwinding a lexical guard, including outer filters.
        foreach (var id in state.LexicalScopes.Keys.OrderByDescending(id => lexicalLocks[id].Syntax.SpanStart).ToArray())
        {
            var lexical = lexicalLocks[id];
            if (operation is not null && lexical.Syntax.Statement.Span.Contains(operation.Syntax.Span)) continue;
            var scope = state.LexicalScopes[id];
            if (scope.Definite) Release(scope.Key, lexical.Operation, state);
            else
            {
                // An unresolved optional scope cannot discharge an unrelated acquisition from
                // another incoming path. Retain its possible depth and withhold must-capability.
                var hold = state.Get(scope.Key);
                state.Set(scope.Key, new GuardHold(0, hold.Maximum, hold.Acquisition));
            }
            state.LexicalScopes.Remove(id);
        }
    }

    private void CloseLexicalScopesAtDestination(SynchronizationState state, SourceControlFlowRouter.Context destination)
    {
        if (state.LexicalScopes.Count == 0 || destination.Filter is not null) return;
        if (router.IsBoundary(destination) || router.IsNormalExit(destination)) { CloseLexicalScopes(state, null); return; }
        var block = graph.Blocks[destination.Ordinal];
        var first = block.Operations.FirstOrDefault() ?? block.BranchValue;
        if (first is null) return;
        var previous = context;
        context = destination;
        CloseLexicalScopes(state, first);
        context = previous;
    }
}
