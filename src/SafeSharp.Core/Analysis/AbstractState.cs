using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace SafeSharp.Core;

// This is a finite may/must product domain over allocation sites, not a concrete C# heap.
// Rust design reference: https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/dataflow.rs
internal sealed class AbstractState : IEquatable<AbstractState>
{
    internal Dictionary<string, Dictionary<string, ReferenceFact>> Places { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, bool> NullablePlaces { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, ResourceFact> Resources { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, LoanFact> Loans { get; } = new(StringComparer.Ordinal);
    internal bool IsUnreachable { get; set; }

    internal AbstractState Clone()
    {
        var result = new AbstractState { IsUnreachable = IsUnreachable };
        foreach (var pair in Places) result.Places.Add(pair.Key, new Dictionary<string, ReferenceFact>(pair.Value, StringComparer.Ordinal));
        foreach (var pair in NullablePlaces) result.NullablePlaces.Add(pair.Key, pair.Value);
        foreach (var pair in Resources) result.Resources.Add(pair.Key, pair.Value);
        foreach (var pair in Loans) result.Loans.Add(pair.Key, pair.Value);
        return result;
    }

    internal IReadOnlyCollection<ReferenceFact> Resolve(string? place)
    {
        if (place is null) return Array.Empty<ReferenceFact>();
        if (Places.TryGetValue(place, out var value)) return value.Values;
        var separator = Math.Max(place.LastIndexOf('.'), place.LastIndexOf('['));
        while (separator > 0)
        {
            if (Places.TryGetValue(place.Substring(0, separator), out value))
            {
                var suffix = place.Substring(separator);
                return value.Values.Select(reference => reference.Project(suffix)).ToArray();
            }
            separator = Math.Max(place.LastIndexOf('.', separator - 1), place.LastIndexOf('[', separator - 1));
        }
        return Array.Empty<ReferenceFact>();
    }

    internal bool CanBeNull(string? place) => place is null || !NullablePlaces.TryGetValue(place, out var mayBeNull) || mayBeNull;

    internal void Assign(string target, IEnumerable<ReferenceFact> references, bool mayBeNull = false)
    {
        var value = new Dictionary<string, ReferenceFact>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (value.TryGetValue(reference.Key, out var previous)) value[reference.Key] = previous.Join(reference);
            else value.Add(reference.Key, reference);
        }
        // Replacing a root also replaces its explicitly materialized projections.
        foreach (var child in Places.Keys.Where(place => PlaceRelations.IsStrictDescendant(place, target)).ToArray())
        {
            Places.Remove(child);
            NullablePlaces.Remove(child);
        }
        Places[target] = value;
        NullablePlaces[target] = mayBeNull;
    }

    internal void Forget(string target)
    {
        Assign(target, Array.Empty<ReferenceFact>(), true);
    }

    internal static AbstractState Join(IEnumerable<AbstractState> inputs)
    {
        var states = inputs.Where(state => !state.IsUnreachable).ToArray();
        if (states.Length == 0) return new AbstractState { IsUnreachable = true };
        var result = states[0].Clone();
        for (var index = 1; index < states.Length; index++) result.JoinWith(states[index]);
        return result;
    }

    private void JoinWith(AbstractState other)
    {
        if (other.IsUnreachable) return;
        foreach (var place in Places.Keys.Union(other.Places.Keys, StringComparer.Ordinal).ToArray())
        {
            Places.TryGetValue(place, out var left);
            other.Places.TryGetValue(place, out var right);
            var joined = new Dictionary<string, ReferenceFact>(StringComparer.Ordinal);
            foreach (var key in ((IEnumerable<string>?)left?.Keys ?? Enumerable.Empty<string>()).Union((IEnumerable<string>?)right?.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal))
            {
                ReferenceFact? l = null;
                ReferenceFact? r = null;
                left?.TryGetValue(key, out l);
                right?.TryGetValue(key, out r);
                joined.Add(key, l is null
                    ? r!.WithAbsence(!Resources.TryGetValue(r.ResourceId, out var leftResource) || leftResource.Status == ResourceStatus.Absent)
                    : r is null
                        ? l.WithAbsence(!other.Resources.TryGetValue(l.ResourceId, out var rightResource) || rightResource.Status == ResourceStatus.Absent)
                        : l.Join(r));
            }
            Places[place] = joined;
            NullablePlaces[place] = CanBeNull(place) || other.CanBeNull(place);
        }

        foreach (var id in Resources.Keys.Union(other.Resources.Keys, StringComparer.Ordinal).ToArray())
        {
            Resources.TryGetValue(id, out var left);
            other.Resources.TryGetValue(id, out var right);
            Resources[id] = left is null ? right!.WithStatus(right.Status | ResourceStatus.Absent)
                : right is null ? left.WithStatus(left.Status | ResourceStatus.Absent) : left.Join(right);
        }

        foreach (var id in Loans.Keys.Union(other.Loans.Keys, StringComparer.Ordinal).ToArray())
        {
            Loans.TryGetValue(id, out var left);
            other.Loans.TryGetValue(id, out var right);
            Loans[id] = left is null ? right!.WithAbsence() : right is null ? left.WithAbsence() : left.Join(right);
        }
    }

    internal void JoinAlternative(AbstractState other)
    {
        if (!IsUnreachable) { JoinWith(other); return; }
        if (other.IsUnreachable) return;
        IsUnreachable = false;
        Places.Clear(); NullablePlaces.Clear(); Resources.Clear(); Loans.Clear();
        foreach (var pair in other.Places) Places[pair.Key] = new Dictionary<string, ReferenceFact>(pair.Value, StringComparer.Ordinal);
        foreach (var pair in other.NullablePlaces) NullablePlaces[pair.Key] = pair.Value;
        foreach (var pair in other.Resources) Resources[pair.Key] = pair.Value;
        foreach (var pair in other.Loans) Loans[pair.Key] = pair.Value;
    }

    internal void ExpireLoans(ISet<string> livePlaces)
    {
        if (Loans.Count == 0) return;
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var place in livePlaces)
        foreach (var reference in Resolve(place))
            if (reference.LoanId is not null) retained.Add(reference.LoanId);
        var queue = new Queue<string>(retained);
        while (queue.Count != 0)
        {
            var id = queue.Dequeue();
            if (!Loans.TryGetValue(id, out var loan)) continue;
            foreach (var parent in loan.ParentIds)
                if (retained.Add(parent)) queue.Enqueue(parent);
        }
        foreach (var id in Loans.Keys.Where(id => !retained.Contains(id)).ToArray()) Loans.Remove(id);
    }

    internal StateSnapshot Snapshot() => new(
        Places.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new PlaceSnapshot(pair.Key,
            pair.Value.Values.OrderBy(reference => reference.Key, StringComparer.Ordinal).Select(reference => new ReferenceSnapshot(
                reference.ResourceId, reference.Availability, reference.LoanId, reference.Projection,
                reference.MovedPaths.OrderBy(path => path, StringComparer.Ordinal).ToImmutableArray(), reference.MayBeAbsent, reference.CoversResource)).ToImmutableArray(), CanBeNull(pair.Key))).ToImmutableArray(),
        Resources.Values.OrderBy(resource => resource.Id, StringComparer.Ordinal).Select(resource => new ResourceSnapshot(
            resource.Id, resource.Ownership, resource.Status, resource.Origin, resource.OwnerIds.OrderBy(id => id, StringComparer.Ordinal).ToImmutableArray(),
            resource.Kind, resource.DisposedResourceIds.OrderBy(id => id, StringComparer.Ordinal).ToImmutableArray())).ToImmutableArray(),
        Loans.Values.OrderBy(loan => loan.Id, StringComparer.Ordinal).Select(loan => new LoanSnapshot(loan.Id, loan.ResourceId,
            loan.Holder, loan.Kind, loan.Origin, loan.ParentIds.OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault(), loan.Projection,
            loan.AllowsSuspension, loan.MayBeAbsent, loan.Phase, loan.ActivationEvidence.Values.Select(evidence => evidence.Site)
                .OrderBy(site => site.Path, StringComparer.Ordinal).ThenBy(site => site.Start).ThenBy(site => site.Length).ToImmutableArray())).ToImmutableArray(), !IsUnreachable);

