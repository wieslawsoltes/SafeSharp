using SafeSharp.Core;
using SafeSharp.Core.Regions;

namespace SafeSharp.Tests.Regions;

public sealed class HigherRankedRegionSolverTests
{
    private static readonly RegionBinder[] Tree =
    [new("root"), new("left", "root"), new("inner", "left"), new("right", "root")];

    [Fact]
    public void VisibilityUsesLexicalAncestryIncludingSiblingIsolation()
    {
        var problem = Problem([]);
        Assert.True(problem.CanName("inner", "root"));
        Assert.True(problem.CanName("inner", "left"));
        Assert.True(problem.CanName("left", "left"));
        Assert.False(problem.CanName("root", "inner"));
        Assert.False(problem.CanName("right", "left"));
        Assert.False(problem.CanName("left", "right"));
        Assert.Throws<ArgumentException>(() => problem.CanName("missing", "root"));
    }

    [Fact]
    public void APlaceholderCanUnifyWithAnInferenceVariableInTheSameUniverse()
    {
        var result = Solve([Placeholder("p", "left"), Inference("x", "left")], [Edge("p", "x"), Edge("x", "p")]);
        Assert.True(result.IsCompatible);
        Assert.Empty(result.Promotions);
        Assert.True(result.Closure!.AreEquivalent("p", "x"));
    }

    [Fact]
    public void AChildInferenceVariableCanNameAnAncestorPlaceholder()
    {
        var result = Solve([Placeholder("p", "left"), Inference("x", "inner")], [Edge("p", "x"), Edge("x", "p")]);
        Assert.True(result.IsCompatible);
        Assert.Empty(result.Promotions);
    }

    [Fact]
    public void InvisibleLowerBoundCanBeSatisfiedByStaticWithoutAnUpperBound()
    {
        var result = Solve([Placeholder("p", "left"), Inference("x", "root")], [Edge("x", "p")]);
        Assert.True(result.IsCompatible);
        var promotion = Assert.Single(result.Promotions);
        Assert.Equal("x", promotion.Variable.Name);
        Assert.Equal("p", promotion.Placeholder.Name);
        Assert.True(result.Closure!.Outlives("x", "$static"));
    }

