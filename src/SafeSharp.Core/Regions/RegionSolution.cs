using System;
using System.Collections.Concurrent;
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
    private readonly Lazy<ImmutableArray<RegionConstraint>> steps;

    internal RegionProof(string longer, string shorter, Func<ImmutableArray<RegionConstraint>> createSteps)
    {
        Longer = longer;
        Shorter = shorter;
        steps = new Lazy<ImmutableArray<RegionConstraint>>(createSteps);
    }

    public string Longer { get; }
    public string Shorter { get; }
    public ImmutableArray<RegionConstraint> Steps => steps.Value;
    public bool IsReflexive => Longer == Shorter;
}

/// <summary>Immutable region implications and their provenance, or an explicit incomplete result.</summary>
/// <remarks>
/// An incomplete result intentionally exposes no partial proofs. Check <see cref="IsComplete"/>
/// before interpreting a missing implication. Reachability queries do not allocate proof paths;
/// witnesses and the full <see cref="Implications"/> collection are materialized on demand and
/// safely shared across concurrent readers. A witness has at most one fewer edges than regions.
/// </remarks>
public sealed class RegionSolution
{
    private readonly ImmutableArray<string> names;
    private readonly ImmutableArray<RegionConstraint> constraints;
    private readonly int[] sources;
    private readonly int[][] predecessors;
    private readonly Dictionary<string, int> indices;
    private readonly int[] representatives;
    private readonly ConcurrentDictionary<long, RegionProof> proofs = new();
    private readonly Lazy<ImmutableArray<RegionProof>> implications;

    internal RegionSolution(bool isComplete, string? budgetReason, int steps, ImmutableArray<string> names,
        ImmutableArray<RegionConstraint> constraints, int[] sources, int[][] predecessors, IEnumerable<RegionComponent> components)
    {
        IsComplete = isComplete;
        BudgetReason = budgetReason;
        Steps = steps;
        this.names = names;
        this.constraints = constraints;
        this.sources = sources;
        this.predecessors = predecessors;
        Components = components.OrderBy(component => component.Representative, StringComparer.Ordinal).ToImmutableArray();
        indices = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < names.Length; ordinal++) indices.Add(names[ordinal], ordinal);
        representatives = new int[names.Length];
        for (var component = 0; component < Components.Length; component++)
            foreach (var name in Components[component].Members) representatives[indices[name]] = component;
        implications = new Lazy<ImmutableArray<RegionProof>>(CreateImplications);
    }

    public bool IsComplete { get; }
    public string? BudgetReason { get; }
    public int Steps { get; }
    public ImmutableArray<RegionComponent> Components { get; }
    public ImmutableArray<RegionProof> Implications => implications.Value;

    public bool Outlives(string longer, string shorter)
    {
        RegionProblem.RequireName(longer, nameof(longer));
        RegionProblem.RequireName(shorter, nameof(shorter));
        return indices.TryGetValue(longer, out var root) && indices.TryGetValue(shorter, out var target)
            && predecessors[root][target] != -1;
    }

    public RegionProof? GetProof(string longer, string shorter)
    {
        RegionProblem.RequireName(longer, nameof(longer));
        RegionProblem.RequireName(shorter, nameof(shorter));
        return indices.TryGetValue(longer, out var root) && indices.TryGetValue(shorter, out var target)
            && predecessors[root][target] != -1 ? Proof(root, target) : null;
    }

    public bool AreEquivalent(string first, string second)
    {
        RegionProblem.RequireName(first, nameof(first));
        RegionProblem.RequireName(second, nameof(second));
        return indices.TryGetValue(first, out var left) && indices.TryGetValue(second, out var right)
            && representatives[left] == representatives[right];
    }

    internal static RegionSolution Incomplete(string reason, int steps) =>
        new(false, reason, steps, ImmutableArray<string>.Empty, ImmutableArray<RegionConstraint>.Empty,
            Array.Empty<int>(), Array.Empty<int[]>(), Array.Empty<RegionComponent>());

    private RegionProof Proof(int root, int target)
    {
        var key = ((long)root << 32) | (uint)target;
        if (proofs.TryGetValue(key, out var cached)) return cached;
        return proofs.GetOrAdd(key, new RegionProof(names[root], names[target], () => CreateSteps(root, target)));
    }

    private ImmutableArray<RegionConstraint> CreateSteps(int root, int target)
    {
        if (root == target) return ImmutableArray<RegionConstraint>.Empty;
        var parents = predecessors[root];
        var length = 0;
        for (var current = target; current != root; current = sources[parents[current]]) length++;
        var path = new RegionConstraint[length];
        for (var current = target; current != root;)
        {
            var edge = parents[current];
            path[--length] = constraints[edge];
            current = sources[edge];
        }
        return ImmutableArray.Create(path);
    }

    private ImmutableArray<RegionProof> CreateImplications()
    {
        var result = ImmutableArray.CreateBuilder<RegionProof>();
        for (var root = 0; root < names.Length; root++)
        for (var target = 0; target < names.Length; target++)
            if (predecessors[root][target] != -1) result.Add(Proof(root, target));
        return result.ToImmutable();
    }
}
