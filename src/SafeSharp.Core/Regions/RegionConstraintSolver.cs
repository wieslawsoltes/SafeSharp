using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SafeSharp.Core.Regions;

/// <summary>Solves explicit region outlives relations with indexed SCC analysis and compact shortest-path forests.</summary>
/// <remarks>
/// Original implementation inspired by Rust's universal-region relations and outlives propagation:
/// https://rustc-dev-guide.rust-lang.org/borrow-check/region-inference.html.
/// Indexed region identities and separating solving from diagnostic witnesses also follow the design in
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/region_infer/mod.rs.
/// This is an original C# adaptation. It keeps deterministic breadth-first predecessor forests rather
/// than rustc's program-point region values; complete witness paths are materialized only when queried.
/// Unlike rustc's full region inference, this solver does not introduce universes, placeholders,
/// higher-ranked binders, type tests, or every program-point region element.
/// </remarks>
public sealed class RegionConstraintSolver
{
    public RegionSolution Solve(RegionProblem problem, RegionSolverOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (problem is null) throw new ArgumentNullException(nameof(problem));
        options ??= new RegionSolverOptions();
        cancellationToken.ThrowIfCancellationRequested();
        if (problem.Regions.Length > options.MaxRegions) return RegionSolution.Incomplete("named-region count", 0);
        if (problem.Constraints.Length > options.MaxConstraints) return RegionSolution.Incomplete("outlives-constraint count", 0);
        var budget = new SolveBudget(options.MaxSteps, cancellationToken);
        try
        {
            var names = problem.Regions;
            var index = names.Select((name, ordinal) => new { name, ordinal }).ToDictionary(item => item.name, item => item.ordinal, StringComparer.Ordinal);
            var outgoing = Enumerable.Range(0, names.Length).Select(_ => new List<int>()).ToArray();
            var incoming = Enumerable.Range(0, names.Length).Select(_ => new List<int>()).ToArray();
            var sources = new int[problem.Constraints.Length];
            var targets = new int[problem.Constraints.Length];
            for (var ordinal = 0; ordinal < problem.Constraints.Length; ordinal++)
            {
                budget.Step();
                var edge = problem.Constraints[ordinal];
                var source = sources[ordinal] = index[edge.Longer];
                var target = targets[ordinal] = index[edge.Shorter];
                outgoing[source].Add(ordinal);
                incoming[target].Add(source);
            }

            // Kosaraju's two iterative passes avoid recursion on large or adversarial cycles.
            var visited = new bool[names.Length];
            var finished = new List<int>();
            for (var root = 0; root < names.Length; root++)
            {
                if (visited[root]) continue;
                var stack = new Stack<(int Node, bool Expanded)>();
                stack.Push((root, false));
                while (stack.Count > 0)
                {
                    budget.Step();
                    var item = stack.Pop();
                    if (item.Expanded) { finished.Add(item.Node); continue; }
                    if (visited[item.Node]) continue;
                    visited[item.Node] = true;
                    stack.Push((item.Node, true));
                    for (var edge = outgoing[item.Node].Count - 1; edge >= 0; edge--)
                        stack.Push((targets[outgoing[item.Node][edge]], false));
                }
            }
            Array.Clear(visited, 0, visited.Length);
            var components = new List<RegionComponent>();
            for (var position = finished.Count - 1; position >= 0; position--)
            {
                var root = finished[position];
                if (visited[root]) continue;
                var stack = new Stack<int>();
                var members = new List<string>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    budget.Step();
                    var node = stack.Pop();
                    if (visited[node]) continue;
                    visited[node] = true;
                    members.Add(names[node]);
                    foreach (var predecessor in incoming[node]) stack.Push(predecessor);
                }
                components.Add(new RegionComponent(members));
            }

            // One predecessor edge per reachable pair is sufficient to retain every shortest proof.
            // Expanding every path here takes cubic space/time for a chain; compiler checks usually
            // need only a few witnesses and otherwise query reachability without constructing one.
            var predecessors = new int[names.Length][];
            var queue = new int[names.Length];
            for (var root = 0; root < names.Length; root++)
            {
                budget.Step();
                var predecessor = new int[names.Length];
                for (var target = 0; target < names.Length; target++) predecessor[target] = -1;
                predecessor[root] = -2; // The root is reachable without a fabricated constraint.
                predecessors[root] = predecessor;
                var head = 0;
                var tail = 0;
                queue[tail++] = root;
                while (head < tail)
                {
                    budget.Step();
                    var node = queue[head++];
                    foreach (var ordinal in outgoing[node])
                    {
                        budget.Step();
                        var target = targets[ordinal];
                        if (predecessor[target] != -1) continue;
                        predecessor[target] = ordinal;
                        queue[tail++] = target;
                    }
                }
            }
            return new RegionSolution(true, null, budget.Steps, names, problem.Constraints, sources, predecessors, components);
        }
        catch (SolveBudgetExceededException)
        {
            return RegionSolution.Incomplete("region-solving steps", budget.Steps);
        }
    }

    private sealed class SolveBudget
    {
        private readonly int limit;
        private readonly CancellationToken cancellationToken;
        internal SolveBudget(int limit, CancellationToken cancellationToken) { this.limit = limit; this.cancellationToken = cancellationToken; }
        internal int Steps { get; private set; }
        internal void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Steps == limit) throw new SolveBudgetExceededException();
            Steps++;
        }
    }

    private sealed class SolveBudgetExceededException : Exception { }
}
