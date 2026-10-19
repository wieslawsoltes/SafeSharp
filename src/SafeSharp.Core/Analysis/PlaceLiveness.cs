using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SafeSharp.Core;

/// <summary>Backward fixed-point liveness supplies non-lexical loan expiration at instruction boundaries.</summary>
// Conceptual source: https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/lib.rs
internal sealed class PlaceLiveness
{
    internal Dictionary<int, HashSet<string>> LiveIn { get; } = new();
    internal Dictionary<int, HashSet<string>> LiveOut { get; } = new();
    internal Dictionary<int, HashSet<string>[]> Before { get; } = new();
    internal Dictionary<int, HashSet<string>[]> After { get; } = new();
    internal bool BudgetExceeded { get; private set; }

    internal static PlaceLiveness Compute(IReadOnlyDictionary<int, FlowBlock> blocks,
        IReadOnlyDictionary<int, List<int>> predecessors, int budget, CancellationToken cancellationToken)
    {
        var result = new PlaceLiveness();
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
            for (var index = block.Instructions.Length - 1; index >= 0; index--) Transfer(block.Instructions[index], live);
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
                Transfer(block.Instructions[index], live);
                before[index] = new HashSet<string>(live, StringComparer.Ordinal);
            }
            result.Before[pair.Key] = before;
            result.After[pair.Key] = after;
        }
        return result;
    }

    private static void Transfer(FlowInstruction instruction, HashSet<string> live)
    {
        if (instruction.Target is not null && instruction.Kind != InstructionKind.Nop)
        {
            live.Remove(instruction.Target);
            live.RemoveWhere(place => PlaceRelations.IsStrictDescendant(place, instruction.Target));
        }
        if (instruction.Source is not null && instruction.Kind != InstructionKind.Nop) live.Add(instruction.Source);
        if (instruction.Kind == InstructionKind.MergeAlias && instruction.Target is not null) live.Add(instruction.Target);
        if (instruction.Owner is not null) live.Add(instruction.Owner);
    }
}
