using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace SafeSharp.Core;

/// <summary>
/// An affine consumption capability, independent of disposable payload ownership. Copying a
/// ValueTask copies its capability identity; converting it to a reusable task consumes that
/// capability without inventing a fresh payload. This mirrors the separation of Future::Output
/// from polling in Rust, not Rust's execution model:
/// https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/library/core/src/future/future.rs
/// .NET contract: https://learn.microsoft.com/dotnet/api/system.threading.tasks.valuetask-1
/// </summary>
internal sealed class ValueTaskProtocolState : IEquatable<ValueTaskProtocolState>
{
    private readonly Dictionary<string, Dictionary<string, ReferenceFact>> places = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ValueTaskFact> instances = new(StringComparer.Ordinal);
    private readonly ReferenceStorageState? referenceStorage;
    internal ValueTaskProtocolState(ReferenceStorageState? referenceStorage = null) => this.referenceStorage = referenceStorage;

    internal IEnumerable<string> Ids(string? place, AbstractState? state = null) => Resolve(place)
        .Concat(state is null ? Enumerable.Empty<ReferenceFact>() : ContainedProtocols(state, state.Resolve(place)))
        .Select(reference => reference.ResourceId);

    private IEnumerable<ReferenceFact> ContainedProtocols(AbstractState state, IEnumerable<ReferenceFact> resources, bool transferOnly = false)
    {
        foreach (var reference in OwnershipTransfer.IncludingFactoryResults(state, resources, transferOnly))
        {
            if (reference.Projection.Length != 0 || !state.Resources.TryGetValue(reference.ResourceId, out var resource)) continue;
            foreach (var cache in OwnershipTransfer.ContainedCaches(resource, transferOnly))
            foreach (var capability in Resolve(cache))
                yield return reference.CoversResource ? capability : capability.WithAbsence(false);
        }
    }

    internal IReadOnlyCollection<ReferenceFact> Resolve(string? place) => place is null ? Array.Empty<ReferenceFact>() :
        referenceStorage?.TryGetTargets(place, out var targets) == true ? ReferenceStorageState.Read(targets, ResolveDirect) : ResolveDirect(place);

    private IReadOnlyCollection<ReferenceFact> ResolveDirect(string place) => places.TryGetValue(place, out var values)
        ? values.Values : Array.Empty<ReferenceFact>();

    internal void Assign(string target, IEnumerable<ReferenceFact> references)
    {
        if (referenceStorage?.TryGetTargets(target, out var targets) == true)
        {
            var values = references.ToArray();
            foreach (var location in targets)
            {
                if (location == ReferenceStorageState.UnknownTarget) continue;
                AssignDirect(location, targets.Count == 1 ? values : ResolveDirect(location).Concat(values).Select(reference => reference.WithAbsence(false)));
            }
        }
        else AssignDirect(target, references);
    }

    private void AssignDirect(string target, IEnumerable<ReferenceFact> references)
    {
        var values = new Dictionary<string, ReferenceFact>(StringComparer.Ordinal);
        foreach (var reference in references)
            values[reference.Key] = values.TryGetValue(reference.Key, out var prior) ? prior.Join(reference) : reference;
        foreach (var child in places.Keys.Where(place => PlaceRelations.IsStrictDescendant(place, target)).ToArray()) places.Remove(child);
        if (values.Count == 0) places.Remove(target);
        else places[target] = values;
    }

    internal ValueTaskProtocolState Clone()
    {
        var result = new ValueTaskProtocolState(referenceStorage);
        result.CopyFrom(this);
        return result;
    }

    internal void CopyFrom(ValueTaskProtocolState other)
    {
        places.Clear(); instances.Clear();
        foreach (var pair in other.places) places.Add(pair.Key, new Dictionary<string, ReferenceFact>(pair.Value, StringComparer.Ordinal));
        foreach (var pair in other.instances) instances.Add(pair.Key, pair.Value);
    }

    internal void JoinWith(ValueTaskProtocolState other)
    {
        foreach (var place in places.Keys.Union(other.places.Keys, StringComparer.Ordinal).ToArray())
        {
            var left = Resolve(place).ToDictionary(reference => reference.Key, StringComparer.Ordinal);
            var right = other.Resolve(place).ToDictionary(reference => reference.Key, StringComparer.Ordinal);
            var joined = new Dictionary<string, ReferenceFact>(StringComparer.Ordinal);
            foreach (var key in left.Keys.Union(right.Keys, StringComparer.Ordinal))
            {
                left.TryGetValue(key, out var l); right.TryGetValue(key, out var r);
                joined.Add(key, l is null ? r!.WithAbsence(!instances.ContainsKey(r.ResourceId))
                    : r is null ? l.WithAbsence(!other.instances.ContainsKey(l.ResourceId)) : l.Join(r));
            }
            if (joined.Count != 0) places[place] = joined;
        }
        foreach (var pair in other.instances)
            instances[pair.Key] = instances.TryGetValue(pair.Key, out var old) ? old.Join(pair.Value) : pair.Value;
    }

