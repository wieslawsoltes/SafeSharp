using SafeSharp.Core;
using SafeSharp.Core.Regions;

namespace SafeSharp.Tests.Regions;

public sealed class RegionForestTests
{
    [Fact]
    public void LongChainSolvesWithinDefaultBudgetWithoutExpandingAllProofPaths()
    {
        var names = Enumerable.Range(0, 256).Select(index => "r" + index.ToString("D3")).ToArray();
        var edges = Enumerable.Range(0, names.Length - 1)
            .Select(index => new RegionConstraint(names[index], names[index + 1], new SourceSite("chain.cs", index, 1))).ToArray();
        var result = new RegionConstraintSolver().Solve(new RegionProblem(names, edges));

        Assert.True(result.IsComplete, result.BudgetReason);
        Assert.True(result.Steps <= 100000);
        Assert.True(result.Outlives(names[0], names[^1]));
        Assert.False(result.Outlives(names[^1], names[0]));
        Assert.Equal(edges, result.GetProof(names[0], names[^1])!.Steps);
        Assert.Equal(256 * 257 / 2, result.Implications.Length);
    }

    [Fact]
    public async Task LazyProofsAndCompleteEnumerationAreStableAcrossConcurrentReaders()
    {
        var names = Enumerable.Range(0, 40).Select(index => "r" + index.ToString("D2")).ToArray();
        var problem = new RegionProblem(names, names.Select((name, index) =>
            new RegionConstraint(name, names[(index + 1) % names.Length])));
        var result = new RegionConstraintSolver().Solve(problem);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            for (var root = 0; root < names.Length; root++)
            {
                var target = (root + worker) % names.Length;
                var proof = Assert.IsType<RegionProof>(result.GetProof(names[root], names[target]));
                Assert.Equal(worker, proof.Steps.Length);
                Assert.Same(proof, result.GetProof(names[root], names[target]));
                Assert.Same(proof, result.Implications.Single(item => item.Longer == names[root] && item.Shorter == names[target]));
            }
        })));
        Assert.Equal(1600, result.Implications.Length);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(29)]
    [InlineData(91)]
    public void IndexedForestMatchesIndependentBreadthFirstOracle(int seed)
    {
        var random = new Random(seed);
        for (var sample = 0; sample < 30; sample++)
        {
            var count = random.Next(1, 25);
            var names = Enumerable.Range(0, count).Select(index => "r" + index.ToString("D2")).ToArray();
            var edges = new List<RegionConstraint>();
            for (var source = 0; source < count; source++)
            for (var target = 0; target < count; target++)
                if (random.Next(5) == 0)
                    edges.Add(new RegionConstraint(names[source], names[target], new SourceSite("graph.cs", edges.Count, 1)));
            var problem = new RegionProblem(names.Reverse(), edges.OrderBy(_ => random.Next()));
            var result = new RegionConstraintSolver().Solve(problem);
            Assert.True(result.IsComplete);

            foreach (var source in names)
            {
                var expected = Paths(problem, source);
                foreach (var target in names)
                {
                    Assert.Equal(expected.ContainsKey(target), result.Outlives(source, target));
                    if (!expected.TryGetValue(target, out var path))
                        Assert.Null(result.GetProof(source, target));
                    else
                        Assert.Equal(path, result.GetProof(source, target)!.Steps);
                    Assert.Equal(expected.ContainsKey(target) && Paths(problem, target).ContainsKey(source), result.AreEquivalent(source, target));
                }
            }
            Assert.Equal(result.Implications.Select(proof => proof.Longer + "\0" + proof.Shorter)
                .Order(StringComparer.Ordinal), result.Implications.Select(proof => proof.Longer + "\0" + proof.Shorter));
        }
    }

    [Fact]
    public void QueryingUnknownRegionsDoesNotInventReflexiveProofsOrEquivalence()
    {
        var result = new RegionConstraintSolver().Solve(new RegionProblem(new[] { "a", "b" }));
        Assert.False(result.Outlives("missing", "missing"));
        Assert.False(result.AreEquivalent("missing", "missing"));
        Assert.Null(result.GetProof("missing", "missing"));
        Assert.Equal(2, result.Implications.Length);
    }

    [Fact]
    public void BudgetFailureStillExposesNoForestOrProof()
    {
        var names = Enumerable.Range(0, 30).Select(index => "r" + index).ToArray();
        var result = new RegionConstraintSolver().Solve(new RegionProblem(names,
            names.Select((name, index) => new RegionConstraint(name, names[(index + 1) % names.Length]))),
            new RegionSolverOptions(maxSteps: 240));
        Assert.False(result.IsComplete);
        Assert.Empty(result.Components);
        Assert.Empty(result.Implications);
        foreach (var name in names)
        {
            Assert.False(result.Outlives(name, name));
            Assert.Null(result.GetProof(name, name));
            Assert.False(result.AreEquivalent(name, name));
        }
    }

    private static Dictionary<string, RegionConstraint[]> Paths(RegionProblem problem, string source)
    {
        var paths = new Dictionary<string, RegionConstraint[]>(StringComparer.Ordinal) { [source] = [] };
        var queue = new Queue<string>();
        queue.Enqueue(source);
        while (queue.TryDequeue(out var current))
            foreach (var edge in problem.Constraints.Where(edge => edge.Longer == current))
            {
                if (paths.ContainsKey(edge.Shorter)) continue;
                paths[edge.Shorter] = paths[current].Append(edge).ToArray();
                queue.Enqueue(edge.Shorter);
            }
        return paths;
    }
}
