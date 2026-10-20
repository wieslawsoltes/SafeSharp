using SafeSharp.Core;
using SafeSharp.Core.Regions;

namespace SafeSharp.Tests.Regions;

public sealed class UniversalRegionSolverTests
{
    [Fact]
    public void ReflexiveAndDisconnectedUniversalsDoNotRequireNewBounds()
    {
        var result = Solve(new[] { "a", "b" }, new[] { "x" });
        Assert.True(result.IsComplete);
        Assert.True(result.IsCompatible);
        Assert.Empty(result.Failures);
        Assert.True(result.Closure!.Outlives("a", "a"));
        Assert.False(result.Closure.Outlives("a", "b"));
        Assert.False(result.AssumedClosure!.Outlives("x", "x"));
        Assert.True(result.Closure.Outlives("x", "x"));
    }

    [Fact]
    public void UnexpectedDirectUniversalRelationRetainsOriginalEvidence()
    {
        var edge = new RegionConstraint("a", "b", new SourceSite("signature.cs", 12, 4), "return needs the second lifetime");
        var result = Solve(new[] { "a", "b" }, Array.Empty<string>(), edge);
        Assert.True(result.IsComplete);
        Assert.False(result.IsCompatible);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("a", failure.Longer);
        Assert.Equal("b", failure.Shorter);
        Assert.Same(edge, Assert.Single(failure.Proof.Steps));
        Assert.Equal("signature.cs", failure.Proof.Steps[0].Site!.Path);
        Assert.False(result.AssumedClosure!.Outlives("a", "b"));
    }

    [Fact]
    public void AnAssumedTransitiveRelationAllowsTheRequiredBound()
    {
        var assumptions = new RegionProblem(new[] { "a", "b", "c" }, new[] { Edge("a", "b"), Edge("b", "c") });
        var problem = new UniversalRegionProblem(assumptions, new[] { "x" }, new[] { Edge("a", "x"), Edge("x", "c") });
        var result = new UniversalRegionSolver().Solve(problem);
        Assert.True(result.IsCompatible);
        Assert.Empty(result.Failures);
        Assert.True(result.AssumedClosure!.Outlives("a", "c"));
    }

    [Fact]
    public void ReversingAnAssumedRelationIsAnAdditionalObligation()
    {
        var assumptions = new RegionProblem(new[] { "a", "b" }, new[] { Edge("a", "b") });
        var result = new UniversalRegionSolver().Solve(new UniversalRegionProblem(assumptions, new[] { "x" },
            new[] { Edge("b", "x"), Edge("x", "a") }));
        Assert.Equal(new[] { "b:a" }, Relations(result));
        Assert.True(result.Closure!.AreEquivalent("a", "b"));
        Assert.False(result.AssumedClosure!.AreEquivalent("a", "b"));
    }

    [Fact]
    public void DistinctInputsMayBeShortenedToACommonInferenceRegion()
    {
        var result = Solve(new[] { "a", "b" }, new[] { "shared" }, Edge("a", "shared"), Edge("b", "shared"));
        Assert.True(result.IsCompatible);
        Assert.False(result.Closure!.Outlives("a", "b"));
        Assert.False(result.Closure.Outlives("b", "a"));
    }

    [Theory]
    [InlineData("a", "b")]
    [InlineData("b", "a")]
    public void ReturningACommonInferenceRegionRequiresEverySourceToSatisfyTheResult(string returned, string other)
    {
        var result = Solve(new[] { "a", "b" }, new[] { "shared" }, Edge("a", "shared"), Edge("b", "shared"), Edge("shared", returned));
        var failure = Assert.Single(result.Failures);
        Assert.Equal(other, failure.Longer);
        Assert.Equal(returned, failure.Shorter);
        Assert.Equal(new[] { other + ":shared", "shared:" + returned }, failure.Proof.Steps.Select(Relation));
    }