    internal void Apply(FlowInstruction instruction, AbstractState state, int blockId, int index, ISet<string> liveAfter, IssueCollector? issues)
    {
        if (instances.Count == 0 && instruction.Kind != InstructionKind.TrackValueTask ||
            instruction.Kind is InstructionKind.BindReference or InstructionKind.MergeReference or InstructionKind.ReleaseReference) return;
        var references = Resolve(instruction.Source).ToArray();
        switch (instruction.Kind)
        {
            case InstructionKind.TrackValueTask:
            {
                if ((instruction.Flags & InstructionFlags.PreserveExistingCapability) != 0 && Resolve(instruction.Target).Count != 0) break;
                var carrierOwners = instruction.Owner is null ? Array.Empty<ReferenceFact>() : state.Resolve(instruction.Owner)
                    .Where(owner => owner.Projection.Length == 0).ToArray();
                if (carrierOwners.Length != 0)
                {
                    var capabilities = new List<ReferenceFact>();
                    foreach (var owner in carrierOwners)
                    {
                        var cache = OwnershipTransfer.AsyncResultCache(owner.ResourceId);
                        var existing = Resolve(cache);
                        if (existing.Count == 0)
                        {
                            var capability = Track("value-task-result:" + owner.ResourceId, instruction, state, liveAfter);
                            if (!owner.CoversResource) capability = capability.WithAbsence(false);
                            else if (owner.MayBeAbsent) capability = capability.WithAbsence(true);
                            Assign(cache, new[] { capability });
                            if (state.Resources.TryGetValue(owner.ResourceId, out var carrier))
                            {
                                state.Assign(cache, state.Resolve(instruction.Target), state.CanBeNull(instruction.Target));
                                state.Resources[carrier.Id] = carrier.WithResultState(carrier.ResultState | AsyncResultState.Materialized);
                            }
                            capabilities.Add(capability);
                        }
                        else capabilities.AddRange(existing.Select(value => !owner.CoversResource ? value.WithAbsence(false) :
                            owner.MayBeAbsent ? value.WithAbsence(true) : value));
                    }
                    Assign(instruction.Target!, capabilities);
                }
                else Assign(instruction.Target!, new[] { Track(instruction.ResourceId ?? "value-task:" + blockId + ":" + index, instruction, state, liveAfter) });
                break;
            }
            case InstructionKind.ConsumeValueTask:
                Consume(instruction, references, ValueTaskConsumption.Consumed, issues);
                break;
            case InstructionKind.Alias:
            case InstructionKind.BorrowShared:
            case InstructionKind.BorrowExclusive:
            case InstructionKind.ReserveExclusiveBorrow:
                Assign(instruction.Target!, references);
                break;
            case InstructionKind.MergeAlias:
            {
                var alternative = Clone();
                alternative.Assign(instruction.Target!, references);
                JoinWith(alternative);
                break;
            }
            case InstructionKind.Move:
                var contained = ContainedProtocols(state, state.Resolve(instruction.Source), transferOnly: true).ToArray();
                if (instruction.Target is null)
                {
                    Validate(instruction, references.Concat(ContainedProtocols(state, state.Resolve(instruction.Source))), issues);
                    Consume(instruction, references.Concat(contained).ToArray(), ValueTaskConsumption.Transferred, issues, validate: false);
                }
                else
                {
                    var caches = OwnershipTransfer.IncludingFactoryResults(state, state.Resolve(instruction.Source), transferOnly: true)
                        .Where(reference => reference.Projection.Length == 0 && state.Resources.ContainsKey(reference.ResourceId))
                        .SelectMany(reference => OwnershipTransfer.ContainedCaches(state.Resources[reference.ResourceId], transferOnly: true))
                        .Distinct(StringComparer.Ordinal).ToDictionary(cache => cache, cache => Resolve(cache).ToArray(), StringComparer.Ordinal);
                    foreach (var reference in references.Concat(contained))
                    foreach (var place in places.Values)
                    foreach (var key in place.Keys.ToArray())
                        if (place[key].ResourceId == reference.ResourceId)
                            place[key] = place[key].Invalidate("", instruction.Site, !reference.CoversResource);
                    foreach (var cache in caches) Assign(cache.Key, cache.Value);
                    Assign(instruction.Target, references);
                }
                break;
            case InstructionKind.Read when (instruction.Flags & InstructionFlags.ValidateReturnedResult) != 0:
                Validate(instruction, references.Concat(ContainedProtocols(state, state.Resolve(instruction.Source))), issues);
                break;
            case InstructionKind.Return:
            case InstructionKind.Escape:
                Validate(instruction, references.Concat(ContainedProtocols(state, state.Resolve(instruction.Source))), issues);
                Consume(instruction, references.Concat(ContainedProtocols(state, state.Resolve(instruction.Source), transferOnly: true)).ToArray(), ValueTaskConsumption.Transferred, issues, validate: false);
                break;
            case InstructionKind.SetAggregateElement:
                var owners = state.Resolve(instruction.Owner);
                if (owners.Count == 0 || owners.Any(owner => owner.Projection.Length != 0 || !state.Resources.TryGetValue(owner.ResourceId, out var aggregate) || aggregate.Kind != ResourceKind.Aggregate))
                {
                    var mayRetainLocally = owners.Any(owner => owner.Projection.Length == 0 && state.Resources.TryGetValue(owner.ResourceId, out var aggregate) && aggregate.Kind == ResourceKind.Aggregate);
                    var represented = references.Concat(ContainedProtocols(state, state.Resolve(instruction.Source)));
                    Validate(instruction, mayRetainLocally ? represented.Select(value => value.WithAbsence(false)) : represented, issues);
                    var stored = references.Concat(ContainedProtocols(state, state.Resolve(instruction.Source), transferOnly: true));
                    Consume(instruction, (mayRetainLocally ? stored.Select(value => value.WithAbsence(false)) : stored).ToArray(), ValueTaskConsumption.Transferred, issues, validate: false);
                }
                break;
            case InstructionKind.BindAsyncResult:
                // The core binder snapshots resource and capability caches together before a
                // repeated carrier site can replace or weakly join its earlier generation.
                Assign(instruction.Target!, Array.Empty<ReferenceFact>());
                break;
            case InstructionKind.ReceiveAsyncResult:
                Assign(instruction.Target!, state.Resolve(instruction.Source).Where(carrier => carrier.Projection.Length == 0)
                    .SelectMany(carrier => Resolve(OwnershipTransfer.AsyncResultCache(carrier.ResourceId))));
                break;
            case InstructionKind.CreateAggregate:
            case InstructionKind.BeginAsyncComposition:
            case InstructionKind.Allocate:
            case InstructionKind.BeginAsyncFactory:
            case InstructionKind.BeginAsyncDispose:
            case InstructionKind.Forget:
            case InstructionKind.ReleaseLoan:
            case InstructionKind.PreserveAwaitable:
                if (instruction.Target is not null) Assign(instruction.Target, Array.Empty<ReferenceFact>());
                break;
        }
    }