    [Fact]
    public void InvisiblePlaceholderCannotEscapeThroughAnOlderFiniteRegion()
    {
        var source = new SourceSite("callback.cs", 30, 7);
        var introduced = new RegionConstraint("x", "p", source, "Nested callback returns captured outer storage.");
        var result = Solve([Universal("a"), Placeholder("p", "left"), Inference("x", "root")], [Edge("a", "x"), introduced]);
        Assert.True(result.IsComplete);
        Assert.False(result.IsCompatible);
        var promotion = Assert.Single(result.Promotions, item => item.Variable.Name == "x");
        Assert.Same(introduced, Assert.Single(promotion.Proof.Steps));
        Assert.Same(source, promotion.Proof.Steps[0].Site);
        var leak = Assert.Single(result.Failures, failure => failure.Longer == "a" && failure.Shorter == "$static");
        Assert.Contains("static upper bound", leak.Proof.Steps[^1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualityWithAnOlderVariableLeaksEvenWithoutAnotherUniversal()
    {
        var result = Solve([Placeholder("p", "left"), Inference("x", "root")], [Edge("x", "p"), Edge("p", "x")]);
        Assert.False(result.IsCompatible);
        Assert.Contains(result.Failures, failure => failure.Longer == "p" && failure.Shorter == "$static");
        Assert.Single(result.Promotions);
    }

    [Fact]
    public void OlderEmptyRegionLowersTheUniverseOfItsPlaceholderPredecessor()
    {
        var result = Solve([Placeholder("p", "left"), Inference("x", "root")], [Edge("p", "x")]);
        Assert.False(result.IsCompatible);
        var promotion = Assert.Single(result.Promotions);
        Assert.Equal("root", promotion.EffectiveBinder);
        Assert.Equal("x", promotion.VisibilityBound.Name);
        Assert.Equal("p", promotion.VisibilityProof.Longer);
        Assert.Equal("x", promotion.VisibilityProof.Shorter);
    }

    [Fact]
    public void SharedShortenedInputDoesNotEquateIndependentPlaceholders()
    {
        var result = Solve([Placeholder("a", "left"), Placeholder("b", "left"), Inference("x", "left")],
            [Edge("a", "x"), Edge("b", "x")]);
        Assert.True(result.IsCompatible);
        Assert.False(result.Closure!.Outlives("a", "b"));
    }

    [Fact]
    public void ReturningASharedShorteningRequiresARealUniversalBound()
    {
        var result = Solve([Placeholder("a", "left"), Placeholder("b", "left"), Inference("x", "left")],
            [Edge("a", "x"), Edge("b", "x"), Edge("x", "a")]);
        Assert.False(result.IsCompatible);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("b", failure.Longer);
        Assert.Equal("a", failure.Shorter);
        Assert.Empty(result.Promotions);
    }

    [Fact]
    public void ExplicitNestedAssumptionCanJustifyReturnedShortening()
    {
        var result = Solve([Placeholder("a", "left"), Placeholder("b", "left"), Inference("x", "left")],
            [Edge("a", "x"), Edge("b", "x"), Edge("x", "a")], [Edge("b", "a")]);
        Assert.True(result.IsCompatible);
    }

    [Fact]
    public void SiblingUniversesCannotNameEachOthersPlaceholders()
    {
        var result = Solve([Placeholder("a", "left"), Placeholder("b", "right"), Inference("x", "left")],
            [Edge("a", "x"), Edge("x", "b")]);
        Assert.False(result.IsCompatible);
        Assert.NotEmpty(result.Promotions);
        Assert.Contains(result.Failures, failure => failure.Longer == "a" && failure.Shorter == "$static");
    }

    [Fact]
    public void ReachableOlderEmptyRegionAlsoRestrictsAYoungInferenceVariable()
    {
        var result = Solve([Placeholder("p", "left"), Inference("young", "left"), Inference("old", "root")],
            [Edge("young", "p"), Edge("young", "old")]);
        Assert.True(result.IsCompatible);
        var promotion = Assert.Single(result.Promotions);
        Assert.Equal("young", promotion.Variable.Name);
        Assert.Equal("root", promotion.EffectiveBinder);
        Assert.Equal("old", promotion.VisibilityBound.Name);
        Assert.True(result.Closure!.Outlives("young", "$static"));
    }

    [Fact]
    public void RootUniversalNamesRemainVisibleAtEveryDepth()
    {
        var result = Solve([Universal("a"), Inference("x", "inner")], [Edge("a", "x"), Edge("x", "a")]);
        Assert.True(result.IsCompatible);
        Assert.Empty(result.Promotions);
    }

    [Fact]
    public void PromotionPropagatesToPredecessorsInOneAdditionalClosure()
    {
        var result = Solve([Universal("a"), Placeholder("p", "inner"), Inference("x", "root"), Inference("y", "left")],
            [Edge("a", "y"), Edge("y", "x"), Edge("x", "p")]);
        Assert.False(result.IsCompatible);
        Assert.True(result.Closure!.Outlives("y", "$static"));
        Assert.True(result.Closure.Outlives("a", "$static"));
    }

    [Fact]
    public void ExplicitStaticUpperBoundCanJustifyAnOtherwiseInvisibleValue()
    {
        var result = Solve([Universal("a"), Placeholder("p", "left"), Inference("x", "root")],
            [Edge("a", "x"), Edge("x", "p")], [Edge("a", "$static")]);
        Assert.True(result.IsCompatible);
    }

    [Fact]
    public void IndependentUniversalRelationsRetainTheShortestOriginalPath()
    {
        var input = Edge("a", "x");
        var output = Edge("x", "b");
        var result = Solve([Universal("a"), Universal("b"), Inference("x", "root")], [input, output]);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(new[] { input, output }, failure.Proof.Steps);
    }

    [Fact]
    public void TypeOutlivesBoundFollowsOnlyCallerAssumedRelations()
    {
        var requirement = new TypeOutlivesConstraint("T", "x");
        var problem = Problem([Universal("a"), Universal("b"), Inference("x", "root")],
            [Edge("x", "b")], [Edge("a", "b")], [new("T", "a")], [requirement]);
        var result = new HigherRankedRegionSolver().Solve(problem);
        Assert.True(result.IsCompatible);
    }

    [Fact]
    public void MissingTypeOutlivesBoundRetainsRequirementAndRegionEvidence()
    {
        var site = new SourceSite("generic.cs", 15, 12);
        var requirement = new TypeOutlivesConstraint("T", "x", site, "Callback generic payload must survive.");
        var edge = Edge("x", "b");
        var problem = Problem([Universal("a"), Universal("b"), Inference("x", "root")], [edge],
            assumedTypes: [new("T", "a")], requiredTypes: [requirement]);
        var result = new HigherRankedRegionSolver().Solve(problem);
        Assert.False(result.IsCompatible);
        Assert.Empty(result.Failures);
        var failure = Assert.Single(result.TypeFailures);
        Assert.Same(requirement, failure.Requirement);
        Assert.Same(site, failure.Requirement.Site);
        Assert.Equal("b", failure.Region);
        Assert.Same(edge, Assert.Single(failure.Proof.Steps));
    }

    [Fact]
    public void TypesDoNotBorrowAnotherTypesBounds()
    {
        var problem = Problem([Universal("a")], assumedTypes: [new("T", "a")], requiredTypes: [new("U", "a")]);
        var result = new HigherRankedRegionSolver().Solve(problem);
        Assert.False(result.IsCompatible);
        Assert.Equal("U", Assert.Single(result.TypeFailures).Requirement.Type);
    }

    [Fact]
    public void TypeOutlivesAnEmptyInferredRegionWithoutExtraCallerBounds()
    {
        var result = new HigherRankedRegionSolver().Solve(Problem([Universal("a"), Inference("x", "root")],
            [Edge("a", "x")], requiredTypes: [new("T", "x")]));
        Assert.True(result.IsCompatible);
    }

    [Fact]
    public void TypeOutlivesStaticRequiresAStaticTypeBound()
    {
        var problem = Problem([Universal("a")], assumedTypes: [new("T", "a")], requiredTypes: [new("T", "$static")]);
        Assert.False(new HigherRankedRegionSolver().Solve(problem).IsCompatible);
        var valid = Problem([Universal("a")], assumedTypes: [new("T", "$static")], requiredTypes: [new("T", "a")]);
        Assert.True(new HigherRankedRegionSolver().Solve(valid).IsCompatible);
    }

    [Fact]
    public void StaticTypeBoundsCannotBeInferredFromCallbackRequirements()
    {
        var problem = Problem([Universal("a"), Inference("x", "root")], [Edge("x", "a")],
            requiredTypes: [new("T", "$static"), new("T", "x")]);
        var result = new HigherRankedRegionSolver().Solve(problem);
        Assert.False(result.IsCompatible);
        Assert.NotEmpty(result.TypeFailures);
    }

    [Fact]
    public void TotalStepBudgetCoversEverySolvePromotionAndComparison()
    {
        var problem = Problem([Universal("a"), Placeholder("p", "left"), Inference("x", "root")],
            [Edge("a", "x"), Edge("x", "p")], requiredTypes: [new("T", "a")]);
        var solver = new HigherRankedRegionSolver();
        var complete = solver.Solve(problem);
        Assert.True(complete.IsComplete);
        var exact = solver.Solve(problem, new RegionSolverOptions(maxSteps: complete.Steps));
        Assert.True(exact.IsComplete);
        var incomplete = solver.Solve(problem, new RegionSolverOptions(maxSteps: complete.Steps - 1));
        Assert.False(incomplete.IsComplete);
        Assert.False(incomplete.IsCompatible);
        Assert.Equal(complete.Steps - 1, incomplete.Steps);
        Assert.Empty(incomplete.Failures);
        Assert.Empty(incomplete.Promotions);
        Assert.Empty(incomplete.TypeFailures);
        Assert.Null(incomplete.Closure);
        Assert.Null(incomplete.AssumedClosure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    public void TinyStepBudgetsNeverReturnPartialProofs(int maximum)
    {
        var result = new HigherRankedRegionSolver().Solve(Problem([Universal("a"), Placeholder("p", "left"), Inference("x", "root")],
            [Edge("a", "x"), Edge("x", "p")]), new RegionSolverOptions(maxSteps: maximum));
        Assert.False(result.IsComplete);
        Assert.InRange(result.Steps, 0, maximum);
        Assert.Empty(result.Failures);
        Assert.Null(result.Closure);
    }

    [Fact]
    public void ConstraintsIncludeImplicitStaticEdgesAndTypeBoundsInOneLimit()
    {
        var problem = Problem([Universal("a"), Universal("b")], assumedTypes: [new("T", "a")]);
        var result = new HigherRankedRegionSolver().Solve(problem, new RegionSolverOptions(maxConstraints: 2));
        Assert.False(result.IsComplete);
        Assert.Equal("outlives/type-constraint count", result.BudgetReason);
        Assert.Equal(0, result.Steps);
    }

    [Fact]
    public void UniverseCountSharesTheRegionDomainLimit()
    {
        var result = new HigherRankedRegionSolver().Solve(Problem([]), new RegionSolverOptions(maxRegions: 3));
        Assert.False(result.IsComplete);
        Assert.Equal("named-region/universe count", result.BudgetReason);
    }

    [Fact]
    public void PromotionsShareTheConstraintLimit()
    {
        var problem = Problem([Placeholder("p", "left"), Inference("x", "root")], [Edge("x", "p")]);
        var result = new HigherRankedRegionSolver().Solve(problem, new RegionSolverOptions(maxConstraints: 3));
        Assert.False(result.IsComplete);
        Assert.Empty(result.Promotions);
        Assert.Null(result.Closure);
    }

    [Fact]
    public void CancellationIsObservedBeforePreflightBudgets()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new HigherRankedRegionSolver().Solve(Problem([]),
            new RegionSolverOptions(maxRegions: 1), canceled.Token));
    }

    [Fact]
    public async Task ImmutableProblemAndSolverAreSafeForConcurrentReuse()
    {
        var problem = Problem([Placeholder("p", "left"), Inference("x", "root")], [Edge("x", "p"), Edge("p", "x")]);
        var solver = new HigherRankedRegionSolver();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => solver.Solve(problem))));
        Assert.All(results, result => Assert.False(result.IsCompatible));
        Assert.Single(results.Select(result => result.Steps).Distinct());
        Assert.Single(results.Select(result => string.Join(";", result.Failures.Select(failure => failure.Longer + ":" + failure.Shorter))).Distinct());
    }