    [Fact]
    public void IndependentInferencesCanReturnOnlyTheFirstInput()
    {
        var result = Solve(new[] { "a", "b" }, new[] { "first", "second" },
            Edge("a", "first"), Edge("b", "second"), Edge("first", "a"));
        Assert.True(result.IsCompatible);
        Assert.False(result.Closure!.Outlives("b", "a"));
    }

    [Fact]
    public void WritableStorageInvarianceAddsTheReverseRelation()
    {
        var covariant = Solve(new[] { "a", "b" }, new[] { "storage" }, Edge("a", "storage"), Edge("b", "storage"));
        var invariant = Solve(new[] { "a", "b" }, new[] { "storage" }, Edge("a", "storage"), Edge("storage", "a"), Edge("b", "storage"));
        Assert.True(covariant.IsCompatible);
        Assert.Equal(new[] { "b:a" }, Relations(invariant));
    }

    [Fact]
    public void AssumedEquivalentUniversalsCanShareInvariantStorage()
    {
        var assumptions = new RegionProblem(new[] { "a", "b" }, new[] { Edge("a", "b"), Edge("b", "a") });
        var result = new UniversalRegionSolver().Solve(new UniversalRegionProblem(assumptions, new[] { "storage" },
            new[] { Edge("a", "storage"), Edge("storage", "a"), Edge("b", "storage"), Edge("storage", "b") }));
        Assert.True(result.IsCompatible);
        Assert.True(result.Closure!.AreEquivalent("a", "storage"));
    }

    [Fact]
    public void CapturedReceiverCannotSatisfyAnArbitraryReturnedLifetime()
    {
        var result = Solve(new[] { "call", "capture" }, new[] { "receiver" }, Edge("capture", "receiver"), Edge("receiver", "call"));
        Assert.Equal(new[] { "capture:call" }, Relations(result));
    }

    [Fact]
    public void AnExplicitCaptureBoundPermitsTheReturnedLifetime()
    {
        var assumptions = new RegionProblem(new[] { "call", "capture" }, new[] { Edge("capture", "call") });
        var result = new UniversalRegionSolver().Solve(new UniversalRegionProblem(assumptions, new[] { "receiver" },
            new[] { Edge("capture", "receiver"), Edge("receiver", "call") }));
        Assert.True(result.IsCompatible);
    }

    [Fact]
    public void InferenceOnlyCyclesAreNotUniversalCompatibilityErrors()
    {
        var result = Solve(new[] { "a", "b" }, new[] { "x", "y", "z" },
            Edge("a", "x"), Edge("x", "y"), Edge("y", "x"), Edge("z", "z"));
        Assert.True(result.IsCompatible);
        Assert.True(result.Closure!.AreEquivalent("x", "y"));
        Assert.False(result.Closure.AreEquivalent("a", "x"));
    }

    [Fact]
    public void AUniversalRelationThroughAnInferenceCycleIsStillChecked()
    {
        var result = Solve(new[] { "a", "b" }, new[] { "x", "y" },
            Edge("a", "x"), Edge("x", "y"), Edge("y", "x"), Edge("y", "b"));
        var failure = Assert.Single(result.Failures);
        Assert.Equal("a:b", failure.Longer + ":" + failure.Shorter);
        Assert.Equal(new[] { "a:x", "x:y", "y:b" }, failure.Proof.Steps.Select(Relation));
    }

    [Fact]
    public void AUniversalCycleRequiresBothDirectionsToHaveBeenAssumed()
    {
        var result = Solve(new[] { "a", "b", "c" }, Array.Empty<string>(),
            Edge("a", "b"), Edge("b", "c"), Edge("c", "a"));
        Assert.Equal(new[] { "a:b", "a:c", "b:a", "b:c", "c:a", "c:b" }, Relations(result));
    }

