using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace SafeSharp.Core;

public sealed class FlowBlock
{
    public FlowBlock(int id, IEnumerable<FlowInstruction> instructions, IEnumerable<int> successors)
    {
        Id = id;
        Instructions = (instructions ?? throw new ArgumentNullException(nameof(instructions))).ToImmutableArray();
        Successors = (successors ?? throw new ArgumentNullException(nameof(successors))).Distinct().ToImmutableArray();
        if (Instructions.Any(instruction => instruction is null)) throw new ArgumentException("Instructions cannot contain null.", nameof(instructions));
    }

    public int Id { get; }
    public ImmutableArray<FlowInstruction> Instructions { get; }
    public ImmutableArray<int> Successors { get; }
}

/// <summary>A finite control-flow graph. Frontends must make normal and exceptional cleanup edges explicit.</summary>
public sealed class AnalysisGraph
{
    public AnalysisGraph(string name, IEnumerable<FlowBlock> blocks, int entryId, SourceSite site)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Blocks = (blocks ?? throw new ArgumentNullException(nameof(blocks))).ToImmutableArray();
        Site = site ?? throw new ArgumentNullException(nameof(site));
        EntryId = entryId;
        var ids = new HashSet<int>();
        foreach (var block in Blocks)
        {
            if (block is null) throw new ArgumentException("Blocks cannot contain null.", nameof(blocks));
            if (!ids.Add(block.Id)) throw new ArgumentException("Block identifiers must be unique.", nameof(blocks));
        }
        if (!ids.Contains(entryId)) throw new ArgumentException("The entry block must exist.", nameof(entryId));
        if (Blocks.Any(block => block.Successors.Any(successor => !ids.Contains(successor))))
            throw new ArgumentException("Every successor must reference an existing block.", nameof(blocks));
    }

    public string Name { get; }
    public ImmutableArray<FlowBlock> Blocks { get; }
    public int EntryId { get; }
    public SourceSite Site { get; }
}
