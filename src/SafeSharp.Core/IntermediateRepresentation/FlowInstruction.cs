using System;

namespace SafeSharp.Core;

/// <summary>The explicit ownership effects produced by a language frontend.</summary>
public enum InstructionKind
{
    Nop, Allocate, Alias, Move, BorrowShared, BorrowExclusive, Read, Write,
    Dispose, Escape, Return, Suspend, Forget, ReleaseLoan, AssumeNull, AssumeNotNull
}

public enum ResourceOwnership { Owned, Borrowed, External, Scoped }
public enum BorrowKind { Shared, Exclusive }

[Flags]
public enum InstructionFlags
{
    None = 0,
    Implicit = 1,
    AllowsSuspension = 2,
    AsyncDispose = 4,
    Conditional = 8,
    SuppressLeak = 16,
    Transfer = 32,
    NoSuspend = 64,
    /// <summary>A frontend exception boundary. The ownership engine treats it as metadata; a CFG frontend expands its handlers.</summary>
    MayThrow = 128,
    /// <summary>The allocation result or input can be null; an absent resource alternative is retained.</summary>
    MayBeNull = 256
}

/// <summary>
/// Immutable language-neutral ownership instruction. Place identifiers must be stable within the graph.
/// A dotted suffix denotes a field projection; array indices should be normalized by the frontend.
/// </summary>
public sealed class FlowInstruction
{
    public FlowInstruction(InstructionKind kind, SourceSite site, string? target = null,
        string? source = null, string? resourceId = null,
        ResourceOwnership ownership = ResourceOwnership.Owned, string? owner = null,
        InstructionFlags flags = InstructionFlags.None, string? description = null)
    {
        Kind = kind;
        Site = site ?? throw new ArgumentNullException(nameof(site));
        Target = target;
        Source = source;
        ResourceId = resourceId;
        Ownership = ownership;
        Owner = owner;
        Flags = flags;
        Description = description;
        if ((kind == InstructionKind.Allocate || kind == InstructionKind.Alias ||
            kind == InstructionKind.BorrowShared || kind == InstructionKind.BorrowExclusive) && string.IsNullOrEmpty(target))
            throw new ArgumentException("This instruction requires a target place.", nameof(target));
        if ((kind == InstructionKind.Alias || kind == InstructionKind.Move || kind == InstructionKind.BorrowShared ||
            kind == InstructionKind.BorrowExclusive || kind == InstructionKind.Read || kind == InstructionKind.Write ||
            kind == InstructionKind.Dispose || kind == InstructionKind.Escape) && string.IsNullOrEmpty(source))
            throw new ArgumentException("This instruction requires a source place.", nameof(source));
    }

    public InstructionKind Kind { get; }
    public SourceSite Site { get; }
    public string? Target { get; }
    public string? Source { get; }
    public string? ResourceId { get; }
    public ResourceOwnership Ownership { get; }
    public string? Owner { get; }
    public InstructionFlags Flags { get; }
    public string? Description { get; }
    public override string ToString() => Description ?? Kind + (Target is null ? "" : " " + Target) + (Source is null ? "" : " <- " + Source);
}