    [Fact]
    public void ConstructionSnapshotsCallerCollections()
    {
        var binders = Tree.ToList();
        var variables = new List<RegionVariable> { Universal("a"), Universal("b") };
        var edges = new List<RegionConstraint> { Edge("a", "b") };
        var problem = new HigherRankedRegionProblem(binders, variables, requirements: edges);
        binders.Clear(); variables.Clear(); edges.Clear();
        Assert.Equal(4, problem.Binders.Length);
        Assert.Equal(3, problem.Regions.Length);
        Assert.Single(problem.Requirements);
        Assert.False(new HigherRankedRegionSolver().Solve(problem).IsCompatible);
    }

    [Fact]
    public void DeepBinderTreesAreIndexedWithoutRecursion()
    {
        var binders = Enumerable.Range(0, 3000).Select(index => new RegionBinder("b" + index, index == 0 ? null : "b" + (index - 1)));
        var problem = new HigherRankedRegionProblem(binders, [new("p", "b1", RegionVariableKind.Placeholder), new("x", "b2999", RegionVariableKind.Inference)],
            requirements: [Edge("p", "x"), Edge("x", "p")]);
        Assert.True(problem.CanName("b2999", "b1"));
        Assert.False(problem.CanName("b1", "b2999"));
        Assert.True(new HigherRankedRegionSolver().Solve(problem, new RegionSolverOptions(maxRegions: 4000)).IsCompatible);
    }

