using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using SafeSharp.Core.Regions;

namespace SafeSharp.Benchmarks;

internal static class RegionBenchmarks
{
    internal static IEnumerable<BenchmarkCase> Create(string? baselineAssembly)
    {
        var optimized = new RegionApi(typeof(RegionConstraintSolver).Assembly);
        var baseline = baselineAssembly is null ? null : new RegionApi(new AssemblyLoadContext("SafeSharpBaseline", isCollectible: true).LoadFromAssemblyPath(baselineAssembly));
        foreach (var size in new[] { 64, 128, 256 })
        foreach (var shape in new[] { "chain", "cycle", "disconnected" })
            yield return Make(size, shape, false, baseline, optimized);
        yield return Make(128, "chain", true, baseline, optimized);
        yield return Make(128, "cycle", true, baseline, optimized);
    }

    private static BenchmarkCase Make(int size, string shape, bool allProofs, RegionApi? baseline, RegionApi optimized)
    {
        var names = Enumerable.Range(0, size).Select(index => "r" + index.ToString("D4")).ToArray();
        var edges = Enumerable.Range(0, size - 1).Where(index => shape != "disconnected" || index % 8 != 7)
            .Select(index => (Longer: names[index], Shorter: names[index + 1])).ToList();
        if (shape == "cycle") edges.Add((names[^1], names[0]));
        var currentProblem = optimized.CreateProblem(names, edges);
        var baselineProblem = baseline?.CreateProblem(names, edges);
        var label = "regions/" + shape + "-" + size + (allProofs ? "/all-proofs" : "/queries");
        object Run(RegionApi api, object problem)
        {
            var solution = api.Solve(problem);
            if (!api.IsComplete(solution)) throw new InvalidOperationException(label + " exceeded the common 100-million-step comparison budget.");
            if (allProofs) GC.KeepAlive(api.TotalProofEdges(solution));
            else
            {
                GC.KeepAlive(api.Outlives(solution, names[0], names[^1]));
                GC.KeepAlive(api.Equivalent(solution, names[0], names[^1]));
                var proof = api.Proof(solution, names[0], names[^1]);
                if (proof is not null) GC.KeepAlive(api.ProofLength(proof));
            }
            return solution;
        }
        return new(label, $"{size} named regions, {edges.Count} edges; " + (allProofs ? "solve plus every shortest proof path" : "solve plus two relation queries and one shortest proof"),
            baseline is null ? null : () => Run(baseline, baselineProblem!), () => Run(optimized, currentProblem),
            () =>
            {
                var after = Run(optimized, currentProblem);
                if (baseline is null) return;
                var before = Run(baseline, baselineProblem!);
                foreach (var left in names)
                foreach (var right in names)
                {
                    if (baseline.Outlives(before, left, right) != optimized.Outlives(after, left, right)
                        || baseline.Equivalent(before, left, right) != optimized.Equivalent(after, left, right))
                        throw new InvalidOperationException(label + " region relations differ.");
                    var oldProof = baseline.Proof(before, left, right);
                    var newProof = optimized.Proof(after, left, right);
                    if ((oldProof is null) != (newProof is null) || oldProof is not null && baseline.ProofLength(oldProof) != optimized.ProofLength(newProof!))
                        throw new InvalidOperationException(label + " shortest proof length differs.");
                }
            });
    }

    // Compile identical strongly typed delegate adapters for both assemblies before measuring.
    // This keeps per-sample reflection and setup overhead out of the old/new comparison.
    private sealed class RegionApi
    {
        private readonly Type constraintType;
        private readonly Type problemType;
        internal RegionApi(Assembly assembly)
        {
            Type Type(string name) => assembly.GetType("SafeSharp.Core.Regions." + name, throwOnError: true)!;
            constraintType = Type("RegionConstraint");
            problemType = Type("RegionProblem");
            var solverType = Type("RegionConstraintSolver");
            var optionsType = Type("RegionSolverOptions");
            var solutionType = Type("RegionSolution");
            var proofType = Type("RegionProof");
            var solver = Activator.CreateInstance(solverType)!;
            var options = Activator.CreateInstance(optionsType, 1024, 16384, 100_000_000)!;
            var problem = Expression.Parameter(typeof(object), "problem");
            Solve = Expression.Lambda<Func<object, object>>(Expression.Convert(Expression.Call(Expression.Constant(solver), solverType.GetMethod("Solve")!,
                Expression.Convert(problem, problemType), Expression.Constant(options), Expression.Constant(default(CancellationToken))), typeof(object)), problem).Compile();
            var solution = Expression.Parameter(typeof(object), "solution");
            var typedSolution = Expression.Convert(solution, solutionType);
            IsComplete = Expression.Lambda<Func<object, bool>>(Expression.Property(typedSolution, "IsComplete"), solution).Compile();
            var first = Expression.Parameter(typeof(string), "first");
            var second = Expression.Parameter(typeof(string), "second");
            Outlives = Expression.Lambda<Func<object, string, string, bool>>(Expression.Call(typedSolution, "Outlives", null, first, second), solution, first, second).Compile();
            Equivalent = Expression.Lambda<Func<object, string, string, bool>>(Expression.Call(typedSolution, "AreEquivalent", null, first, second), solution, first, second).Compile();
            Proof = Expression.Lambda<Func<object, string, string, object?>>(Expression.Convert(Expression.Call(typedSolution, "GetProof", null, first, second), typeof(object)), solution, first, second).Compile();
            var proof = Expression.Parameter(typeof(object), "proof");
            ProofLength = Expression.Lambda<Func<object, int>>(Expression.Property(Expression.Property(Expression.Convert(proof, proofType), "Steps"), "Length"), proof).Compile();
            var member = Expression.Parameter(proofType, "member");
            var selector = Expression.Lambda(Expression.Property(Expression.Property(member, "Steps"), "Length"), member);
            var implications = Expression.Convert(Expression.Property(typedSolution, "Implications"), typeof(IEnumerable<>).MakeGenericType(proofType));
            TotalProofEdges = Expression.Lambda<Func<object, int>>(Expression.Call(typeof(Enumerable), "Sum", [proofType], implications, selector), solution).Compile();
        }

        internal Func<object, object> Solve { get; }
        internal Func<object, bool> IsComplete { get; }
        internal Func<object, string, string, bool> Outlives { get; }
        internal Func<object, string, string, bool> Equivalent { get; }
        internal Func<object, string, string, object?> Proof { get; }
        internal Func<object, int> ProofLength { get; }
        internal Func<object, int> TotalProofEdges { get; }

        internal object CreateProblem(string[] names, List<(string Longer, string Shorter)> edges)
        {
            var constraints = Array.CreateInstance(constraintType, edges.Count);
            for (var index = 0; index < edges.Count; index++)
                constraints.SetValue(Activator.CreateInstance(constraintType, edges[index].Longer, edges[index].Shorter, null, "Benchmark edge."), index);
            return Activator.CreateInstance(problemType, names, constraints)!;
        }
    }
}