    private ReferenceFact Track(string id, FlowInstruction instruction, AbstractState state, ISet<string> liveAfter)
    {
        var fact = new ValueTaskFact(id, ValueTaskConsumption.Available, instruction.Site);
        // A repeated site is fresh only if no direct or nested live alias names an older
        // capability. A cached Task output has one capability across all receive sites.
        if (instances.TryGetValue(id, out var previous) && liveAfter.Where(place => place != instruction.Target).SelectMany(Resolve)
            .Concat(ContainedProtocols(state, liveAfter.Where(place => place != instruction.Target).SelectMany(state.Resolve)))
            .Any(reference => reference.ResourceId == id && !reference.DefinitelyMoved)) fact = fact.Join(previous);
        instances[id] = fact;
        return new ReferenceFact(id);
    }

    internal void RenewCarrier(string carrierId, bool retained)
    {
        if (instances.Count == 0) return;
        var cache = OwnershipTransfer.AsyncResultCache(carrierId);
        var previous = Resolve(cache);
        if (previous.Count == 0) return;
        Assign(cache, retained ? previous.Select(reference => reference.WithAbsence(false)) : Array.Empty<ReferenceFact>());
    }

    private void Consume(FlowInstruction instruction, ReferenceFact[] references, ValueTaskConsumption consumption, IssueCollector? issues, bool validate = true)
    {
        foreach (var reference in references.GroupBy(value => value.Key, StringComparer.Ordinal).Select(group => group.Aggregate((left, right) => left.Join(right))))
        {
            if (!instances.TryGetValue(reference.ResourceId, out var fact)) continue;
            if (validate) CheckConsumption(instruction, reference, fact, issues);
            var evidence = new AnalysisEvidence(consumption == ValueTaskConsumption.Transferred ? "value-task-transfer" : "value-task-consumption",
                consumption == ValueTaskConsumption.Transferred ? "The single-consumption capability was transferred here." : "The ValueTask was consumed here, including if completion faults or is canceled.", instruction.Site);
            instances[fact.Id] = new ValueTaskFact(fact.Id,
                reference.CoversResource ? consumption : fact.State | consumption, fact.Origin,
                fact.Evidence.SetItem(EvidenceFacts.Key(evidence), evidence));
        }
    }

