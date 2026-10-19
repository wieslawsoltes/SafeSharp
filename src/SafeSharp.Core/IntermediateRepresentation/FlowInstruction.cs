using System;

namespace SafeSharp.Core;

/// <summary>The explicit ownership effects produced by a language frontend.</summary>
public enum InstructionKind
{
    Nop, Allocate, Alias, Move, BorrowShared, BorrowExclusive, Read, Write,
    Dispose, Escape, Return, Suspend, Forget, ReleaseLoan, AssumeNull, AssumeNotNull,
    /// <summary>Creates an exclusive reservation which permits reads and shared loans until activation.</summary>
    ReserveExclusiveBorrow,
    /// <summary>Activates the reserved loan referenced by Source, including through aliases.</summary>
    ActivateBorrow,
    /// <summary>Invokes disposal on Source and creates an owned completion obligation in Target.</summary>
    BeginAsyncDispose,
    /// <summary>Observes the asynchronous disposal completion referenced by Source.</summary>
    CompleteAsyncDispose,
    /// <summary>Joins the current Target value with Source as alternative alias origins.</summary>
    MergeAlias
}

public enum ResourceOwnership { Owned, Borrowed, External, Scoped }
public enum ResourceKind { Resource, AsyncDisposalCompletion }
public enum BorrowKind { Shared, Exclusive }

/// <summary>Possible phases of a loan. A joined state can retain both reserved and active alternatives.</summary>
[Flags]
public enum BorrowPhase { None = 0, Reserved = 1, Active = 2 }

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
    MayBeNull = 256,
    /// <summary>BeginAsyncDispose creates only the completion because the frontend already emitted the disposal invocation before an exception boundary.</summary>
    DisposalAlreadyInvoked = 512
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
            kind == InstructionKind.BorrowShared || kind == InstructionKind.BorrowExclusive ||
            kind == InstructionKind.ReserveExclusiveBorrow || kind == InstructionKind.BeginAsyncDispose ||
            kind == InstructionKind.MergeAlias) && string.IsNullOrEmpty(target))
            throw new ArgumentException("This instruction requires a target place.", nameof(target));
        if ((kind == InstructionKind.Alias || kind == InstructionKind.Move || kind == InstructionKind.BorrowShared ||
            kind == InstructionKind.BorrowExclusive || kind == InstructionKind.Read || kind == InstructionKind.Write ||
            kind == InstructionKind.Dispose || kind == InstructionKind.Escape ||
            kind == InstructionKind.ReserveExclusiveBorrow || kind == InstructionKind.ActivateBorrow ||
            kind == InstructionKind.BeginAsyncDispose || kind == InstructionKind.CompleteAsyncDispose ||
            kind == InstructionKind.MergeAlias) && string.IsNullOrEmpty(source))
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
