using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SafeSharp.Core;

/// <summary>Backward fixed-point liveness supplies non-lexical loan expiration at instruction boundaries.</summary>
/// <remarks>
/// Adapts rustc's indexed bitset and deduplicating worklist design in original C#.
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_data_structures/src/work_queue.rs
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_mir_dataflow/src/framework/mod.rs
/// </remarks>
internal sealed class PlaceLiveness
{
    internal Dictionary<int, PlaceSet> LiveIn { get; } = new();
    internal Dictionary<int, PlaceSet> LiveOut { get; } = new();
    internal Dictionary<int, PlaceSet[]> Before { get; } = new();
    internal Dictionary<int, PlaceSet[]> After { get; } = new();
    internal bool BudgetExceeded { get; private set; }

    internal static PlaceLiveness Compute(IReadOnlyDictionary<int, FlowBlock> blocks,
        IReadOnlyDictionary<int, List<int>> predecessors, int budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new PlaceLiveness();
        var blockArray = blocks.Values.ToArray();
        var universe = new PlaceUniverse(blockArray, cancellationToken);
        var indices = new Dictionary<int, int>(blockArray.Length);
        for (var index = 0; index < blockArray.Length; index++) indices.Add(blockArray[index].Id, index);

        var compiled = new BlockEffects[blockArray.Length];
        var inputs = new PlaceSet[blockArray.Length];
        var outputs = new PlaceSet[blockArray.Length];
        var queue = new Queue<int>(blockArray.Length);
        var queued = new bool[blockArray.Length];
        for (var index = blockArray.Length - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = blockArray[index];
            compiled[index] = new BlockEffects(block, universe, indices, predecessors[block.Id], cancellationToken);
            inputs[index] = outputs[index] = universe.Empty;
            queue.Enqueue(index);
            queued[index] = true;
        }

        // Reuse the dense scratch buffer between blocks. Frozen boundary sets never alias it.
        var scratch = new PlaceSetBuilder(universe.WordCount);
        var iterations = 0;
        while (queue.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++iterations > budget) { result.BudgetExceeded = true; break; }
            var index = queue.Dequeue();
            queued[index] = false;
            var block = compiled[index];
            scratch.Clear();
            foreach (var successor in block.Successors) inputs[successor].UnionInto(scratch, cancellationToken);
            if (!outputs[index].Matches(scratch, cancellationToken))
                outputs[index] = PlaceSet.Freeze(universe, scratch, cancellationToken);

            // SafeSharp's IR has fixed GEN/KILL effects, so a block transfer is two word operations.
            // This specialization is measured independently; rustc 1.91 uses its generic Analysis trait.
            block.Killed.ExceptFrom(scratch, cancellationToken);
            block.Generated.UnionInto(scratch, cancellationToken);
            if (inputs[index].Matches(scratch, cancellationToken)) continue;
            inputs[index] = PlaceSet.Freeze(universe, scratch, cancellationToken);
            foreach (var predecessor in block.Predecessors)
            {
                if (queued[predecessor]) continue;
                queued[predecessor] = true;
                queue.Enqueue(predecessor);
            }
        }

        for (var blockIndex = 0; blockIndex < blockArray.Length; blockIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = blockArray[blockIndex];
            result.LiveIn[block.Id] = inputs[blockIndex];
            result.LiveOut[block.Id] = outputs[blockIndex];
            // No partial instruction facts are consumed on an exhausted solve.
            if (result.BudgetExceeded) continue;
            var effects = compiled[blockIndex].Instructions;
            var before = new PlaceSet[effects.Length];
            var after = new PlaceSet[effects.Length];
            var live = outputs[blockIndex];
            scratch.Clear();
            live.UnionInto(scratch, cancellationToken);
            for (var index = effects.Length - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                after[index] = live;
                if (effects[index].Apply(scratch, cancellationToken) && !live.Matches(scratch, cancellationToken))
                    live = PlaceSet.Freeze(universe, scratch, cancellationToken);
                before[index] = live;
            }
            result.Before[block.Id] = before;
            result.After[block.Id] = after;
        }
        return result;
    }

    private sealed class BlockEffects
    {
        internal BlockEffects(FlowBlock block, PlaceUniverse universe, Dictionary<int, int> indices,
            List<int> predecessors, CancellationToken cancellationToken)
        {
            Successors = block.Successors.Where(indices.ContainsKey).Select(id => indices[id]).ToArray();
            Predecessors = predecessors.Select(id => indices[id]).ToArray();
            Instructions = new InstructionEffect[block.Instructions.Length];
            var generated = new PlaceSetBuilder(universe.WordCount);
            var killed = new PlaceSetBuilder(universe.WordCount);
            for (var index = block.Instructions.Length - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var effect = new InstructionEffect(block.Instructions[index], universe);
                Instructions[index] = effect;
                effect.Apply(generated, cancellationToken);
                effect.Kill?.AddTo(killed, cancellationToken);
            }
            Generated = PlaceSet.Freeze(universe, generated, cancellationToken);
            Killed = PlaceSet.Freeze(universe, killed, cancellationToken);
        }

        internal int[] Successors { get; }
        internal int[] Predecessors { get; }
        internal InstructionEffect[] Instructions { get; }
        internal PlaceSet Generated { get; }
        internal PlaceSet Killed { get; }
    }

    private readonly struct InstructionEffect
    {
        internal InstructionEffect(FlowInstruction instruction, PlaceUniverse universe)
        {
            var target = instruction.Kind == InstructionKind.Nop ? -1 : universe.Index(instruction.Target);
            Kill = target < 0 ? null : universe.KillFor(target);
            Source = instruction.Kind == InstructionKind.Nop ? -1 : universe.Index(instruction.Source);
            MergeTarget = instruction.Kind is InstructionKind.MergeAlias or InstructionKind.MergeReference ? target : -1;
            Owner = universe.Index(instruction.Owner);
        }

        internal ProjectionKill? Kill { get; }
        private int Source { get; }
        private int MergeTarget { get; }
        private int Owner { get; }

        internal bool Apply(PlaceSetBuilder words, CancellationToken cancellationToken) =>
            (Kill?.RemoveFrom(words, cancellationToken) ?? false)
            | words.Add(Source)
            | words.Add(MergeTarget)
            | words.Add(Owner);
    }
}