    [Fact]
    public void ShortestFailureProofCanIncludeBothAssumptionsAndRequirements()
    {
        var assumed = new RegionConstraint("a", "b", new SourceSite("target.cs", 1, 2), "target bound");
        var input = new RegionConstraint("b", "x", new SourceSite("argument.cs", 3, 4), "input subtyping");
        var output = new RegionConstraint("x", "c", new SourceSite("result.cs", 5, 6), "result covariance");
        var assumptions = new RegionProblem(new[] { "a", "b", "c" }, new[] { assumed });
        var result = new UniversalRegionSolver().Solve(new UniversalRegionProblem(assumptions, new[] { "x" }, new[] { input, output }));
        Assert.Equal(new[] { "a:c", "b:c" }, Relations(result));
        Assert.Equal(new[] { assumed, input, output }, result.Failures[0].Proof.Steps);
        Assert.Equal(new[] { input, output }, result.Failures[1].Proof.Steps);
    }

    [Fact]
    public void ADirectRequirementWinsOverALongerFailureWitness()
    {
        var direct = new RegionConstraint("a", "b", reason: "direct");
        var result = Solve(new[] { "a", "b" }, new[] { "x", "y" },
            Edge("a", "x"), Edge("x", "y"), Edge("y", "b"), direct);
        Assert.Same(direct, Assert.Single(Assert.Single(result.Failures).Proof.Steps));
    }

    [Fact]
    public void ShuffledDeclarationsAndRequirementsKeepDeterministicFailureOrderAndEvidence()
    {
        var first = new RegionConstraint("a", "x", new SourceSite("a.cs", 4, 1), "first");
        var second = new RegionConstraint("a", "x", new SourceSite("z.cs", 1, 1), "parallel");
        var requirements = new[] { Edge("z", "a"), Edge("a", "y"), Edge("y", "b"), second, Edge("x", "b"), first };
        var one = Solve(new[] { "z", "b", "a" }, new[] { "y", "x" }, requirements);
        var two = Solve(new[] { "a", "b", "z" }, new[] { "x", "y" }, requirements.Reverse().ToArray());
        Assert.Equal(Serialize(one), Serialize(two));
        var witness = one.Failures.Single(failure => failure.Longer == "a" && failure.Shorter == "b").Proof;
        Assert.Same(first, witness.Steps[0]);
        Assert.Equal(new[] { "a:x", "x:b" }, witness.Steps.Select(Relation));
    }

    [Theory]
    [InlineData("U_", "I_")]
    [InlineData("universally_", "instantiated_")]
    [InlineData("α_", "β_")]
    public void RegionNamesHaveNoSemanticMeaningBeyondAlphaRenaming(string universal, string inference)
    {
        var a = universal + "a";
        var b = universal + "b";
        var x = inference + "x";
        var result = Solve(new[] { a, b }, new[] { x }, Edge(a, x), Edge(b, x), Edge(x, a));
        var failure = Assert.Single(result.Failures);
        Assert.Equal(b, failure.Longer);
        Assert.Equal(a, failure.Shorter);
        Assert.Equal(new[] { b + ":" + x, x + ":" + a }, failure.Proof.Steps.Select(Relation));
    }

    [Fact]
    public void AllInputCollectionsAreSnapshottedAndDuplicatesAreCanonicalized()
    {
        var universal = new List<string> { "b", "a", "a" };
        var assumed = new List<RegionConstraint> { Edge("a", "b") };
        var inference = new List<string> { "y", "x", "x" };
        var required = new List<RegionConstraint> { Edge("b", "x"), Edge("x", "a") };
        var problem = new UniversalRegionProblem(new RegionProblem(universal, assumed), inference, required);
        universal.Clear(); assumed.Clear(); inference.Clear(); required.Clear();
        Assert.Equal(new[] { "a", "b" }, problem.UniversalRegions);
        Assert.Equal(new[] { "x", "y" }, problem.InferenceRegions);
        Assert.Equal(2, problem.Requirements.Length);
        Assert.Equal(3, problem.CombinedProblem.Constraints.Length);
        Assert.Equal(new[] { "b:a" }, Relations(new UniversalRegionSolver().Solve(problem)));
    }