    [Fact]
    public void AnExplicitStaticIdentityIsPreserved()
    {
        var problem = Problem([new("eternal", "root", RegionVariableKind.Static), Universal("a")]);
        Assert.Equal("eternal", problem.StaticRegion);
        Assert.Equal(2, problem.Regions.Length);
        Assert.True(new HigherRankedRegionSolver().Solve(problem).Closure!.Outlives("eternal", "a"));
    }

    [Fact]
    public void GeneratedStaticIdentityCannotAliasAUserRegion()
    {
        var problem = Problem([Universal("$static")]);
        Assert.Equal("$static$", problem.StaticRegion);
        Assert.Equal(2, problem.Regions.Length);
    }

    [Fact]
    public void InvalidBinderGraphsAndQuantificationAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new HigherRankedRegionProblem([], []));
        Assert.Throws<ArgumentException>(() => new HigherRankedRegionProblem([new("a"), new("b")], []));
        Assert.Throws<ArgumentException>(() => new HigherRankedRegionProblem([new("root"), new("a", "b"), new("b", "a")], []));
        Assert.Throws<ArgumentException>(() => new HigherRankedRegionProblem([new("root"), new("a", "missing")], []));
        Assert.Throws<ArgumentException>(() => new HigherRankedRegionProblem([new("root"), new("root")], []));
        Assert.Throws<ArgumentException>(() => Problem([new("a", "left", RegionVariableKind.Universal)]));
        Assert.Throws<ArgumentException>(() => Problem([new("a", "left", RegionVariableKind.Static)]));
        Assert.Throws<ArgumentException>(() => Problem([new("a", "missing", RegionVariableKind.Inference)]));
        Assert.Throws<ArgumentException>(() => Problem([Universal("a"), Universal("a")]));
        Assert.Throws<ArgumentException>(() => Problem([Inference("x", "root")], assumed: [Edge("x", "$static")]));
        Assert.Throws<ArgumentException>(() => Problem([Inference("x", "root")], assumedTypes: [new("T", "x")]));
        Assert.Throws<ArgumentException>(() => Problem([], requiredTypes: [new("T", "missing")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegionVariable("a", "root", (RegionVariableKind)99));
    }

    private static RegionConstraint Edge(string longer, string shorter) => new(longer, shorter);
    private static RegionVariable Universal(string name) => new(name, "root", RegionVariableKind.Universal);
    private static RegionVariable Placeholder(string name, string binder) => new(name, binder, RegionVariableKind.Placeholder);
    private static RegionVariable Inference(string name, string binder) => new(name, binder, RegionVariableKind.Inference);
    private static HigherRankedRegionProblem Problem(RegionVariable[] variables, RegionConstraint[]? required = null,
        RegionConstraint[]? assumed = null, TypeOutlivesConstraint[]? assumedTypes = null, TypeOutlivesConstraint[]? requiredTypes = null) =>
        new(Tree, variables, assumed, required, assumedTypes, requiredTypes);
    private static HigherRankedRegionSolution Solve(RegionVariable[] variables, RegionConstraint[]? required = null, RegionConstraint[]? assumed = null) =>
        new HigherRankedRegionSolver().Solve(Problem(variables, required, assumed));
}
