using SafeSharp.Core;
using SafeSharp.Core.Regions;

namespace SafeSharp.Tests.Regions;

public sealed class RegionConstraintSolverTests
{
    [Fact]
    public void ReflexivityIsKnownWithoutInventingAnEdge()
    {
        var solution = Solve(new[] { "a", "b" });
        Assert.True(solution.IsComplete);
        Assert.True(solution.Outlives("a", "a"));
        Assert.True(Assert.IsType<RegionProof>(solution.GetProof("a", "a")).IsReflexive);
        Assert.Empty(solution.GetProof("a", "a")!.Steps);
        Assert.False(solution.Outlives("a", "b"));
        Assert.False(solution.Outlives("missing", "missing"));
    }

    [Fact]
    public void TransitiveWitnessRetainsOrderedConstraintEvidence()
    {
        var first = new RegionConstraint("long", "middle", new SourceSite("a.cs", 10, 4), "first declaration");
        var second = new RegionConstraint("middle", "short", new SourceSite("b.cs", 20, 5), "second declaration");
        var solution = Solve(new[] { "short", "middle", "long" }, first, second);
        var proof = Assert.IsType<RegionProof>(solution.GetProof("long", "short"));
        Assert.Equal(new[] { first, second }, proof.Steps);
        Assert.Equal("first declaration", proof.Steps[0].Reason);
        Assert.Equal("b.cs", proof.Steps[1].Site!.Path);
        Assert.False(solution.Outlives("short", "long"));
    }

    [Fact]
    public void ShortestWitnessWinsOverLongerAlternative()
    {
        var direct = new RegionConstraint("a", "c", reason: "direct");
        var solution = Solve(new[] { "a", "b", "c" }, new RegionConstraint("a", "b"), new RegionConstraint("b", "c"), direct);
        Assert.Same(direct, Assert.Single(solution.GetProof("a", "c")!.Steps));
    }

    [Fact]
    public void EqualLengthWitnessUsesDeterministicLexicalOrder()
    {
        var edges = new[] { new RegionConstraint("a", "c"), new RegionConstraint("c", "d"), new RegionConstraint("a", "b"), new RegionConstraint("b", "d") };
        var expected = new[] { "a", "b" };
        Assert.Equal(expected, Solve(new[] { "d", "c", "b", "a" }, edges).GetProof("a", "d")!.Steps.Select(edge => edge.Longer));
        Assert.Equal(expected, Solve(new[] { "a", "b", "c", "d" }, edges.Reverse().ToArray()).GetProof("a", "d")!.Steps.Select(edge => edge.Longer));
    }

    [Fact]
    public void ParallelEdgesChooseStableSourceEvidence()
    {
        var later = new RegionConstraint("a", "b", new SourceSite("z.cs", 0, 1));
        var earlier = new RegionConstraint("a", "b", new SourceSite("a.cs", 20, 1));
        var solution = Solve(new[] { "a", "b" }, later, earlier);
        Assert.Same(earlier, Assert.Single(solution.GetProof("a", "b")!.Steps));
    }

    [Fact]
    public void StronglyConnectedRegionsAreEquivalentWithoutMergingExternalNeighbors()
    {
        var solution = Solve(new[] { "a", "b", "c", "d" }, new RegionConstraint("a", "b"), new RegionConstraint("b", "c"),
            new RegionConstraint("c", "a"), new RegionConstraint("c", "d"));
        Assert.True(solution.AreEquivalent("a", "c"));
        Assert.False(solution.AreEquivalent("a", "d"));
        Assert.Equal(new[] { "a", "b", "c" }, solution.Components[0].Members);
        Assert.Equal("a", solution.Components[0].Representative);
        Assert.Equal("d", solution.Components[1].Representative);
        Assert.True(solution.Outlives("b", "d"));
        Assert.False(solution.Outlives("d", "b"));
    }

