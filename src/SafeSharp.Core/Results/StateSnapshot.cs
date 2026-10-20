using System;
using System.Collections.Immutable;

namespace SafeSharp.Core;

[Flags]
public enum ResourceStatus
{
    None = 0, Absent = 1, Alive = 2, Disposed = 4, Transferred = 8, Escaped = 16,
    /// <summary>An asynchronous disposal completion or owned factory result has not been observed or transferred.</summary>
    Pending = 32,
    /// <summary>The asynchronous operation has been observed; successful factory result production is tracked separately.</summary>
    Completed = 64
}

[Flags]
public enum ReferenceAvailability { None = 0, Available = 1, Moved = 2 }

/// <summary>Possible states of a single-consumption ValueTask capability, independent of its result.</summary>
[Flags]
public enum ValueTaskConsumption { None = 0, Available = 1, Consumed = 2, Transferred = 4 }

public sealed class ValueTaskSnapshot
{
    internal ValueTaskSnapshot(string id, ValueTaskConsumption state, SourceSite origin,
        ImmutableArray<string> places, ImmutableArray<SourceSite> evidenceSites)
    { Id = id; State = state; Origin = origin; Places = places; EvidenceSites = evidenceSites; }

    public string Id { get; }
    public ValueTaskConsumption State { get; }
    public SourceSite Origin { get; }
    public ImmutableArray<string> Places { get; }
    public ImmutableArray<SourceSite> EvidenceSites { get; }
}

public sealed class ReferenceSnapshot
{
    internal ReferenceSnapshot(string resourceId, ReferenceAvailability availability, string? loanId,
        string projection, ImmutableArray<string> movedProjections, bool mayBeAbsent = false, bool coversResource = true)
    {
        ResourceId = resourceId;
        Availability = availability;
        LoanId = loanId;
        Projection = projection;
        MovedProjections = movedProjections;
        MayBeAbsent = mayBeAbsent;
        CoversResource = coversResource;
    }

    public string ResourceId { get; }
    public ReferenceAvailability Availability { get; }
    public string? LoanId { get; }
    public string Projection { get; }
    public ImmutableArray<string> MovedProjections { get; }
    public bool MayBeAbsent { get; }
    public bool CoversResource { get; }
}

public sealed class PlaceSnapshot
{
    internal PlaceSnapshot(string place, ImmutableArray<ReferenceSnapshot> references, bool mayBeNull = false)
    {
        Place = place;
        References = references;
        MayBeNull = mayBeNull;
    }

    public string Place { get; }
    public ImmutableArray<ReferenceSnapshot> References { get; }
    public bool MayBeNull { get; }
}

/// <summary>A canonical index and the resource/capability identities stored at that index.</summary>
public sealed class AggregateElementSnapshot
{
    internal AggregateElementSnapshot(string index, ImmutableArray<string> resourceIds, ImmutableArray<string> valueTaskIds)
    { Index = index; ResourceIds = resourceIds; ValueTaskIds = valueTaskIds; }
    public string Index { get; }
    public ImmutableArray<string> ResourceIds { get; }
    public ImmutableArray<string> ValueTaskIds { get; }
}

public sealed class ResourceSnapshot
{
    // Preserve the original signature for friend assemblies without widening its accessibility.
    internal ResourceSnapshot(string id, ResourceOwnership ownership, ResourceStatus status,
        SourceSite origin, ImmutableArray<string> ownerResourceIds, ResourceKind kind,
        ImmutableArray<string> disposedResourceIds, AsyncResultState resultState,
        ImmutableArray<string> producedResourceIds)
        : this(id, ownership, status, origin, ownerResourceIds, kind, disposedResourceIds,
            resultState, producedResourceIds, 1, AsyncCompositionKind.None, default, default, false)
    {
    }

    internal ResourceSnapshot(string id, ResourceOwnership ownership, ResourceStatus status,
        SourceSite origin, ImmutableArray<string> ownerResourceIds, ResourceKind kind = ResourceKind.Resource,
        ImmutableArray<string> disposedResourceIds = default, AsyncResultState resultState = AsyncResultState.None,
        ImmutableArray<string> producedResourceIds = default, int asyncResultDepth = 1,
        AsyncCompositionKind asyncComposition = AsyncCompositionKind.None, ImmutableArray<string> dependencyResourceIds = default,
        ImmutableArray<AggregateElementSnapshot> elements = default, bool summarizesMultipleAggregates = false)
    {
        Id = id;
        Ownership = ownership;
        Status = status;
        Origin = origin;
        OwnerResourceIds = ownerResourceIds;
        Kind = kind;
        DisposedResourceIds = disposedResourceIds.IsDefault ? ImmutableArray<string>.Empty : disposedResourceIds;
        ResultState = resultState;
        ProducedResourceIds = producedResourceIds.IsDefault ? ImmutableArray<string>.Empty : producedResourceIds;
        AsyncResultDepth = asyncResultDepth;
        AsyncComposition = asyncComposition;
        DependencyResourceIds = dependencyResourceIds.IsDefault ? ImmutableArray<string>.Empty : dependencyResourceIds;
        Elements = elements.IsDefault ? ImmutableArray<AggregateElementSnapshot>.Empty : elements;
        SummarizesMultipleAggregates = summarizesMultipleAggregates;
    }

