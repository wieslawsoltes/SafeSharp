using System;
using System.Collections.Immutable;

namespace SafeSharp.Core;

[Flags]
public enum ResourceStatus
{
    None = 0, Absent = 1, Alive = 2, Disposed = 4, Transferred = 8, Escaped = 16,
    /// <summary>The asynchronous disposal awaitable has not been observed or transferred.</summary>
    Pending = 32,
    /// <summary>The asynchronous disposal completion has been observed.</summary>
    Completed = 64
}

[Flags]
public enum ReferenceAvailability { None = 0, Available = 1, Moved = 2 }

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

public sealed class ResourceSnapshot
{
    internal ResourceSnapshot(string id, ResourceOwnership ownership, ResourceStatus status,
        SourceSite origin, ImmutableArray<string> ownerResourceIds, ResourceKind kind = ResourceKind.Resource,
        ImmutableArray<string> disposedResourceIds = default)
    {
        Id = id;
        Ownership = ownership;
        Status = status;
        Origin = origin;
        OwnerResourceIds = ownerResourceIds;
        Kind = kind;
        DisposedResourceIds = disposedResourceIds.IsDefault ? ImmutableArray<string>.Empty : disposedResourceIds;
    }

    public string Id { get; }
    public ResourceOwnership Ownership { get; }
    public ResourceStatus Status { get; }
    public SourceSite Origin { get; }
    public ImmutableArray<string> OwnerResourceIds { get; }
    public ResourceKind Kind { get; }
    /// <summary>Resources whose cleanup produced this completion; these are provenance, not owning scopes.</summary>
    public ImmutableArray<string> DisposedResourceIds { get; }
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
    internal StateSnapshot(ImmutableArray<PlaceSnapshot> places, ImmutableArray<ResourceSnapshot> resources,
        ImmutableArray<LoanSnapshot> loans, bool isReachable = true)
    {
        Places = places;
        Resources = resources;
        Loans = loans;
        IsReachable = isReachable;
    }

    public ImmutableArray<PlaceSnapshot> Places { get; }
    public ImmutableArray<ResourceSnapshot> Resources { get; }
    public ImmutableArray<LoanSnapshot> Loans { get; }
    public bool IsReachable { get; }
}