    public bool Equals(AbstractState? other) => other is not null && IsUnreachable == other.IsUnreachable && (IsUnreachable ||
        MapEquals(NullablePlaces, other.NullablePlaces, (left, right) => left == right)
        && MapEquals(Resources, other.Resources, (left, right) => left.Equals(right))
        && MapEquals(Loans, other.Loans, (left, right) => left.Equals(right))
        && MapEquals(Places, other.Places, (left, right) => MapEquals(left, right, (l, r) => l.Equals(r))));
    public override bool Equals(object? obj) => obj is AbstractState state && Equals(state);
    public override int GetHashCode() => Places.Count ^ Resources.Count ^ Loans.Count;

    private static bool MapEquals<T>(Dictionary<string, T> left, Dictionary<string, T> right, Func<T, T, bool> equals)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && equals(pair.Value, value));
}

internal static class PlaceRelations
{
    internal static bool IsStrictDescendant(string child, string parent) => child.Length > parent.Length && child.StartsWith(parent, StringComparison.Ordinal)
        && (parent.Length == 0 || child[parent.Length] == '.' || child[parent.Length] == '[');
    internal static bool IsPrefix(string prefix, string path) => prefix == path || prefix.Length == 0 || IsStrictDescendant(path, prefix);
    // Corresponds conceptually to rustc's structural place overlap checks; unknown indices remain conservative.
    // https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/places_conflict.rs
    internal static bool Overlaps(string left, string right) => IsPrefix(left, right) || IsPrefix(right, left)
        || left.IndexOf('[') >= 0 && right.IndexOf('[') >= 0
            && left.Substring(0, left.IndexOf('[')) == right.Substring(0, right.IndexOf('['));
}

