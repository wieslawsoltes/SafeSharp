using SafeSharp.Core;
using SafeSharp.Core.Regions;

namespace SafeSharp.Benchmarks;

/// <summary>Current-only measurements for domains that did not exist in the frozen liveness baseline.</summary>
internal static class BoundaryBenchmarks
{
    internal static IEnumerable<BenchmarkCase> Create()
    {
        yield return References(128);
        yield return AsyncCapabilities(128);
        yield return HigherRanked(64);
    }

    private static BenchmarkCase References(int count)
    {
        var instructions = new List<FlowInstruction>();
        for (var index = 0; index < count; index++)
        {
            var owner = "owner" + index;
            var alias = "reference" + index;
            instructions.Add(new(InstructionKind.Allocate, Site(instructions.Count), owner, resourceId: owner));
            instructions.Add(new(InstructionKind.BindReference, Site(instructions.Count), alias, owner));
            instructions.Add(new(InstructionKind.Dispose, Site(instructions.Count), source: alias));
        }
        var graph = Graph("Reference storage", instructions);
        var engine = new OwnershipAnalysisEngine();
        return new("boundaries/reference-storage/128", $"{count} local storage bindings and disposals through references; Core IR, snapshots excluded", null,
            () => engine.Analyze(graph), () =>
            {
                var result = engine.Analyze(graph, new AnalysisOptions(captureSnapshots: true));
                RequireClean(result);
                var resources = result.Blocks.Single().Exit.Resources;
                if (resources.Length != count || resources.Any(resource => resource.Status != ResourceStatus.Disposed))
                    throw new InvalidOperationException("Reference benchmark failed to dispose every original owner.");
            });
    }

    private static BenchmarkCase AsyncCapabilities(int count)
    {
        var instructions = new List<FlowInstruction>
        {
            new(InstructionKind.CreateAggregate, Site(0), "inputs", resourceId: "inputs")
        };
        for (var index = 0; index < count; index++)
        {
            var value = "value" + index;
            var task = "task" + index;
            instructions.Add(new(InstructionKind.TrackValueTask, Site(instructions.Count), value, resourceId: value));
            instructions.Add(new(InstructionKind.BindAsyncResult, Site(instructions.Count), task, value, resourceId: task));
            instructions.Add(new(InstructionKind.SetAggregateElement, Site(instructions.Count), source: task, owner: "inputs", projection: "[" + index + "]"));
        }
        instructions.Add(new(InstructionKind.BeginAsyncComposition, Site(instructions.Count), "combined", "inputs", resourceId: "combined",
            flags: InstructionFlags.HasAsyncResult, asyncComposition: AsyncCompositionKind.WhenAll));
        instructions.Add(new(InstructionKind.ReceiveAsyncResult, Site(instructions.Count), "results", "combined"));
        for (var index = 0; index < count; index++)
        {
            var result = "result" + index;
            instructions.Add(new(InstructionKind.ReadAggregateElement, Site(instructions.Count), result, "results", projection: "[" + index + "]"));
            instructions.Add(new(InstructionKind.ConsumeValueTask, Site(instructions.Count), source: result));
        }
        var graph = Graph("Composed ValueTask payloads", instructions);
        var engine = new OwnershipAnalysisEngine();
        return new("boundaries/valuetask-whenall/128", $"{count} stable ValueTask identities carried through Task.WhenAll result elements and consumed once; Core IR, snapshots excluded", null,
            () => engine.Analyze(graph), () =>
            {
                var result = engine.Analyze(graph, new AnalysisOptions(captureSnapshots: true));
                RequireClean(result);
                var capabilities = result.Blocks.Single().Exit.ValueTasks;
                if (capabilities.Length != count || capabilities.Any(capability => capability.State != ValueTaskConsumption.Consumed))
                    throw new InvalidOperationException("Composition benchmark lost or failed to consume a ValueTask identity.");
            });
    }

    private static BenchmarkCase HigherRanked(int count)
    {
        var binders = new List<RegionBinder> { new("root") };
        var variables = new List<RegionVariable>();
        var requirements = new List<RegionConstraint>();
        for (var index = 0; index < count; index++)
        {
            var binder = "binder" + index;
            var placeholder = "placeholder" + index;
            var inference = "inference" + index;
            binders.Add(new(binder, "root"));
            variables.Add(new(placeholder, binder, RegionVariableKind.Placeholder));
            variables.Add(new(inference, binder, RegionVariableKind.Inference));
            requirements.Add(new(placeholder, inference));
            requirements.Add(new(inference, placeholder));
        }
        var problem = new HigherRankedRegionProblem(binders, variables, requirements: requirements);
        var solver = new HigherRankedRegionSolver();
        return new("boundaries/higher-ranked/64", $"{count} independent nested binders, {2 * count} variables, and same-universe placeholder/inference equivalence constraints", null,
            () => solver.Solve(problem), () =>
            {
                var result = solver.Solve(problem);
                if (!result.IsComplete || !result.IsCompatible || result.Promotions.Length != 0)
                    throw new InvalidOperationException("Higher-ranked benchmark rejected valid independent universe constraints.");
                for (var index = 0; index < count; index++)
                    if (!result.Closure!.AreEquivalent("placeholder" + index, "inference" + index))
                        throw new InvalidOperationException("Higher-ranked benchmark did not preserve the required equivalence.");
            });
    }

    private static AnalysisGraph Graph(string name, IEnumerable<FlowInstruction> instructions) =>
        new(name, [new FlowBlock(0, instructions, [])], 0, Site(10000));

    private static SourceSite Site(int offset) => new("boundary-benchmark.cs", offset, 1);

    private static void RequireClean(AnalysisResult result)
    {
        if (result.BudgetExceeded || !result.Issues.IsEmpty)
            throw new InvalidOperationException("Boundary benchmark failed semantic verification: " + string.Join(", ", result.Issues.Select(issue => issue.Id + ": " + issue.Message)));
    }
}
