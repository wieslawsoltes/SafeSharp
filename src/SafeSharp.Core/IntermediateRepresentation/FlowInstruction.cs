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
    MergeAlias,
    /// <summary>Creates a carrier whose successful completion will yield an owned result; Flags describe that result.</summary>
    BeginAsyncFactory,
    /// <summary>Observes a factory carrier before an await/GetResult fault boundary, without assuming a result was produced.</summary>
    ObserveAsyncFactory,
    /// <summary>Receives a result after successful completion, preserving one payload identity across carrier aliases and repeated receives.</summary>
    ReceiveAsyncResult,
    /// <summary>Binds an existing result identity to a framework carrier, preserving ownership and loans; DeferredAsyncResult marks a promised result whose completion is not yet observed.</summary>
    BindAsyncResult,
    /// <summary>Starts an independent single-consumption ValueTask capability in Target.</summary>
    TrackValueTask,
    /// <summary>Consumes a ValueTask before completion can succeed, fail, or be canceled.</summary>
    ConsumeValueTask,
    /// <summary>Preserves the carrier identity in Target without copying its single-consumption capability.</summary>
    PreserveAwaitable,
    /// <summary>Creates a locally represented aggregate with identity-preserving element caches.</summary>
    CreateAggregate,
    /// <summary>Writes Source into the indexed element of Owner.</summary>
    SetAggregateElement,
    /// <summary>Reads an indexed element of Source into Target.</summary>
    ReadAggregateElement,
    /// <summary>Creates a proxy that captures its task dependencies without observing them.</summary>
    BeginAsyncComposition,
    /// <summary>Receives composition outputs only on a successful continuation.</summary>
    CompleteAsyncComposition,
    /// <summary>Binds Target to the storage locations currently designated by Source.</summary>
    BindReference,
    /// <summary>Unions Source's storage locations into an existing reference Target.</summary>
    MergeReference,
    /// <summary>Ends a temporary storage binding without overwriting the value in its targets.</summary>
    ReleaseReference,
    /// <summary>Observes only a composed proxy, without discarding an ordinary generic factory output.</summary>
    ObserveAsyncComposition,
    /// <summary>Weakens represented aggregate contents which an unmodeled call may replace.</summary>
    InvalidateAggregateContents
}

public enum ResourceOwnership { Owned, Borrowed, External, Scoped }
public enum ResourceKind { Resource, AsyncDisposalCompletion, AsyncFactoryCarrier, Aggregate }

/// <summary>Trusted framework composition semantics; these never imply a fresh payload.</summary>
public enum AsyncCompositionKind { None, WaitAsync, Unwrap, WhenAll, WhenAny }
public enum BorrowKind { Shared, Exclusive }

/// <summary>Whether a carrier's result identity is deferred or represented by existing references in an execution.</summary>
[Flags]
public enum AsyncResultState { None = 0, Deferred = 1, Materialized = 2 }

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
    DisposalAlreadyInvoked = 512,
    /// <summary>BindAsyncResult represents an existing result promised by an asynchronous borrowed-return contract, without claiming completed execution.</summary>
    DeferredAsyncResult = 1024,
    /// <summary>A final borrowed-return Read validates any represented asynchronous result after cleanup without transferring the borrowed value.</summary>
    ValidateReturnedResult = 2048,
    /// <summary>TrackValueTask retains an already represented capability, for a cached nested asynchronous result.</summary>
    PreserveExistingCapability = 4096,
    /// <summary>The composed carrier exposes a result, rather than only completion.</summary>
    HasAsyncResult = 8192
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
        InstructionFlags flags = InstructionFlags.None, string? description = null, int asyncResultDepth = 1,
        AsyncCompositionKind asyncComposition = AsyncCompositionKind.None, string? projection = null)
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
        if (asyncResultDepth < 1 || asyncResultDepth > 16) throw new ArgumentOutOfRangeException(nameof(asyncResultDepth));
        AsyncResultDepth = asyncResultDepth;
        AsyncComposition = asyncComposition;
        Projection = projection;
        if (kind == InstructionKind.SetAggregateElement && string.IsNullOrEmpty(owner))
            throw new ArgumentException("An aggregate element write requires an owner place.", nameof(owner));
        if (kind == InstructionKind.BeginAsyncComposition && asyncComposition == AsyncCompositionKind.None)
            throw new ArgumentException("A composition instruction requires trusted composition semantics.", nameof(asyncComposition));
        if ((kind == InstructionKind.Allocate || kind == InstructionKind.Alias ||
            kind == InstructionKind.BorrowShared || kind == InstructionKind.BorrowExclusive ||
            kind == InstructionKind.ReserveExclusiveBorrow || kind == InstructionKind.BeginAsyncDispose ||
            kind == InstructionKind.MergeAlias || kind == InstructionKind.BeginAsyncFactory ||
            kind == InstructionKind.ReceiveAsyncResult || kind == InstructionKind.BindAsyncResult ||
            kind == InstructionKind.TrackValueTask || kind == InstructionKind.PreserveAwaitable ||
            kind == InstructionKind.CreateAggregate || kind == InstructionKind.ReadAggregateElement || kind == InstructionKind.BeginAsyncComposition ||
            kind == InstructionKind.BindReference || kind == InstructionKind.MergeReference || kind == InstructionKind.ReleaseReference) && string.IsNullOrEmpty(target))
            throw new ArgumentException("This instruction requires a target place.", nameof(target));
        if ((kind == InstructionKind.Alias || kind == InstructionKind.Move || kind == InstructionKind.BorrowShared ||
            kind == InstructionKind.BorrowExclusive || kind == InstructionKind.Read || kind == InstructionKind.Write ||
            kind == InstructionKind.Dispose || kind == InstructionKind.Escape ||
            kind == InstructionKind.ReserveExclusiveBorrow || kind == InstructionKind.ActivateBorrow ||
            kind == InstructionKind.BeginAsyncDispose || kind == InstructionKind.CompleteAsyncDispose ||
            kind == InstructionKind.MergeAlias || kind == InstructionKind.ObserveAsyncFactory ||
            kind == InstructionKind.ReceiveAsyncResult || kind == InstructionKind.BindAsyncResult ||
            kind == InstructionKind.ConsumeValueTask || kind == InstructionKind.PreserveAwaitable ||
            kind == InstructionKind.SetAggregateElement || kind == InstructionKind.ReadAggregateElement ||
            kind == InstructionKind.BeginAsyncComposition || kind == InstructionKind.CompleteAsyncComposition || kind == InstructionKind.ObserveAsyncComposition ||
            kind == InstructionKind.InvalidateAggregateContents ||
            kind == InstructionKind.BindReference || kind == InstructionKind.MergeReference) && string.IsNullOrEmpty(source))
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
    /// <summary>Proven nested carrier layers before a fresh terminal payload, bounded by the frontend summary budget.</summary>
    public int AsyncResultDepth { get; }
    public AsyncCompositionKind AsyncComposition { get; }
    /// <summary>Canonical aggregate index; unknown indices use an asterisk.</summary>
    public string? Projection { get; }
    public override string ToString() => Description ?? Kind + (Target is null ? "" : " " + Target) + (Source is null ? "" : " <- " + Source);
}