internal sealed class ReferenceFact : IEquatable<ReferenceFact>
{
    internal ReferenceFact(string resourceId, ReferenceAvailability availability = ReferenceAvailability.Available,
        string? loanId = null, string projection = "", bool mayBeAbsent = false,
        ImmutableHashSet<string>? movedPaths = null, ImmutableHashSet<string>? definiteMovedPaths = null,
        ImmutableDictionary<string, AnalysisEvidence>? evidence = null, bool coversResource = true)
    {
        ResourceId = resourceId;
        Availability = availability;
        LoanId = loanId;
        Projection = projection;
        MayBeAbsent = mayBeAbsent;
        CoversResource = coversResource;
        MovedPaths = movedPaths ?? ImmutableHashSet.Create<string>(StringComparer.Ordinal);
        DefiniteMovedPaths = definiteMovedPaths ?? ImmutableHashSet.Create<string>(StringComparer.Ordinal);
        Evidence = evidence ?? ImmutableDictionary.Create<string, AnalysisEvidence>(StringComparer.Ordinal);
        Key = resourceId.Length + ":" + resourceId + projection.Length + ":" + projection + loanId;
    }

    internal string Key { get; }
    internal string ResourceId { get; }
    internal ReferenceAvailability Availability { get; }
    internal string? LoanId { get; }
    internal string Projection { get; }
    internal bool MayBeAbsent { get; }
    // True when this reference is present on every path on which its resource exists. This retains
    // the correlation needed to return/dispose branch-local allocations without inventing leaks.
    internal bool CoversResource { get; }
    internal ImmutableHashSet<string> MovedPaths { get; }
    internal ImmutableHashSet<string> DefiniteMovedPaths { get; }
    internal ImmutableDictionary<string, AnalysisEvidence> Evidence { get; }
    internal bool MayBeMoved => (Availability & ReferenceAvailability.Moved) != 0 || MovedPaths.Any(path => PlaceRelations.Overlaps(path, Projection));
    internal bool DefinitelyMoved => Availability == ReferenceAvailability.Moved || DefiniteMovedPaths.Any(path => PlaceRelations.Overlaps(path, Projection));

    internal ReferenceFact WithAbsence(bool onlyWhenResourceAbsent) => new(ResourceId, Availability, LoanId, Projection, true,
        MovedPaths, DefiniteMovedPaths, Evidence, CoversResource && onlyWhenResourceAbsent);

    internal ReferenceFact WithPresence() => new(ResourceId, Availability, LoanId, Projection, false,
        MovedPaths, DefiniteMovedPaths, Evidence, CoversResource);

