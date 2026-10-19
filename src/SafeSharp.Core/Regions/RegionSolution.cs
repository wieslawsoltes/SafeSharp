using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace SafeSharp.Core.Regions;

/// <summary>A strongly connected set of equivalent named regions.</summary>
public sealed class RegionComponent
{
    internal RegionComponent(IEnumerable<string> members)
    {
        Members = members.OrderBy(name => name, StringComparer.Ordinal).ToImmutableArray();
        Representative = Members[0];
    }

    public string Representative { get; }
    public ImmutableArray<string> Members { get; }
}

/// <summary>A deterministic shortest edge witness for an implied outlives relation.</summary>
public sealed class RegionProof
{
    internal RegionProof(string longer, string shorter, IEnumerable<RegionConstraint> steps)
    {
        Longer = longer;
        Shorter = shorter;
        Steps = steps.ToImmutableArray();
    }

    public string Longer { get; }
    public string Shorter { get; }
    public ImmutableArray<RegionConstraint> Steps { get; }
    public bool IsReflexive => Longer == Shorter && Steps.IsEmpty;
}

/// <summary>Immutable region implications and their provenance, or an explicit incomplete result.</summary>
/// <remarks>An incomplete result intentionally exposes no partial proofs. Check <see cref="IsComplete"/> before interpreting a missing implication.</remarks>
public sealed class RegionSolution
{
    private readonly ImmutableDictionary<string, RegionProof> proofs;
    private readonly ImmutableDictionary<string, string> representatives;

    internal RegionSolution(bool isComplete, string? budgetReason, int steps, IEnumerable<RegionComponent> components, IEnumerable<RegionProof> implications)
    {
        IsComplete = isComplete;
        BudgetReason = budgetReason;
        Steps = steps;
        Components = components.OrderBy(component => component.Representative, StringComparer.Ordinal).ToImmutableArray();
        Implications = implications.OrderBy(proof => proof.Longer, StringComparer.Ordinal).ThenBy(proof => proof.Shorter, StringComparer.Ordinal).ToImmutableArray();
        proofs = Implications.ToImmutableDictionary(proof => Key(proof.Longer, proof.Shorter), StringComparer.Ordinal);
        representatives = Components.SelectMany(component => component.Members.Select(name => new KeyValuePair<string, string>(name, component.Representative)))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    public bool IsComplete { get; }
    public string? BudgetReason { get; }
    public int Steps { get; }
    public ImmutableArray<RegionComponent> Components { get; }
    public ImmutableArray<RegionProof> Implications { get; }

    public bool Outlives(string longer, string shorter) => GetProof(longer, shorter) is not null;

    public RegionProof? GetProof(string longer, string shorter)
    {
        RegionProblem.RequireName(longer, nameof(longer));
        RegionProblem.RequireName(shorter, nameof(shorter));
        return proofs.TryGetValue(Key(longer, shorter), out var proof) ? proof : null;
    }

    public bool AreEquivalent(string first, string second)
    {
        RegionProblem.RequireName(first, nameof(first));
        RegionProblem.RequireName(second, nameof(second));
        return representatives.TryGetValue(first, out var left) && representatives.TryGetValue(second, out var right) && left == right;
    }

    internal static RegionSolution Incomplete(string reason, int steps) =>
        new(false, reason, steps, Array.Empty<RegionComponent>(), Array.Empty<RegionProof>());

    private static string Key(string longer, string shorter) => longer + "\0" + shorter;
}
