using System.Collections.Immutable;

namespace SafeSharp.Core;

public sealed class InstructionSnapshot
{
    internal InstructionSnapshot(int index, FlowInstruction instruction, StateSnapshot before, StateSnapshot after,
        ImmutableArray<string> liveBefore, ImmutableArray<string> liveAfter)
    {
        Index = index;
        Instruction = instruction;
        Before = before;
        After = after;
        LiveBefore = liveBefore;
        LiveAfter = liveAfter;
    }

    public int Index { get; }
    public FlowInstruction Instruction { get; }
    public StateSnapshot Before { get; }
    public StateSnapshot After { get; }
    public ImmutableArray<string> LiveBefore { get; }
    public ImmutableArray<string> LiveAfter { get; }
}

public sealed class BlockAnalysisResult
{
    internal BlockAnalysisResult(int id, StateSnapshot entry, StateSnapshot exit,
        ImmutableArray<InstructionSnapshot> steps, ImmutableArray<string> liveIn, ImmutableArray<string> liveOut)
    {
        Id = id;
        Entry = entry;
        Exit = exit;
        Steps = steps;
        LiveIn = liveIn;
        LiveOut = liveOut;
    }

    public int Id { get; }
    public StateSnapshot Entry { get; }
    public StateSnapshot Exit { get; }
    public ImmutableArray<InstructionSnapshot> Steps { get; }
    public ImmutableArray<string> LiveIn { get; }
    public ImmutableArray<string> LiveOut { get; }
}

public sealed class AnalysisResult
{
    internal AnalysisResult(AnalysisGraph graph, ImmutableArray<AnalysisIssue> issues,
        ImmutableArray<BlockAnalysisResult> blocks, int iterations, bool budgetExceeded)
    {
        Graph = graph;
        Issues = issues;
        Blocks = blocks;
        Iterations = iterations;
        BudgetExceeded = budgetExceeded;
    }

    public AnalysisGraph Graph { get; }
    public ImmutableArray<AnalysisIssue> Issues { get; }
    /// <summary>Reachable block states, populated only when snapshot capture is enabled.</summary>
    public ImmutableArray<BlockAnalysisResult> Blocks { get; }
    public int Iterations { get; }
    public bool BudgetExceeded { get; }
}