    internal ReferenceFact Join(ReferenceFact other) => new(ResourceId, Availability | other.Availability, LoanId, Projection,
        MayBeAbsent || other.MayBeAbsent, MovedPaths.Union(other.MovedPaths), DefiniteMovedPaths.Intersect(other.DefiniteMovedPaths), Evidence.SetItems(other.Evidence), CoversResource && other.CoversResource);

    internal ReferenceFact Project(string suffix)
    {
        var projection = Projection + suffix;
        var available = Availability;
        if (DefiniteMovedPaths.Any(path => PlaceRelations.IsPrefix(path, projection))) available = ReferenceAvailability.Moved;
        else if (MovedPaths.Any(path => PlaceRelations.IsPrefix(path, projection))) available |= ReferenceAvailability.Moved;
        return new ReferenceFact(ResourceId, available, LoanId, projection, MayBeAbsent,
            MovedPaths.Where(path => PlaceRelations.Overlaps(path, projection)).ToImmutableHashSet(StringComparer.Ordinal),
            DefiniteMovedPaths.Where(path => PlaceRelations.Overlaps(path, projection)).ToImmutableHashSet(StringComparer.Ordinal), Evidence, CoversResource);
    }

    internal ReferenceFact Invalidate(string projection, SourceSite site, bool conditional)
    {
        if (!PlaceRelations.Overlaps(Projection, projection)) return this;
        var availability = Availability;
        var moved = MovedPaths.Add(projection);
        var definite = conditional ? DefiniteMovedPaths : DefiniteMovedPaths.Add(projection);
        if (PlaceRelations.IsPrefix(projection, Projection))
            availability = conditional ? availability | ReferenceAvailability.Moved : ReferenceAvailability.Moved;
        var evidence = new AnalysisEvidence("move", "Ownership was moved from this alias.", site);
        return new ReferenceFact(ResourceId, availability, LoanId, Projection, MayBeAbsent, moved, definite,
            Evidence.SetItem(EvidenceFacts.Key(evidence), evidence), CoversResource);
    }

    public bool Equals(ReferenceFact? other) => other is not null && Key == other.Key && Availability == other.Availability
        && MayBeAbsent == other.MayBeAbsent && CoversResource == other.CoversResource && MovedPaths.SetEquals(other.MovedPaths) && DefiniteMovedPaths.SetEquals(other.DefiniteMovedPaths)
        && EvidenceFacts.Equal(Evidence, other.Evidence);
    public override bool Equals(object? obj) => obj is ReferenceFact other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key) ^ (int)Availability;
}

internal sealed class ResourceFact : IEquatable<ResourceFact>
{
    internal ResourceFact(string id, ResourceOwnership ownership, ResourceStatus status, SourceSite origin,
        InstructionFlags flags, ImmutableHashSet<string> ownerIds, ImmutableDictionary<string, AnalysisEvidence>? evidence = null,
        ResourceKind kind = ResourceKind.Resource, ImmutableHashSet<string>? disposedResourceIds = null)
    {
        Id = id;
        Ownership = ownership;
        Status = status;
        Origin = origin;
        Flags = flags;
        OwnerIds = ownerIds;
        Evidence = evidence ?? ImmutableDictionary.Create<string, AnalysisEvidence>(StringComparer.Ordinal);
        Kind = kind;
        DisposedResourceIds = disposedResourceIds ?? ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    }

    internal string Id { get; }
    internal ResourceOwnership Ownership { get; }
    internal ResourceStatus Status { get; }
    internal SourceSite Origin { get; }
    internal InstructionFlags Flags { get; }
    internal ImmutableHashSet<string> OwnerIds { get; }
    internal ImmutableDictionary<string, AnalysisEvidence> Evidence { get; }
    internal ResourceKind Kind { get; }
    internal ImmutableHashSet<string> DisposedResourceIds { get; }
    internal ResourceStatus OutstandingStatus => Kind == ResourceKind.AsyncDisposalCompletion ? ResourceStatus.Pending : ResourceStatus.Alive;
    internal bool HasOutstandingObligation => (Status & OutstandingStatus) != 0;
    internal ResourceFact WithStatus(ResourceStatus status) => new(Id, Ownership, status, Origin, Flags, OwnerIds, Evidence, Kind, DisposedResourceIds);
    internal ResourceFact Transition(ResourceStatus status, string kind, string message, SourceSite site)
    {
        var evidence = new AnalysisEvidence(kind, message, site);
        return new ResourceFact(Id, Ownership, status, Origin, Flags, OwnerIds, Evidence.SetItem(EvidenceFacts.Key(evidence), evidence), Kind, DisposedResourceIds);
    }
    internal ResourceFact Join(ResourceFact other) => new(Id, Ownership, Status | other.Status, Origin, Flags | other.Flags,
        OwnerIds.Union(other.OwnerIds), Evidence.SetItems(other.Evidence), Kind, DisposedResourceIds.Union(other.DisposedResourceIds));
    public bool Equals(ResourceFact? other) => other is not null && Id == other.Id && Ownership == other.Ownership && Status == other.Status
        && Flags == other.Flags && Kind == other.Kind && OwnerIds.SetEquals(other.OwnerIds) && DisposedResourceIds.SetEquals(other.DisposedResourceIds)
        && EvidenceFacts.Equal(Evidence, other.Evidence);
    public override bool Equals(object? obj) => obj is ResourceFact other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id) ^ (int)Status;
}

