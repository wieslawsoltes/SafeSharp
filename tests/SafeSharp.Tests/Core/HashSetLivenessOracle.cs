// SafeSharp fd66dc0 HashSet solver with explicit reference-address uses: independent correctness and benchmark oracle.
// Keep this HashSet solver separate from the optimized solver's indexed transfer helpers.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using SafeSharp.Core;

namespace SafeSharp.TestSupport;

/// <summary>Backward fixed-point liveness supplies non-lexical loan expiration at instruction boundaries.</summary>
// Conceptual source: https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/lib.rs
internal sealed class HashSetLivenessOracle
{
    internal Dictionary<int, HashSet<string>> LiveIn { get; } = new();
    internal Dictionary<int, HashSet<string>> LiveOut { get; } = new();
    internal Dictionary<int, HashSet<string>[]> Before { get; } = new();
    internal Dictionary<int, HashSet<string>[]> After { get; } = new();
    internal bool BudgetExceeded { get; private set; }

    internal static HashSetLivenessOracle Compute(IReadOnlyDictionary<int, FlowBlock> blocks,
        IReadOnlyDictionary<int, List<int>> predecessors, int budget, CancellationToken cancellationToken)
    {
        var result = new HashSetLivenessOracle();
        var references = blocks.Values.SelectMany(block => block.Instructions)
            .Where(instruction => instruction.Kind is InstructionKind.BindReference or InstructionKind.MergeReference or InstructionKind.BindAggregateElementReference)
            .Select(instruction => instruction.Target!).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in blocks.Keys)
        {
            result.LiveIn[id] = new HashSet<string>(StringComparer.Ordinal);
            result.LiveOut[id] = new HashSet<string>(StringComparer.Ordinal);
        }
        var queue = new Queue<int>(blocks.Keys.Reverse());
        var queued = new HashSet<int>(queue);
        var iterations = 0;
        while (queue.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++iterations > budget) { result.BudgetExceeded = true; break; }
            var id = queue.Dequeue();
            queued.Remove(id);
            var block = blocks[id];
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var successor in block.Successors)
                if (result.LiveIn.TryGetValue(successor, out var successorLive)) live.UnionWith(successorLive);
            result.LiveOut[id] = new HashSet<string>(live, StringComparer.Ordinal);
            for (var index = block.Instructions.Length - 1; index >= 0; index--) Transfer(block.Instructions[index], live, references);
            if (result.LiveIn[id].SetEquals(live)) continue;
            result.LiveIn[id] = live;
            foreach (var predecessor in predecessors[id])
                if (queued.Add(predecessor)) queue.Enqueue(predecessor);
        }
        foreach (var pair in blocks)
        {
            var block = pair.Value;
            var before = new HashSet<string>[block.Instructions.Length];
            var after = new HashSet<string>[block.Instructions.Length];
            var live = new HashSet<string>(result.LiveOut[pair.Key], StringComparer.Ordinal);
            for (var index = block.Instructions.Length - 1; index >= 0; index--)
            {
                after[index] = new HashSet<string>(live, StringComparer.Ordinal);
                Transfer(block.Instructions[index], live, references);
                before[index] = new HashSet<string>(live, StringComparer.Ordinal);
            }
            result.Before[pair.Key] = before;
            result.After[pair.Key] = after;
        }
        return result;
    }

    private static void Transfer(FlowInstruction instruction, HashSet<string> live, string[] references)
    {
        if (instruction.Target is not null && instruction.Kind != InstructionKind.Nop)
        {
            live.Remove(instruction.Target);
            live.RemoveWhere(place => IsStrictDescendant(place, instruction.Target));
        }
        if (instruction.Source is not null && instruction.Kind != InstructionKind.Nop) live.Add(instruction.Source);
        if (instruction.Target is { } target && (instruction.Kind is InstructionKind.MergeAlias or InstructionKind.MergeReference ||
            instruction.Kind is not (InstructionKind.Nop or InstructionKind.BindReference or InstructionKind.BindAggregateElementReference or InstructionKind.ReleaseReference) &&
            references.Any(reference => reference == target || IsStrictDescendant(target, reference)))) live.Add(target);
        if (instruction.Owner is not null) live.Add(instruction.Owner);
    }

    // Freeze the original structural relation too; do not let an optimized production helper
    // change both sides of the differential test together.
    private static bool IsStrictDescendant(string child, string parent) =>
        child.Length > parent.Length && child.StartsWith(parent, StringComparison.Ordinal)
        && (parent.Length == 0 || child[parent.Length] == '.' || child[parent.Length] == '[');
}