    [Fact]
    public void UniversalAndInferenceNamesMustBeDisjoint()
    {
        var error = Assert.Throws<ArgumentException>(() => new UniversalRegionProblem(new RegionProblem(new[] { "a" }), new[] { "a" }));
        Assert.Equal("inferenceRegions", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("x\0y")]
    public void InvalidInferenceNamesAreRejected(string name) =>
        Assert.Throws<ArgumentException>(() => new UniversalRegionProblem(new RegionProblem(Array.Empty<string>()), new[] { name }));

    [Fact]
    public void RequiredEndpointsMustExistInTheCombinedNames() =>
        Assert.Throws<ArgumentException>(() => new UniversalRegionProblem(new RegionProblem(new[] { "a" }), new[] { "x" }, new[] { Edge("x", "missing") }));

    [Fact]
    public void NullPublicInputsAreRejected()
    {
        var assumptions = new RegionProblem(Array.Empty<string>());
        Assert.Throws<ArgumentNullException>(() => new UniversalRegionProblem(null!, Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => new UniversalRegionProblem(assumptions, null!));
        Assert.Throws<ArgumentNullException>(() => new UniversalRegionProblem(assumptions, new string[] { null! }));
        Assert.Throws<ArgumentException>(() => new UniversalRegionProblem(assumptions, Array.Empty<string>(), new RegionConstraint[] { null! }));
        Assert.Throws<ArgumentNullException>(() => new UniversalRegionSolver().Solve(null!));
    }

    [Fact]
    public void EmptyAndInferenceOnlyProblemsAreComplete()
    {
        var empty = Solve(Array.Empty<string>(), Array.Empty<string>());
        Assert.True(empty.IsCompatible);
        Assert.Equal(0, empty.Steps);
        Assert.Empty(empty.Closure!.Implications);
        var inferenceOnly = Solve(Array.Empty<string>(), new[] { "x", "y" }, Edge("x", "y"), Edge("y", "x"));
        Assert.True(inferenceOnly.IsCompatible);
        Assert.True(inferenceOnly.Closure!.AreEquivalent("x", "y"));
        Assert.Empty(inferenceOnly.Failures);
    }

    [Theory]
    [InlineData(2, 10, 10000, "named-region count")]
    [InlineData(10, 1, 10000, "outlives-constraint count")]
    [InlineData(10, 10, 1, "region-solving steps")]
    public void LimitsApplyToTheCombinedProblemAndExposeNoPartialClosures(int regions, int constraints, int steps, string reason)
    {
        var assumptions = new RegionProblem(new[] { "a", "b" }, new[] { Edge("a", "b") });
        var problem = new UniversalRegionProblem(assumptions, new[] { "x" }, new[] { Edge("b", "x") });
        var result = new UniversalRegionSolver().Solve(problem, new RegionSolverOptions(regions, constraints, steps));
        AssertIncomplete(result);
        Assert.Equal(reason, result.BudgetReason);
        Assert.InRange(result.Steps, 0, steps);
    }

    [Fact]
    public void BothClosureSolvesAndPairComparisonsUseOneTotalStepBudget()
    {
        var problem = new UniversalRegionProblem(new RegionProblem(new[] { "a", "b", "c" }), new[] { "x" },
            new[] { Edge("a", "x"), Edge("x", "b"), Edge("b", "c") });
        var baseSolver = new RegionConstraintSolver();
        var assumedSteps = baseSolver.Solve(problem.Assumptions).Steps;
        var combinedSteps = baseSolver.Solve(problem.CombinedProblem).Steps;
        var exactBudget = assumedSteps + combinedSteps + problem.UniversalRegions.Length * problem.UniversalRegions.Length;
        var solver = new UniversalRegionSolver();
        var exact = solver.Solve(problem, new RegionSolverOptions(maxSteps: exactBudget));
        Assert.True(exact.IsComplete);
        Assert.False(exact.IsCompatible);
        Assert.Equal(exactBudget, exact.Steps);
        AssertIncomplete(solver.Solve(problem, new RegionSolverOptions(maxSteps: exactBudget - 1)));
        AssertIncomplete(solver.Solve(problem, new RegionSolverOptions(maxSteps: combinedSteps)));
    }

    [Fact]
    public void ExhaustionAfterAnEarlierFailureStillDiscardsAllPartialEvidence()
    {
        var problem = new UniversalRegionProblem(new RegionProblem(new[] { "a", "b", "c" }), Array.Empty<string>(),
            new[] { Edge("a", "b"), Edge("a", "c"), Edge("b", "c") });
        var baseSolver = new RegionConstraintSolver();
        var solves = baseSolver.Solve(problem.Assumptions).Steps + baseSolver.Solve(problem.CombinedProblem).Steps;
        // The first two ordered pairs are a:a and a:b. The latter is already a failure,
        // but the check is incomplete until every pair has been inspected.
        var result = new UniversalRegionSolver().Solve(problem, new RegionSolverOptions(maxSteps: solves + 2));
        AssertIncomplete(result);
        Assert.Equal("universal-region comparison steps", result.BudgetReason);
        Assert.Equal(solves + 2, result.Steps);
    }

    [Fact]
    public void InferenceOnlySolvingCanUseTheExactFinalStep()
    {
        var problem = new UniversalRegionProblem(new RegionProblem(Array.Empty<string>()), new[] { "x", "y" }, new[] { Edge("x", "y") });
        var steps = new RegionConstraintSolver().Solve(problem.CombinedProblem).Steps;
        var result = new UniversalRegionSolver().Solve(problem, new RegionSolverOptions(maxSteps: steps));
        Assert.True(result.IsCompatible);
        Assert.Equal(steps, result.Steps);
    }

    [Fact]
    public void CancellationIsObservedBeforeLimitsAndDoesNotPoisonTheSolver()
    {
        var problem = new UniversalRegionProblem(new RegionProblem(new[] { "a", "b" }), new[] { "x" }, new[] { Edge("a", "x"), Edge("x", "b") });
        var solver = new UniversalRegionSolver();
        Assert.Throws<OperationCanceledException>(() => solver.Solve(problem, new RegionSolverOptions(maxRegions: 1), new CancellationToken(true)));
        Assert.True(solver.Solve(problem).IsComplete);
    }

    [Fact]
    public async Task SharedSolverAndEvidenceAreReentrantAndDeterministic()
    {
        var problem = new UniversalRegionProblem(new RegionProblem(new[] { "a", "b", "c" }), new[] { "x", "y" },
            new[] { Edge("a", "x"), Edge("x", "b"), Edge("b", "y"), Edge("y", "c") });
        var solver = new UniversalRegionSolver();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => solver.Solve(problem))));
        var expected = Serialize(results[0]);
        Assert.All(results, result => Assert.Equal(expected, Serialize(result)));
        var sameResult = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => Serialize(results[0]))));
        Assert.All(sameResult, result => Assert.Equal(expected, result));
    }

    private static RegionConstraint Edge(string longer, string shorter) => new(longer, shorter);
    private static string Relation(RegionConstraint edge) => edge.Longer + ":" + edge.Shorter;
    private static string[] Relations(UniversalRegionSolution result) => result.Failures.Select(failure => failure.Longer + ":" + failure.Shorter).ToArray();
    private static UniversalRegionSolution Solve(IEnumerable<string> universal, IEnumerable<string> inference, params RegionConstraint[] requirements) =>
        new UniversalRegionSolver().Solve(new UniversalRegionProblem(new RegionProblem(universal), inference, requirements));

    private static void AssertIncomplete(UniversalRegionSolution result)
    {
        Assert.False(result.IsComplete);
        Assert.False(result.IsCompatible);
        Assert.NotNull(result.BudgetReason);
        Assert.Empty(result.Failures);
        Assert.Null(result.AssumedClosure);
        Assert.Null(result.Closure);
    }

    private static string Serialize(UniversalRegionSolution solution) => solution.IsComplete + "/" + solution.IsCompatible + "/" + solution.Steps + ":" +
        string.Join(";", solution.Failures.Select(failure => failure.Longer + ":" + failure.Shorter + "=" + string.Join(",",
            failure.Proof.Steps.Select(edge => Relation(edge) + "@" + edge.Site?.Path + ":" + edge.Site?.Start + ":" + edge.Reason))));
}