internal sealed class LoanFact : IEquatable<LoanFact>
{
    internal LoanFact(string id, string resourceId, string holder, BorrowKind kind, SourceSite origin,
        string projection, bool allowsSuspension, ImmutableHashSet<string> parentIds, bool mayBeAbsent = false,
        BorrowPhase phase = BorrowPhase.Active, ImmutableDictionary<string, AnalysisEvidence>? activationEvidence = null)
    {
        Id = id;
        ResourceId = resourceId;
        Holder = holder;
        Kind = kind;
        Origin = origin;
        Projection = projection;
        AllowsSuspension = allowsSuspension;
        ParentIds = parentIds;
        MayBeAbsent = mayBeAbsent;
        Phase = phase;
        ActivationEvidence = activationEvidence ?? ImmutableDictionary.Create<string, AnalysisEvidence>(StringComparer.Ordinal);
    }
    internal string Id { get; }
    internal string ResourceId { get; }
    internal string Holder { get; }
    internal BorrowKind Kind { get; }
    internal SourceSite Origin { get; }
    internal string Projection { get; }
    internal bool AllowsSuspension { get; }
    internal ImmutableHashSet<string> ParentIds { get; }
    internal bool MayBeAbsent { get; }
    internal BorrowPhase Phase { get; }
    internal ImmutableDictionary<string, AnalysisEvidence> ActivationEvidence { get; }
    internal LoanFact WithAbsence() => new(Id, ResourceId, Holder, Kind, Origin, Projection, AllowsSuspension, ParentIds, true, Phase, ActivationEvidence);
    internal LoanFact Activate(SourceSite site, bool weak)
    {
        var evidence = new AnalysisEvidence("activation", "The exclusive reservation is activated here.", site);
        return new LoanFact(Id, ResourceId, Holder, Kind, Origin, Projection, AllowsSuspension, ParentIds, MayBeAbsent,
            weak ? Phase | BorrowPhase.Active : BorrowPhase.Active, ActivationEvidence.SetItem(EvidenceFacts.Key(evidence), evidence));
    }
    internal LoanFact Join(LoanFact other) => new(Id, ResourceId, Holder, Kind, Origin, Projection, AllowsSuspension && other.AllowsSuspension,
        ParentIds.Union(other.ParentIds), MayBeAbsent || other.MayBeAbsent, Phase | other.Phase, ActivationEvidence.SetItems(other.ActivationEvidence));
    public bool Equals(LoanFact? other) => other is not null && Id == other.Id && MayBeAbsent == other.MayBeAbsent
        && AllowsSuspension == other.AllowsSuspension && Phase == other.Phase && ParentIds.SetEquals(other.ParentIds)
        && EvidenceFacts.Equal(ActivationEvidence, other.ActivationEvidence);
    public override bool Equals(object? obj) => obj is LoanFact other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);
}

internal static class EvidenceFacts
{
    internal static string Key(AnalysisEvidence evidence) => evidence.Kind + ":" + evidence.Site.Path + ":" + evidence.Site.Start + ":" + evidence.Site.Length;
    internal static bool Equal(ImmutableDictionary<string, AnalysisEvidence> left, ImmutableDictionary<string, AnalysisEvidence> right)
        => left.Count == right.Count && left.Keys.All(right.ContainsKey);
}