    private void Validate(FlowInstruction instruction, IEnumerable<ReferenceFact> references, IssueCollector? issues)
    {
        if (issues is null) return;
        foreach (var reference in references.GroupBy(value => value.Key, StringComparer.Ordinal).Select(group => group.Aggregate((left, right) => left.Join(right))))
            if (instances.TryGetValue(reference.ResourceId, out var fact)) CheckConsumption(instruction, reference, fact, issues);
    }

    private static void CheckConsumption(FlowInstruction instruction, ReferenceFact reference, ValueTaskFact fact, IssueCollector? issues)
    {
        var invalid = reference.MayBeMoved || (fact.State & (ValueTaskConsumption.Consumed | ValueTaskConsumption.Transferred)) != 0;
        if (invalid)
        {
            var definite = !reference.MayBeAbsent && (reference.DefinitelyMoved || (fact.State & ValueTaskConsumption.Available) == 0);
            issues?.Add("SS0048", "This ValueTask capability has already been consumed or transferred. Await it once, or retain the reusable result of AsTask or Preserve.",
                instruction.Site, definite && (instruction.Flags & InstructionFlags.Conditional) == 0 ? IssueCertainty.Definite : IssueCertainty.Possible,
                fact.Id, fact.Evidence.Values.Concat(reference.Evidence.Values));
        }
    }

    internal ImmutableArray<ValueTaskSnapshot> Snapshot() => instances.Values.OrderBy(fact => fact.Id, StringComparer.Ordinal)
        .Select(fact => new ValueTaskSnapshot(fact.Id, fact.State, fact.Origin,
            places.Where(place => place.Value.Values.Any(reference => reference.ResourceId == fact.Id))
                .Select(place => place.Key).OrderBy(place => place, StringComparer.Ordinal).ToImmutableArray(),
            fact.Evidence.Values.Select(evidence => evidence.Site).Distinct().OrderBy(site => site.Path, StringComparer.Ordinal)
                .ThenBy(site => site.Start).ThenBy(site => site.Length).ToImmutableArray())).ToImmutableArray();

    public bool Equals(ValueTaskProtocolState? other) => other is not null && places.Count == other.places.Count && instances.Count == other.instances.Count &&
        places.All(pair => other.places.TryGetValue(pair.Key, out var values) && pair.Value.Count == values.Count &&
            pair.Value.All(reference => values.TryGetValue(reference.Key, out var value) && reference.Value.Equals(value))) &&
        instances.All(pair => other.instances.TryGetValue(pair.Key, out var value) && pair.Value.Equals(value));
    public override bool Equals(object? obj) => obj is ValueTaskProtocolState other && Equals(other);
    public override int GetHashCode() => places.Count ^ instances.Count;
}

internal sealed class ValueTaskFact : IEquatable<ValueTaskFact>
{
    internal ValueTaskFact(string id, ValueTaskConsumption state, SourceSite origin, ImmutableDictionary<string, AnalysisEvidence>? evidence = null)
    {
        Id = id; State = state; Origin = origin;
        var acquisition = new AnalysisEvidence("value-task-origin", "This operation creates a single-consumption ValueTask capability.", origin);
        Evidence = evidence ?? ImmutableDictionary.Create<string, AnalysisEvidence>(StringComparer.Ordinal).Add(EvidenceFacts.Key(acquisition), acquisition);
    }
    internal string Id { get; }
    internal ValueTaskConsumption State { get; }
    internal SourceSite Origin { get; }
    internal ImmutableDictionary<string, AnalysisEvidence> Evidence { get; }
    internal ValueTaskFact Join(ValueTaskFact other) => new(Id, State | other.State, Origin, Evidence.SetItems(other.Evidence));
    public bool Equals(ValueTaskFact? other) => other is not null && Id == other.Id && State == other.State && EvidenceFacts.Equal(Evidence, other.Evidence);
    public override bool Equals(object? obj) => obj is ValueTaskFact other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id) ^ (int)State;
}
