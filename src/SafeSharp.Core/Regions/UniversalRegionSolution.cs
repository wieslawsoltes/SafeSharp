using System.Collections.Immutable;

namespace SafeSharp.Core.Regions;

/// <summary>An implied universal outlives relation which the caller did not assume.</summary>
public sealed class UniversalRegionFailure
{
    internal UniversalRegionFailure(string longer, string shorter, RegionProof proof)
    { Longer = longer; Shorter = shorter; Proof = proof; }

    public string Longer { get; }
    public string Shorter { get; }
    public RegionProof Proof { get; }
}

/// <summary>
/// The immutable result of one universal-region compatibility check. An incomplete result exposes
/// neither failures nor partial closures and must never be interpreted as a successful proof.
/// </summary>
public sealed class UniversalRegionSolution
{
    internal UniversalRegionSolution(bool isComplete, string? budgetReason, int steps,
        ImmutableArray<UniversalRegionFailure> failures, RegionSolution? assumedClosure, RegionSolution? closure)
    {
        IsComplete = isComplete;
        BudgetReason = budgetReason;
        Steps = steps;
        Failures = failures;
        AssumedClosure = assumedClosure;
        Closure = closure;
    }

    public bool IsComplete { get; }
    public bool IsCompatible => IsComplete && Failures.IsEmpty;
    public string? BudgetReason { get; }
    public int Steps { get; }
    public ImmutableArray<UniversalRegionFailure> Failures { get; }
    public RegionSolution? AssumedClosure { get; }
    public RegionSolution? Closure { get; }

    internal static UniversalRegionSolution Incomplete(string reason, int steps) =>
        new(false, reason, steps, ImmutableArray<UniversalRegionFailure>.Empty, null, null);
}