    [Fact]
    public void DisconnectedCyclesAndSelfEdgesRemainSeparate()
    {
        var solution = Solve(new[] { "a", "b", "c", "d", "e" }, new RegionConstraint("a", "b"), new RegionConstraint("b", "a"),
            new RegionConstraint("c", "d"), new RegionConstraint("d", "c"), new RegionConstraint("e", "e"));
        Assert.Equal(3, solution.Components.Length);
        Assert.True(solution.AreEquivalent("e", "e"));
        Assert.False(solution.AreEquivalent("a", "c"));
        Assert.False(solution.AreEquivalent("unknown", "unknown"));
    }

    [Theory]
    [InlineData(1, 10, 1000, "named-region count")]
    [InlineData(10, 1, 1000, "outlives-constraint count")]
    [InlineData(10, 10, 1, "region-solving steps")]
    public void ExhaustedBudgetsReturnNoPartialProofs(int regions, int constraints, int steps, string reason)
    {
        var problem = new RegionProblem(new[] { "a", "b", "c" }, new[] { new RegionConstraint("a", "b"), new RegionConstraint("b", "c") });
        var result = new RegionConstraintSolver().Solve(problem, new RegionSolverOptions(regions, constraints, steps));
        Assert.False(result.IsComplete);
        Assert.Equal(reason, result.BudgetReason);
        Assert.Empty(result.Components);
        Assert.Empty(result.Implications);
        Assert.False(result.Outlives("a", "b"));
        Assert.True(result.Steps <= steps);
    }

    [Fact]
    public void CancellationIsObservedBeforeSolving()
    {
        var problem = new RegionProblem(new[] { "a" });
        Assert.Throws<OperationCanceledException>(() => new RegionConstraintSolver().Solve(problem, cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public async Task SharedSolverIsReentrantAndDeterministic()
    {
        var solver = new RegionConstraintSolver();
        var problem = new RegionProblem(new[] { "a", "b", "c" }, new[] { new RegionConstraint("a", "b"), new RegionConstraint("b", "c") });
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => solver.Solve(problem))));
        var expected = Serialize(results[0]);
        Assert.All(results, result => Assert.Equal(expected, Serialize(result)));
    }

    [Fact]
    public void InputCollectionsAreSnapshotted()
    {
        var names = new List<string> { "a", "b" };
        var edges = new List<RegionConstraint> { new("a", "b") };
        var problem = new RegionProblem(names, edges);
        names.Clear();
        edges.Clear();
        var solution = new RegionConstraintSolver().Solve(problem);
        Assert.True(solution.Outlives("a", "b"));
    }

    [Fact]
    public void UndeclaredConstraintEndpointIsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => new RegionProblem(new[] { "a" }, new[] { new RegionConstraint("a", "b") }));
        Assert.Equal("constraints", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a\0b")]
    public void InvalidCoreNamesAreRejected(string name) =>
        Assert.Throws<ArgumentException>(() => new RegionProblem(new[] { name }));

    [Fact]
    public void EmptyProblemIsCompleteAndHasNoImplications()
    {
        var solution = Solve(Array.Empty<string>());
        Assert.True(solution.IsComplete);
        Assert.Empty(solution.Implications);
        Assert.Empty(solution.Components);
    }

    [Fact]
    public void LongCycleUsesAnIterativeSccTraversal()
    {
        var names = Enumerable.Range(0, 64).Select(index => "r" + index.ToString("D2")).ToArray();
        var edges = names.Select((name, index) => new RegionConstraint(name, names[(index + 1) % names.Length])).ToArray();
        var result = new RegionConstraintSolver().Solve(new RegionProblem(names, edges), new RegionSolverOptions(maxSteps: 1000000));
        Assert.True(result.IsComplete);
        Assert.Equal(names, Assert.Single(result.Components).Members);
        Assert.Equal(64 * 64, result.Implications.Length);
    }

    private static RegionSolution Solve(IEnumerable<string> names, params RegionConstraint[] edges) => new RegionConstraintSolver().Solve(new RegionProblem(names, edges));
    private static string Serialize(RegionSolution solution) => string.Join(";", solution.Implications.Select(proof => proof.Longer + ":" + proof.Shorter + "=" + string.Join(",", proof.Steps.Select(edge => edge.Longer + ":" + edge.Shorter))));
}