    public string Id { get; }
    public ResourceOwnership Ownership { get; }
    public ResourceStatus Status { get; }
    public SourceSite Origin { get; }
    public ImmutableArray<string> OwnerResourceIds { get; }
    public ResourceKind Kind { get; }
    /// <summary>Resources whose cleanup produced this completion; these are provenance, not owning scopes.</summary>
    public ImmutableArray<string> DisposedResourceIds { get; }
    /// <summary>Factory result production alternatives, independent of completion observation and ownership transfer.</summary>
    public AsyncResultState ResultState { get; }
    /// <summary>The stable result identity associated with this factory carrier, when a result has been materialized.</summary>
    public string? ProducedResourceId => ProducedResourceIds.Length == 1 ? ProducedResourceIds[0] : null;
    /// <summary>Possible existing or newly produced result identities represented by this carrier.</summary>
    public ImmutableArray<string> ProducedResourceIds { get; }
    public int AsyncResultDepth { get; }
    public AsyncCompositionKind AsyncComposition { get; }
    public ImmutableArray<string> DependencyResourceIds { get; }
    public ImmutableArray<AggregateElementSnapshot> Elements { get; }
    public bool SummarizesMultipleAggregates { get; }
}

public sealed class LoanSnapshot
{
    internal LoanSnapshot(string id, string resourceId, string holder, BorrowKind kind,
        SourceSite origin, string? parentId, string projection, bool allowsSuspension, bool mayBeAbsent = false,
        BorrowPhase phase = BorrowPhase.Active, ImmutableArray<SourceSite> activationSites = default)
    {
        Id = id;
        ResourceId = resourceId;
        Holder = holder;
        Kind = kind;
        Origin = origin;
        ParentId = parentId;
        Projection = projection;
        AllowsSuspension = allowsSuspension;
        MayBeAbsent = mayBeAbsent;
        Phase = phase;
        ActivationSites = activationSites.IsDefault ? ImmutableArray<SourceSite>.Empty : activationSites;
    }

    public string Id { get; }
    public string ResourceId { get; }
    public string Holder { get; }
    public BorrowKind Kind { get; }
    public SourceSite Origin { get; }
    public string? ParentId { get; }
    public string Projection { get; }
    public bool AllowsSuspension { get; }
    public bool MayBeAbsent { get; }
    public BorrowPhase Phase { get; }
    public ImmutableArray<SourceSite> ActivationSites { get; }
}

public sealed class StateSnapshot
{
    // Preserve the original signature for friend assemblies without widening its accessibility.
    internal StateSnapshot(ImmutableArray<PlaceSnapshot> places, ImmutableArray<ResourceSnapshot> resources,
        ImmutableArray<LoanSnapshot> loans, bool isReachable)
        : this(places, resources, loans, isReachable, default, default)
    {
    }

    internal StateSnapshot(ImmutableArray<PlaceSnapshot> places, ImmutableArray<ResourceSnapshot> resources,
        ImmutableArray<LoanSnapshot> loans, bool isReachable = true, ImmutableArray<ValueTaskSnapshot> valueTasks = default, ImmutableArray<ReferenceStorageSnapshot> referenceStorage = default)
    {
        Places = places;
        Resources = resources;
        Loans = loans;
        IsReachable = isReachable;
        ValueTasks = valueTasks.IsDefault ? ImmutableArray<ValueTaskSnapshot>.Empty : valueTasks;
        ReferenceStorage = referenceStorage.IsDefault ? ImmutableArray<ReferenceStorageSnapshot>.Empty : referenceStorage;
    }

    public ImmutableArray<PlaceSnapshot> Places { get; }
    public ImmutableArray<ResourceSnapshot> Resources { get; }
    public ImmutableArray<LoanSnapshot> Loans { get; }
    public bool IsReachable { get; }
    /// <summary>Single-consumption capabilities, including ValueTasks with scalar or no results.</summary>
    public ImmutableArray<ValueTaskSnapshot> ValueTasks { get; }
    /// <summary>Aliases of storage locations, distinct from the resource values currently stored there.</summary>
    public ImmutableArray<ReferenceStorageSnapshot> ReferenceStorage { get; }
}
