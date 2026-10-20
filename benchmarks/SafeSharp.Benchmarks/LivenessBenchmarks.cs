using SafeSharp.Core;
using SafeSharp.TestSupport;

namespace SafeSharp.Benchmarks;

internal static class LivenessBenchmarks
{
    private static readonly SourceSite Site = new("benchmark.cs", 0, 1, 0, 0);

    internal static IEnumerable<BenchmarkCase> Create()
    {
        yield return Make("liveness/tiny", "4 blocks, 32 instructions, 16 places; small-method setup included", General(4, 8, 16, false));
        yield return Make("liveness/dense-joins", "64 blocks, 4096 instructions, 127 distinct places with dense live boundaries", DenseJoins());
        yield return Make("liveness/projection-loop", "96 blocks, 3072 instructions, projected places and backward edges", General(96, 32, 512, true));
        yield return Make("liveness/sparse-temporaries", "1 block, 8192 instructions, 4096 short-lived distinct temporaries", SparseTemporaries());
        yield return Make("liveness/unchanged-boundaries", "32 blocks, 4096 no-op boundaries and one live input", NoOps());
    }

    private static BenchmarkCase Make(string name, string details, Dictionary<int, FlowBlock> blocks)
    {
        var predecessors = blocks.Keys.ToDictionary(id => id, _ => new List<int>());
        foreach (var block in blocks.Values)
        foreach (var successor in block.Successors) predecessors[successor].Add(block.Id);
        const int budget = 1_000_000;
        return new(name, details,
            () => HashSetLivenessOracle.Compute(blocks, predecessors, budget, default),
            () => PlaceLiveness.Compute(blocks, predecessors, budget, default),
            () =>
            {
                var before = HashSetLivenessOracle.Compute(blocks, predecessors, budget, default);
                var after = PlaceLiveness.Compute(blocks, predecessors, budget, default);
                if (before.BudgetExceeded || after.BudgetExceeded) throw new InvalidOperationException(name + " exhausted its liveness budget.");
                foreach (var block in blocks.Values)
                {
                    if (!before.LiveIn[block.Id].SetEquals(after.LiveIn[block.Id]) || !before.LiveOut[block.Id].SetEquals(after.LiveOut[block.Id]))
                        throw new InvalidOperationException(name + " block liveness differs.");
                    for (var index = 0; index < block.Instructions.Length; index++)
                        if (!before.Before[block.Id][index].SetEquals(after.Before[block.Id][index]) || !before.After[block.Id][index].SetEquals(after.After[block.Id][index]))
                            throw new InvalidOperationException(name + " instruction liveness differs.");
                }
            });
    }

    private static Dictionary<int, FlowBlock> General(int blockCount, int instructionCount, int placeCount, bool loops)
    {
        var names = Enumerable.Range(0, placeCount).Select(index => index % 4 == 0 ? "p" + index / 4 : "p" + index / 4 + ".field" + index % 4).ToArray();
        var blocks = new Dictionary<int, FlowBlock>();
        for (var block = 0; block < blockCount; block++)
        {
            var instructions = new List<FlowInstruction>();
            for (var index = 0; index < instructionCount; index++)
            {
                var place = names[(block * 13 + index * 7) % names.Length];
                instructions.Add(index % 5 == 0
                    ? new(InstructionKind.Alias, Site, source: names[(block * 11 + index * 3) % names.Length], target: place)
                    : new(InstructionKind.Read, Site, source: place));
            }
            var successors = new List<int>();
            if (block + 1 < blockCount) successors.Add(block + 1);
            if (block + 3 < blockCount && block % 3 == 0) successors.Add(block + 3);
            if (loops && block > 3 && block % 11 == 0) successors.Add(block - 3);
            blocks.Add(block, new(block, instructions, successors));
        }
        return blocks;
    }

    private static Dictionary<int, FlowBlock> DenseJoins()
    {
        var blocks = new Dictionary<int, FlowBlock>();
        for (var block = 0; block < 64; block++)
        {
            var instructions = Enumerable.Range(0, 64).Select(index => new FlowInstruction(InstructionKind.Read, Site, source: "p" + ((index + block) % 128)));
            var successors = new List<int>();
            if (block + 1 < 64) successors.Add(block + 1);
            if (block + 5 < 64) successors.Add(block + 5);
            blocks.Add(block, new(block, instructions, successors));
        }
        return blocks;
    }

    private static Dictionary<int, FlowBlock> SparseTemporaries()
    {
        var instructions = Enumerable.Range(0, 4096).SelectMany(index => new[]
        {
            new FlowInstruction(InstructionKind.Allocate, Site, target: "$t:" + index),
            new FlowInstruction(InstructionKind.Read, Site, source: "$t:" + index),
        });
        return new() { [0] = new(0, instructions, []) };
    }

    private static Dictionary<int, FlowBlock> NoOps()
    {
        var blocks = new Dictionary<int, FlowBlock>();
        for (var block = 0; block < 32; block++)
        {
            var instructions = Enumerable.Range(0, 128).Select(_ => new FlowInstruction(InstructionKind.Nop, Site)).ToList();
            if (block == 31) instructions.Add(new(InstructionKind.Read, Site, source: "input"));
            blocks.Add(block, new(block, instructions, block == 31 ? [] : [block + 1]));
        }
        return blocks;
    }
}
