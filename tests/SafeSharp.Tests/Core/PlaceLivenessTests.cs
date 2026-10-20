using System.Collections;
using SafeSharp.Core;
using SafeSharp.TestSupport;

namespace SafeSharp.Tests.Core;

public sealed class PlaceLivenessTests
{
    private static readonly SourceSite Site = new("liveness.cs", 0, 1, 0, 0);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 63)]
    [InlineData(8, 64)]
    [InlineData(9, 65)]
    [InlineData(12, 127)]
    [InlineData(13, 128)]
    [InlineData(16, 129)]
    [InlineData(31, 257)]
    [InlineData(32, 1025)]
    public void IndexedSolverMatchesIndependentHashSetOracleAcrossRandomGraphs(int blockCount, int placeCount)
    {
        for (var seed = 0; seed < 24; seed++)
        {
            var graph = RandomBlocks(seed, blockCount, placeCount);
            AssertMatchesOracle(graph, 100_000);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(13)]
    [InlineData(25)]
    [InlineData(100)]
    [InlineData(1000)]
    public void WorklistBudgetDecisionsMatchHashSetOracle(int budget)
    {
        for (var seed = 0; seed < 12; seed++) AssertMatchesOracle(RandomBlocks(seed, 13, 129), budget);
    }

    [Fact]
    public void EveryInstructionKindPreservesSourceTargetOwnerAndMergeRules()
    {
        foreach (var kind in Enum.GetValues<InstructionKind>())
        foreach (var source in new[] { "target", "target.child", "source" })
        {
            var instructions = new[]
            {
                new FlowInstruction(kind, Site, target: "target", source: source, owner: "owner",
                    asyncComposition: kind == InstructionKind.BeginAsyncComposition ? AsyncCompositionKind.WhenAll : AsyncCompositionKind.None),
                new FlowInstruction(InstructionKind.Read, Site, source: "target.child"),
                new FlowInstruction(InstructionKind.Read, Site, source: "target[0]"),
                new FlowInstruction(InstructionKind.Read, Site, source: "targetOther"),
            };
            AssertMatchesOracle(new Dictionary<int, FlowBlock> { [7] = new(7, instructions, []) }, 100);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(4097)]
    public void ProjectionKillHonorsFieldAndIndexBoundariesAcrossWordEdges(int padding)
    {
        var children = Enumerable.Range(0, 137).Select(index => "p.field" + index).ToArray();
        var others = new[] { "p!", "p0", "p/", "p\\", "pX", "prefix.p", "P.field", "p]" };
        var names = Enumerable.Range(0, padding).Select(index => "a" + index)
            .Concat(children).Concat(others).Concat(["p", "p[0]", "p[1].value", "p.", "p[", "p.\uffff"]);
        var block = new FlowBlock(-901, new[] { new FlowInstruction(InstructionKind.Forget, Site, target: "p") }
            .Concat(names.Select(name => new FlowInstruction(InstructionKind.Read, Site, source: name))), []);
        var blocks = new Dictionary<int, FlowBlock> { [block.Id] = block };
        var actual = Compute(blocks);
        foreach (var other in others) Assert.Contains(other, actual.Before[block.Id][0]);
        Assert.DoesNotContain(actual.Before[block.Id][0], place => place == "p" || place.StartsWith("p.", StringComparison.Ordinal) || place.StartsWith("p[", StringComparison.Ordinal));
        AssertMatchesOracle(blocks, 100);
    }

    [Fact]
    public void EmptyPlaceKillKeepsItsExistingAllDescendantsMeaning()
    {
        var block = new FlowBlock(0,
        [
            new(InstructionKind.Forget, Site, target: ""),
            new(InstructionKind.Read, Site, source: "anything"),
            new(InstructionKind.Read, Site, source: "x.y"),
            new(InstructionKind.Read, Site, source: "x[1]"),
        ], []);
        var blocks = new Dictionary<int, FlowBlock> { [0] = block };
        Assert.Empty(Compute(blocks).Before[0][0]);
        AssertMatchesOracle(blocks, 100);
    }

    [Fact]
    public void EmptyGraphsAndEmptyBlocksDoNotInventPlaces()
    {
        AssertMatchesOracle(new Dictionary<int, FlowBlock>(), 100);
        AssertMatchesOracle(new Dictionary<int, FlowBlock> { [9] = new(9, [], [9]) }, 100);
    }

    [Fact]
    public void NopUsesOnlyItsOwnerAndUnchangedBoundariesAreSharedImmutably()
    {
        var block = new FlowBlock(0,
        [
            new(InstructionKind.Nop, Site, source: "ignored-source", target: "ignored-target"),
            new(InstructionKind.ReleaseLoan, Site, source: "used", target: "used"),
            new(InstructionKind.Nop, Site, source: "also-ignored", target: "used", owner: "used"),
            new(InstructionKind.Read, Site, source: "used"),
        ], []);
        var liveness = Compute(new Dictionary<int, FlowBlock> { [0] = block });
        Assert.Equal(["used"], liveness.Before[0][0]);
        Assert.Same(liveness.After[0][0], liveness.Before[0][0]);
        Assert.Same(liveness.Before[0][1], liveness.After[0][0]);
        Assert.Same(liveness.Before[0][1], liveness.After[0][1]);
        ISet<string> frozen = liveness.Before[0][0];
        Assert.True(frozen.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => frozen.Add("other"));
        Assert.Throws<NotSupportedException>(() => frozen.Remove("used"));
        Assert.Throws<NotSupportedException>(() => frozen.Clear());
        Assert.Throws<NotSupportedException>(() => frozen.UnionWith(["other"]));
        Assert.Equal(["used"], frozen);
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(65, false)]
    [InlineData(129, false)]
    [InlineData(4097, false)]
    [InlineData(4097, true)]
    public void DenseAndSparseFrozenSetsPreserveOrdinalMembershipAndCopySemantics(int count, bool sparse)
    {
        var names = Enumerable.Range(0, count).Select(index => "v" + index.ToString("D5")).ToArray();
        var block = new FlowBlock(0, names.Select(name => new FlowInstruction(InstructionKind.Read, Site, source: name)), []);
        var universe = new PlaceUniverse([block], default);
        var words = new ulong[universe.WordCount];
        var expected = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            if (sparse && index is not (0 or 64 or 128 or 4096)) continue;
            words[index >> 6] |= 1UL << (index & 63);
            expected.Add(names[index]);
        }
        var frozen = PlaceSet.Freeze(universe, words, default);
        Array.Clear(words);
        Assert.Equal(expected.Count, frozen.Count);
        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), frozen.ToArray());
        foreach (var name in names) Assert.Equal(expected.Contains(name), frozen.Contains(name));
        Assert.DoesNotContain("unknown", frozen);
        Assert.True(frozen.SetEquals(expected));
        Assert.True(frozen.IsSubsetOf(names));
        Assert.True(frozen.IsSupersetOf(expected));
        Assert.Equal(expected.Count > 0, frozen.Overlaps(names));
        var copy = new string[frozen.Count + 2];
        frozen.CopyTo(copy, 1);
        Assert.Null(copy[0]);
        Assert.Null(copy[^1]);
        Assert.Equal(frozen.ToArray(), copy.Skip(1).Take(frozen.Count));
        Assert.Throws<ArgumentException>(() => frozen.CopyTo([], 0));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(8193)]
    public void SparseOccupancySummaryMatchesScalarRangeOracleAcrossBothWordLevels(int count)
    {
        var names = Enumerable.Range(0, count).Select(index => "v" + index.ToString("D5")).ToArray();
        var universe = new PlaceUniverse([new FlowBlock(0, names.Select(name => new FlowInstruction(InstructionKind.Read, Site, source: name)), [])], default);
        var buffer = new PlaceSetBuilder(universe.WordCount);
        var expected = new bool[count];
        var random = new Random(count);
        for (var iteration = 0; iteration < 80; iteration++)
        {
            var start = iteration == 0 ? 0 : random.Next(count + 1);
            var end = iteration == 0 ? count : random.Next(start, count + 1);
            var value = iteration == 0 || random.Next(2) == 0;
            buffer.SetRange(start, end, value, default);
            for (var index = start; index < end; index++) expected[index] = value;
            var frozen = PlaceSet.Freeze(universe, buffer, default);
            Assert.Equal(expected.Count(present => present), buffer.Count);
            Assert.True(frozen.Matches(buffer, default));
            Assert.Equal(names.Where((_, index) => expected[index]), frozen.ToArray());
            if (iteration % 17 != 16) continue;
            buffer.Clear();
            Array.Clear(expected);
            Assert.Equal(0, buffer.Count);
            Assert.Equal(0, buffer.OccupiedWords);
            Assert.Empty(PlaceSet.Freeze(universe, buffer, default));
        }
    }

    [Fact]
    public void CancellationDuringDomainDiscoveryStopsBeforeAnySolverResult()
    {
        using var cancellation = new CancellationTokenSource();
        var block = new FlowBlock(1, [new(InstructionKind.Read, Site, source: "v")], []);
        var blocks = new CancelOnValuesDictionary(block, cancellation);
        Assert.Throws<OperationCanceledException>(() => PlaceLiveness.Compute(blocks, new Dictionary<int, List<int>> { [1] = [] }, 100, cancellation.Token));
    }

    [Fact]
    public void PreCancelledTokenStopsEmptyAndNonemptyGraphs()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PlaceLiveness.Compute(new Dictionary<int, FlowBlock>(), new Dictionary<int, List<int>>(), 100, cancellation.Token));
        var blocks = RandomBlocks(0, 4, 8);
        Assert.Throws<OperationCanceledException>(() => PlaceLiveness.Compute(blocks, Predecessors(blocks), 100, cancellation.Token));
    }

    [Fact]
    public void PublicOwnershipSnapshotsKeepExactReferenceLiveness()
    {
        var blocks = new Dictionary<int, FlowBlock>
        {
            [37] = new(37, [new(InstructionKind.Allocate, Site, target: "owner", resourceId: "r"), new(InstructionKind.BorrowShared, Site, target: "borrow", source: "owner")], [7, 1024]),
            [7] = new(7, [new(InstructionKind.Read, Site, source: "borrow.child")], [9]),
            [1024] = new(1024, [new(InstructionKind.Alias, Site, source: "borrow", target: "alias"), new(InstructionKind.Read, Site, source: "alias")], [9]),
            [9] = new(9, [new(InstructionKind.Dispose, Site, source: "owner")], []),
        };
        var expected = HashSetLivenessOracle.Compute(blocks, Predecessors(blocks), 1000, default);
        var result = new OwnershipAnalysisEngine().Analyze(new("M", blocks.Values, 37, Site), new(captureSnapshots: true));
        Assert.False(result.BudgetExceeded);
        Assert.Empty(result.Issues);
        foreach (var block in result.Blocks)
        {
            Assert.Equal(expected.LiveIn[block.Id].OrderBy(name => name, StringComparer.Ordinal), block.LiveIn);
            Assert.Equal(expected.LiveOut[block.Id].OrderBy(name => name, StringComparer.Ordinal), block.LiveOut);
            foreach (var step in block.Steps)
            {
                Assert.Equal(expected.Before[block.Id][step.Index].OrderBy(name => name, StringComparer.Ordinal), step.LiveBefore);
                Assert.Equal(expected.After[block.Id][step.Index].OrderBy(name => name, StringComparer.Ordinal), step.LiveAfter);
            }
        }
    }

    private static Dictionary<int, FlowBlock> RandomBlocks(int seed, int blockCount, int placeCount)
    {
        var random = new Random(seed);
        var kinds = Enum.GetValues<InstructionKind>();
        var ids = Enumerable.Range(0, blockCount).Select(index => index * 1009 - 711).OrderBy(_ => random.Next()).ToArray();
        var places = Enumerable.Range(0, placeCount).Select(index => (index % 7) switch
        {
            0 => "root" + index / 7,
            1 => "root" + index / 7 + ".field",
            2 => "root" + index / 7 + "[0]",
            3 => "root" + index / 7 + ".field.nested",
            4 => "root" + index / 7 + "[0].field",
            5 => "root" + index / 7 + "Extra",
            _ => "$t:call-argument:" + index,
        }).ToArray();
        var blocks = new Dictionary<int, FlowBlock>();
        foreach (var id in ids)
        {
            var instructions = Enumerable.Range(0, random.Next(0, 25)).Select(_ =>
            {
                var kind = kinds[random.Next(kinds.Length)];
                var target = places[random.Next(places.Length)];
                var source = places[random.Next(places.Length)];
                var owner = random.Next(3) == 0 ? places[random.Next(places.Length)] : null;
                return new FlowInstruction(kind, Site, target: target, source: source,
                    owner: kind == InstructionKind.SetAggregateElement ? owner ?? source : owner,
                    asyncComposition: kind == InstructionKind.BeginAsyncComposition ? AsyncCompositionKind.WhenAll : AsyncCompositionKind.None);
            }).ToArray();
            var successors = Enumerable.Range(0, random.Next(0, 5)).Select(_ => ids[random.Next(ids.Length)]);
            blocks.Add(id, new(id, instructions, successors));
        }
        return blocks;
    }

    private static Dictionary<int, List<int>> Predecessors(IReadOnlyDictionary<int, FlowBlock> blocks)
    {
        var predecessors = blocks.Keys.ToDictionary(id => id, _ => new List<int>());
        foreach (var block in blocks.Values)
        foreach (var successor in block.Successors) predecessors[successor].Add(block.Id);
        return predecessors;
    }

    private static PlaceLiveness Compute(IReadOnlyDictionary<int, FlowBlock> blocks) => PlaceLiveness.Compute(blocks, Predecessors(blocks), 100_000, default);

    private static void AssertMatchesOracle(IReadOnlyDictionary<int, FlowBlock> blocks, int budget)
    {
        var predecessors = Predecessors(blocks);
        // Preserve the frozen pre-ref-storage solver as an independent oracle. The newer
        // MergeReference opcode has the same structural liveness as its existing MergeAlias
        // instruction: it reads the previous target as well as the new source.
        var oracleBlocks = blocks.ToDictionary(pair => pair.Key, pair => new FlowBlock(pair.Value.Id,
            pair.Value.Instructions.Select(instruction => instruction.Kind == InstructionKind.MergeReference
                ? new FlowInstruction(InstructionKind.MergeAlias, instruction.Site, instruction.Target, instruction.Source,
                    owner: instruction.Owner) : instruction), pair.Value.Successors));
        var expected = HashSetLivenessOracle.Compute(oracleBlocks, predecessors, budget, default);
        var actual = PlaceLiveness.Compute(blocks, predecessors, budget, default);
        Assert.Equal(expected.BudgetExceeded, actual.BudgetExceeded);
        foreach (var id in blocks.Keys)
        {
            Assert.True(expected.LiveIn[id].SetEquals(actual.LiveIn[id]), "LiveIn differs for block " + id);
            Assert.True(expected.LiveOut[id].SetEquals(actual.LiveOut[id]), "LiveOut differs for block " + id);
            if (actual.BudgetExceeded) continue;
            Assert.Equal(expected.Before[id].Length, actual.Before[id].Length);
            Assert.Equal(expected.After[id].Length, actual.After[id].Length);
            for (var index = 0; index < expected.Before[id].Length; index++)
            {
                Assert.True(expected.Before[id][index].SetEquals(actual.Before[id][index]), "Before differs for block " + id + ", instruction " + index);
                Assert.True(expected.After[id][index].SetEquals(actual.After[id][index]), "After differs for block " + id + ", instruction " + index);
            }
        }
        if (actual.BudgetExceeded) { Assert.Empty(actual.Before); Assert.Empty(actual.After); }
    }

    private sealed class CancelOnValuesDictionary(FlowBlock block, CancellationTokenSource cancellation) : IReadOnlyDictionary<int, FlowBlock>
    {
        public IEnumerable<FlowBlock> Values { get { yield return block; cancellation.Cancel(); } }
        public int Count => 1;
        public IEnumerable<int> Keys => [block.Id];
        public FlowBlock this[int key] => block;
        public bool ContainsKey(int key) => key == block.Id;
        public bool TryGetValue(int key, out FlowBlock value) { value = block; return ContainsKey(key); }
        public IEnumerator<KeyValuePair<int, FlowBlock>> GetEnumerator() { yield return new(block.Id, block); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
