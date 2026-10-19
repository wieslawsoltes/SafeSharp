using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SafeSharp.Core.Regions;

/// <summary>Solves explicit region outlives relations with iterative SCC analysis and deterministic proof-producing closure.</summary>
/// <remarks>
/// Original implementation inspired by Rust's universal-region relations and outlives propagation:
/// https://rustc-dev-guide.rust-lang.org/borrow-check/region-inference.html.
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
            var outgoing = Enumerable.Range(0, names.Length).Select(_ => new List<RegionConstraint>()).ToArray();
            var incoming = Enumerable.Range(0, names.Length).Select(_ => new List<int>()).ToArray();
            foreach (var edge in problem.Constraints)
            {
                budget.Step();
                outgoing[index[edge.Longer]].Add(edge);
                incoming[index[edge.Shorter]].Add(index[edge.Longer]);
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
                        stack.Push((index[outgoing[item.Node][edge].Shorter], false));
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

            var implications = new List<RegionProof>();
            for (var root = 0; root < names.Length; root++)
            {
                var predecessor = new RegionConstraint?[names.Length];
                Array.Clear(visited, 0, visited.Length);
                visited[root] = true;
                var queue = new Queue<int>();
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    budget.Step();
                    var node = queue.Dequeue();
                    foreach (var edge in outgoing[node])
                    {
                        budget.Step();
                        var target = index[edge.Shorter];
                        if (visited[target]) continue;
                        visited[target] = true;
                        predecessor[target] = edge;
                        queue.Enqueue(target);
                    }
                }
                for (var target = 0; target < names.Length; target++)
                {
                    if (!visited[target]) continue;
                    var path = new List<RegionConstraint>();
                    for (var current = target; current != root;)
                    {
                        budget.Step();
                        var edge = predecessor[current]!;
                        path.Add(edge);
                        current = index[edge.Longer];
                    }
                    path.Reverse();
                    implications.Add(new RegionProof(names[root], names[target], path));
                }
            }
            return new RegionSolution(true, null, budget.Steps, components, implications);
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
