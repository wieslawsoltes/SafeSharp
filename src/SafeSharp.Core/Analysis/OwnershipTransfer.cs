using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace SafeSharp.Core;

/// <summary>Transfer semantics and source-grounded rule evaluation, independent of the fixed-point scheduling policy.</summary>
internal sealed class OwnershipTransfer
{
    private readonly AnalysisOptions options;
    internal OwnershipTransfer(AnalysisOptions options) => this.options = options;

    internal void Apply(AbstractState state, FlowInstruction instruction, int blockId, int instructionIndex,
        ISet<string> liveAfter, IssueCollector? issues)
    {
        if (state.IsUnreachable) return;
        var alternative = (instruction.Flags & InstructionFlags.Conditional) != 0 ? state.Clone() : null;
        var references = state.Resolve(instruction.Source).ToArray();
        var sourceMayBeNull = state.CanBeNull(instruction.Source);
        issues?.BeginInstruction(instruction, state, references);
        switch (instruction.Kind)
        {
            case InstructionKind.Allocate:
                Allocate(state, instruction, blockId, instructionIndex, liveAfter, issues);
                break;
            case InstructionKind.Alias:
                CheckMoved(state, instruction, references, issues);
                CheckOverwrite(state, instruction, instruction.Target!, references, issues, liveAfter);
                state.Assign(instruction.Target!, references, sourceMayBeNull);
                break;
            case InstructionKind.MergeAlias:
                CheckMoved(state, instruction, references.Concat(state.Resolve(instruction.Target)).ToArray(), issues);
                CheckOverwrite(state, instruction, instruction.Target!, references, issues, liveAfter);
                var sourceChoice = state.Clone();
                sourceChoice.Assign(instruction.Target!, references, sourceMayBeNull);
                // Expire each alternative before joining: unselected temporary loans are only possibly live.
                sourceChoice.ExpireLoans(liveAfter);
                state.ExpireLoans(liveAfter);
                state.JoinAlternative(sourceChoice);
                break;
            case InstructionKind.Move:
                CheckUse(state, instruction, references, issues);
                CheckLoans(state, instruction, references, "SS0005", "Ownership cannot move while a loan remains live.", _ => true, false, issues);
                CheckBorrowedEscape(state, instruction, references, issues);
                TransferFactoryResults(state, instruction, references, issues);
                if (instruction.Target is not null) CheckOverwrite(state, instruction, instruction.Target, references, issues, liveAfter);
                foreach (var reference in references)
                {
                    InvalidateAliases(state, reference, instruction.Site, !reference.CoversResource);
                    if (instruction.Target is null && reference.Projection.Length == 0 && state.Resources.TryGetValue(reference.ResourceId, out var resource))
                        state.Resources[reference.ResourceId] = resource.Transition(TransferStatus(resource.Status, ResourceStatus.Transferred,
                            !reference.CoversResource), "transfer", "Ownership was consumed by a call or transferred out of the method.", instruction.Site);
                }
                if (instruction.Target is not null)
                    state.Assign(instruction.Target, references.Select(reference => new ReferenceFact(reference.ResourceId,
                        reference.MayBeMoved ? ReferenceAvailability.Available | ReferenceAvailability.Moved : ReferenceAvailability.Available,
                        reference.LoanId, reference.Projection, reference.MayBeAbsent, coversResource: reference.CoversResource)), sourceMayBeNull);
                break;
            case InstructionKind.BorrowShared:
            case InstructionKind.BorrowExclusive:
            case InstructionKind.ReserveExclusiveBorrow:
                Borrow(state, instruction, references, blockId, instructionIndex, liveAfter, issues);
                break;
            case InstructionKind.ActivateBorrow:
                ActivateBorrow(state, instruction, references, issues);
                break;
            case InstructionKind.Read:
                var readReferences = (instruction.Flags & InstructionFlags.ValidateReturnedResult) != 0
                    ? IncludingFactoryResults(state, references).ToArray() : references;
                CheckUse(state, instruction, readReferences, issues);
                CheckLoans(state, instruction, readReferences, "SS0013", "The owner cannot be read while an active exclusive loan remains live.",
                    IsPossiblyActiveExclusive, true, issues, definitelyConflicts: IsDefinitelyActiveExclusive);
                break;
            case InstructionKind.Write:
                CheckUse(state, instruction, references, issues);
                CheckLoans(state, instruction, references, "SS0007", "Mutation conflicts with a live shared or exclusive loan.", _ => true, true, issues, allowOwnExclusive: true);
                break;
            case InstructionKind.Dispose:
                Dispose(state, instruction, references, issues);
                break;
            case InstructionKind.BeginAsyncDispose:
                if ((instruction.Flags & InstructionFlags.DisposalAlreadyInvoked) == 0)
                    Dispose(state, instruction, references, issues);
                BeginAsyncDispose(state, instruction, references, blockId, instructionIndex, liveAfter, issues);
                break;
            case InstructionKind.CompleteAsyncDispose:
                CheckMoved(state, instruction, references, issues);
                foreach (var reference in references)
                    if (reference.Projection.Length == 0 && state.Resources.TryGetValue(reference.ResourceId, out var completion) &&
                        completion.Kind == ResourceKind.AsyncDisposalCompletion)
                        state.Resources[reference.ResourceId] = completion.Transition(TransferStatus(completion.Status, ResourceStatus.Completed,
                            !reference.CoversResource), "completion", "Asynchronous disposal completion is observed here.", instruction.Site);
                break;
            case InstructionKind.BeginAsyncFactory:
                BeginAsyncFactory(state, instruction, blockId, instructionIndex, liveAfter, issues);
                break;
            case InstructionKind.ObserveAsyncFactory:
                CheckMoved(state, instruction, references, issues);
                foreach (var reference in references)
                    if (reference.Projection.Length == 0 && state.Resources.TryGetValue(reference.ResourceId, out var factory) &&
                        factory.Kind == ResourceKind.AsyncFactoryCarrier)
                        state.Resources[reference.ResourceId] = factory.Transition(TransferStatus(factory.Status, ResourceStatus.Completed,
                            !reference.CoversResource), "factory-observation", "Factory completion is observed; a fault does not produce an owned result.", instruction.Site);
                break;
            case InstructionKind.ReceiveAsyncResult:
                ReceiveAsyncResult(state, instruction, references, blockId, instructionIndex, liveAfter, issues);
                break;
            case InstructionKind.BindAsyncResult:
                BindAsyncResult(state, instruction, references, blockId, instructionIndex, liveAfter, issues);
                break;
            case InstructionKind.Escape:
            case InstructionKind.Return:
                CheckUse(state, instruction, references, issues);
                CheckBorrowedEscape(state, instruction, references, issues);
                CheckLoans(state, instruction, references, "SS0005", "Ownership cannot escape while a loan remains live.", _ => true, false, issues);
                TransferFactoryResults(state, instruction, references, issues);
                foreach (var reference in references)
                {
                    if (reference.Projection.Length != 0 || !state.Resources.TryGetValue(reference.ResourceId, out var resource)
                        || resource.Ownership != ResourceOwnership.Owned || reference.LoanId is not null) continue;
                    var status = instruction.Kind == InstructionKind.Return ? ResourceStatus.Transferred : ResourceStatus.Escaped;
                    state.Resources[reference.ResourceId] = resource.Transition(TransferStatus(resource.Status, status,
                        !reference.CoversResource), "escape", "Responsibility for this resource leaves the current method.", instruction.Site);
                }
                break;
            case InstructionKind.Suspend:
                if (options.CheckSuspension) CheckSuspension(state, instruction, liveAfter, issues);
                break;
            case InstructionKind.Forget:
                if (instruction.Target is not null)
                {
                    CheckOverwrite(state, instruction, instruction.Target, Array.Empty<ReferenceFact>(), issues, liveAfter);
                    state.Forget(instruction.Target);
                }
                break;
            case InstructionKind.ReleaseLoan:
                if (instruction.Target is not null) state.Forget(instruction.Target);
                break;
            case InstructionKind.Nop:
                break;
            case InstructionKind.AssumeNull:
                if (!state.CanBeNull(instruction.Source)) state.IsUnreachable = true;
                else if (instruction.Source is not null)
                {
                    foreach (var reference in references)
                        if (reference.CoversResource && reference.Projection.Length == 0 && state.Resources.TryGetValue(reference.ResourceId, out var resource))
                            state.Resources[reference.ResourceId] = resource.WithStatus(ResourceStatus.Absent);
                    state.Forget(instruction.Source);
                }
                break;
            case InstructionKind.AssumeNotNull:
                if (instruction.Source is not null)
                {
                    if (state.Places.ContainsKey(instruction.Source) && references.Length == 0) state.IsUnreachable = true;
                    else
                    {
                        state.NullablePlaces[instruction.Source] = false;
                        if (references.Length == 1)
                        {
                            var reference = references[0];
                            state.Assign(instruction.Source, new[] { reference.WithPresence() });
                            if (reference.CoversResource && reference.Projection.Length == 0 && state.Resources.TryGetValue(reference.ResourceId, out var resource))
                            {
                                var status = resource.Status & ~ResourceStatus.Absent;
                                if (status == ResourceStatus.None) state.IsUnreachable = true;
                                else state.Resources[reference.ResourceId] = resource.WithStatus(status);
                            }
                        }
                    }
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(instruction), instruction.Kind, "Unknown ownership instruction.");
        }
        if (alternative is not null) state.JoinAlternative(alternative);
        issues?.EndInstruction();
    }

    private static void Allocate(AbstractState state, FlowInstruction instruction, int blockId, int index, ISet<string> liveAfter, IssueCollector? issues)
    {
        var id = instruction.ResourceId ?? "allocation:" + blockId + ":" + index;
        var owners = state.Resolve(instruction.Owner).Select(reference => reference.ResourceId).ToImmutableHashSet(StringComparer.Ordinal);
        var mayBeNull = (instruction.Flags & InstructionFlags.MayBeNull) != 0;
        var resource = new ResourceFact(id, instruction.Ownership, ResourceStatus.Alive | (mayBeNull ? ResourceStatus.Absent : ResourceStatus.None), instruction.Site, instruction.Flags, owners);
        TrackAllocation(state, instruction, resource, mayBeNull, liveAfter, issues);
    }

    private static void BeginAsyncDispose(AbstractState state, FlowInstruction instruction, ReferenceFact[] references,
        int blockId, int index, ISet<string> liveAfter, IssueCollector? issues)
    {
        var id = instruction.ResourceId ?? "completion:" + blockId + ":" + index;
        var evidence = ImmutableDictionary.CreateBuilder<string, AnalysisEvidence>(StringComparer.Ordinal);
        foreach (var fact in FactsFor(state, references)) evidence[EvidenceFacts.Key(fact)] = fact;
        var start = new AnalysisEvidence("async-disposal", "Cleanup was invoked; its completion still needs to be observed or transferred.", instruction.Site);
        evidence[EvidenceFacts.Key(start)] = start;
        var completion = new ResourceFact(id, ResourceOwnership.Owned, ResourceStatus.Pending, instruction.Site, InstructionFlags.None,
            ImmutableHashSet.Create<string>(StringComparer.Ordinal), evidence.ToImmutable(), ResourceKind.AsyncDisposalCompletion,
            references.Select(reference => reference.ResourceId).ToImmutableHashSet(StringComparer.Ordinal));
        TrackAllocation(state, instruction, completion, false, liveAfter, issues);
    }

    // Rust's Future::Output and Poll::Ready separate an asynchronous computation from its produced
    // value. C# Task additionally permits receiving the same cached result repeatedly. This original
    // adaptation retains both identities and never allocates the result on a failing completion edge.
    // https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/library/core/src/future/future.rs
    private static void BeginAsyncFactory(AbstractState state, FlowInstruction instruction, int blockId, int index,
        ISet<string> liveAfter, IssueCollector? issues)
    {
        var id = instruction.ResourceId ?? "async-factory:" + blockId + ":" + index;
        var fact = new AnalysisEvidence("async-factory", "A successful factory completion must yield an owned result that is disposed or transferred.", instruction.Site);
        var evidence = ImmutableDictionary.Create<string, AnalysisEvidence>(StringComparer.Ordinal).Add(EvidenceFacts.Key(fact), fact);
        var carrier = new ResourceFact(id, ResourceOwnership.Owned, ResourceStatus.Pending, instruction.Site, instruction.Flags,
            ImmutableHashSet.Create<string>(StringComparer.Ordinal), evidence, ResourceKind.AsyncFactoryCarrier,
            resultState: AsyncResultState.Deferred);
        TrackAllocation(state, instruction, carrier, false, liveAfter, issues);
    }

    private const string AsyncResultCachePrefix = "$async-result-cache:";
    internal static string AsyncResultCache(string carrierId) => AsyncResultCachePrefix + carrierId;

    private static void BindAsyncResult(AbstractState state, FlowInstruction instruction, ReferenceFact[] references,
        int blockId, int index, ISet<string> liveAfter, IssueCollector? issues)
    {
        CheckMoved(state, instruction, references, issues);
        var nullable = state.CanBeNull(instruction.Source);
        var id = instruction.ResourceId ?? "completed-factory:" + blockId + ":" + index;
        var pending = (instruction.Flags & InstructionFlags.DeferredAsyncResult) != 0;
        var fact = new AnalysisEvidence("factory-binding", pending
            ? "An asynchronous return contract retains this existing result identity without assuming that completion has occurred."
            : "A framework result producer wraps this already available value without creating a second owned resource.", instruction.Site);
        var evidence = ImmutableDictionary.Create<string, AnalysisEvidence>(StringComparer.Ordinal).Add(EvidenceFacts.Key(fact), fact);
        var carrier = new ResourceFact(id, ResourceOwnership.Owned, pending ? ResourceStatus.Pending : ResourceStatus.Completed, instruction.Site, InstructionFlags.SuppressLeak,
            ImmutableHashSet.Create<string>(StringComparer.Ordinal), evidence, ResourceKind.AsyncFactoryCarrier,
            resultState: AsyncResultState.Materialized);
        TrackAllocation(state, instruction, carrier, false, liveAfter, issues);
        state.Assign(AsyncResultCache(id), references, nullable);
    }

    internal static IEnumerable<ReferenceFact> IncludingFactoryResults(AbstractState state, IEnumerable<ReferenceFact> references)
    {
        foreach (var reference in references)
        {
            yield return reference;
            if (reference.Projection.Length != 0 || !state.Resources.TryGetValue(reference.ResourceId, out var carrier) ||
                carrier.Kind != ResourceKind.AsyncFactoryCarrier || (carrier.ResultState & AsyncResultState.Materialized) == 0) continue;
            foreach (var result in state.Resolve(AsyncResultCache(carrier.Id)))
                yield return reference.CoversResource ? result : result.WithAbsence(false);
        }
    }

    private static void TransferFactoryResults(AbstractState state, FlowInstruction instruction, ReferenceFact[] references, IssueCollector? issues)
    {
        foreach (var reference in references)
        {
            if (reference.Projection.Length != 0 || !state.Resources.TryGetValue(reference.ResourceId, out var carrier) ||
                carrier.Kind != ResourceKind.AsyncFactoryCarrier || (carrier.ResultState & AsyncResultState.Materialized) == 0) continue;
            var cache = AsyncResultCache(carrier.Id);
            var cached = state.Resolve(cache).ToArray();
            var values = reference.CoversResource ? cached : cached.Select(value => value.WithAbsence(false)).ToArray();
            CheckUse(state, instruction, values, issues);
            CheckBorrowedEscape(state, instruction, values, issues);
            CheckLoans(state, instruction, values, "SS0005", "A factory carrier cannot transfer its received result while a loan to that result remains live.",
                _ => true, false, issues);
            var localMove = instruction.Kind == InstructionKind.Move && instruction.Target is not null;
            foreach (var value in values)
            {
                if (instruction.Kind == InstructionKind.Move) InvalidateAliases(state, value, instruction.Site, !value.CoversResource);
                if (localMove || value.Projection.Length != 0 || value.LoanId is not null ||
                    !state.Resources.TryGetValue(value.ResourceId, out var resource) || resource.Ownership != ResourceOwnership.Owned) continue;
                state.Resources[value.ResourceId] = resource.Transition(TransferStatus(resource.Status,
                    instruction.Kind == InstructionKind.Escape ? ResourceStatus.Escaped : ResourceStatus.Transferred, !value.CoversResource),
                    "factory-result-transfer", "Responsibility for the received result leaves with its factory carrier.", instruction.Site);
            }
            // A local carrier Move invalidates existing payload aliases, but its destination keeps
            // the original cached result. An already stale cached result is never made available.
            if (localMove) state.Assign(cache, cached, state.CanBeNull(cache));
        }
    }

    private static void ReceiveAsyncResult(AbstractState state, FlowInstruction instruction, ReferenceFact[] references,
        int blockId, int index, ISet<string> liveAfter, IssueCollector? issues)
    {
        CheckMoved(state, instruction, references, issues);
        var received = new List<ReferenceFact>();
        var external = references.Length == 0;
        var mayBeNull = false;
        foreach (var reference in references)
        {
            if (reference.Projection.Length != 0 || !state.Resources.TryGetValue(reference.ResourceId, out var carrier) ||
                carrier.Kind != ResourceKind.AsyncFactoryCarrier)
            {
                // Neither an opaque Task nor a loan of that Task proves a payload alias.
                // A declared borrowed output uses BindAsyncResult to name its actual source.
                external = true;
                continue;
            }
            var cache = AsyncResultCache(carrier.Id);
            var previous = state.Resolve(cache).ToArray();
            var materialized = (carrier.ResultState & AsyncResultState.Materialized) != 0;
            var values = new List<ReferenceFact>();
            var cacheMayBeNull = false;
            if (materialized) values.AddRange(previous);
            if ((carrier.ResultState & AsyncResultState.Deferred) != 0)
            {
                var id = "async-result:" + carrier.Id;
                var nullable = (carrier.Flags & InstructionFlags.MayBeNull) != 0;
                var absent = nullable || reference.MayBeAbsent || (carrier.Status & ResourceStatus.Absent) != 0;
                var produced = new AnalysisEvidence("factory-result", "The owned result becomes available only after this successful completion.", instruction.Site);
                var resource = new ResourceFact(id, ResourceOwnership.Owned, ResourceStatus.Alive | (absent ? ResourceStatus.Absent : ResourceStatus.None),
                    instruction.Site, carrier.Flags, ImmutableHashSet.Create<string>(StringComparer.Ordinal),
                    carrier.Evidence.SetItem(EvidenceFacts.Key(produced), produced));
                if (state.Resources.TryGetValue(id, out var old))
                {
                    var retained = materialized || liveAfter.Where(place => place != instruction.Target && place != cache)
                        .SelectMany(state.Resolve).Any(value => value.ResourceId == id && !value.DefinitelyMoved);
                    if (retained) resource = resource.Join(old);
                    else if (old.HasOutstandingObligation && (old.Flags & InstructionFlags.SuppressLeak) == 0)
                        issues?.Add("SS0011", "Receiving a new result from this repeated factory site abandons an undisposed earlier result.", instruction.Site,
                            Certainty(old.Status == ResourceStatus.Alive, instruction), old.Id, Evidence(old));
                }
                state.Resources[id] = resource;
                values.Add(new ReferenceFact(id, mayBeAbsent: absent, coversResource: reference.CoversResource));
                cacheMayBeNull |= nullable;
            }
            if (materialized)
            {
                // The cached reference participates in ordinary Move invalidation; a later await
                // cannot create fresh ownership after an earlier result was consumed or moved.
                CheckMoved(state, instruction, previous, issues);
                cacheMayBeNull |= state.CanBeNull(cache);
            }
            state.Assign(cache, values, cacheMayBeNull);
            mayBeNull |= cacheMayBeNull;
            received.AddRange(state.Resolve(cache));
            state.Resources[carrier.Id] = carrier.Transition(TransferStatus(carrier.Status, ResourceStatus.Completed, !reference.CoversResource),
                "factory-result", "The factory result is received through this carrier alias.", instruction.Site)
                .WithResultState(reference.CoversResource ? AsyncResultState.Materialized : carrier.ResultState | AsyncResultState.Materialized);
        }
        if (external)
        {
            var id = instruction.ResourceId ?? "external-async-result:" + blockId + ":" + index;
            var nullable = (instruction.Flags & InstructionFlags.MayBeNull) != 0;
            var resource = new ResourceFact(id, ResourceOwnership.External, ResourceStatus.Alive | (nullable ? ResourceStatus.Absent : ResourceStatus.None),
                instruction.Site, instruction.Flags, ImmutableHashSet.Create<string>(StringComparer.Ordinal));
            if (state.Resources.TryGetValue(id, out var previous)) resource = resource.Join(previous);
            state.Resources[id] = resource;
            received.Add(new ReferenceFact(id, mayBeAbsent: nullable || received.Count != 0));
            mayBeNull |= nullable;
        }
        CheckOverwrite(state, instruction, instruction.Target!, received, issues, liveAfter);
        state.Assign(instruction.Target!, received, mayBeNull);
    }

    private static void Dispose(AbstractState state, FlowInstruction instruction, ReferenceFact[] references, IssueCollector? issues)
    {
        CheckMoved(state, instruction, references, issues);
        CheckDisposed(state, instruction, references, "SS0002", "Resource is disposed more than once.", issues);
        CheckScope(state, instruction, references, issues);
        CheckBorrowedDisposal(state, instruction, references, issues);
        CheckLoans(state, instruction, references, "SS0014", "The owner cannot be disposed while a loan remains live.", _ => true, false, issues);
        foreach (var reference in references)
            if (reference.Projection.Length == 0 && state.Resources.TryGetValue(reference.ResourceId, out var resource) &&
                resource.Kind == ResourceKind.Resource)
                state.Resources[reference.ResourceId] = resource.Transition(TransferStatus(resource.Status, ResourceStatus.Disposed,
                    !reference.CoversResource), "dispose", "Resource disposal occurs here.", instruction.Site);
        // Disposing an awaitable object is not equivalent to awaiting its asynchronous cleanup operation.
    }

    private static void TrackAllocation(AbstractState state, FlowInstruction instruction, ResourceFact resource, bool mayBeNull,
        ISet<string> liveAfter, IssueCollector? issues)
    {
        var id = resource.Id;
        CheckOverwrite(state, instruction, instruction.Target!, Array.Empty<ReferenceFact>(), issues, liveAfter);
        // Allocation-site abstraction must not reactivate a disposed instance still reachable through an older alias.
        // Strong updates are safe only when this place is the only local representative of the site.
        if (state.Resources.TryGetValue(id, out var previous))
        {
            if (previous.Kind != resource.Kind) throw new ArgumentException("A resource identity cannot change its resource kind.", nameof(instruction));
            var retained = liveAfter.Where(place => place != instruction.Target && !PlaceRelations.IsStrictDescendant(place, instruction.Target!))
                .SelectMany(state.Resolve).Any(reference => reference.ResourceId == id && (reference.Availability & ReferenceAvailability.Available) != 0);
            if (previous.Kind == ResourceKind.AsyncDisposalCompletion && previous.HasOutstandingObligation)
                issues?.Add("SS0017", retained
                    ? "A completion from an earlier execution of this disposal site may still be pending."
                    : "A repeated asynchronous disposal abandons an unobserved completion from an earlier iteration.", instruction.Site,
                    Certainty(!retained && previous.Status == ResourceStatus.Pending, instruction), previous.Id, Evidence(previous));
            if (previous.Kind == ResourceKind.AsyncFactoryCarrier && previous.HasOutstandingObligation &&
                (previous.Flags & InstructionFlags.SuppressLeak) == 0)
                issues?.Add("SS0011", "A repeated asynchronous factory call may abandon an owned result before the previous carrier is received or transferred.",
                    instruction.Site, IssueCertainty.Possible, previous.Id, Evidence(previous));
            if (retained) resource = resource.Join(previous);
            else if (issues is not null && previous.Ownership == ResourceOwnership.Owned && (previous.Status & ResourceStatus.Alive) != 0
                && (previous.Flags & InstructionFlags.SuppressLeak) == 0)
                issues.Add("SS0011", "A repeated allocation abandons an undisposed instance from an earlier iteration.", instruction.Site,
                    previous.Status == ResourceStatus.Alive ? IssueCertainty.Definite : IssueCertainty.Possible, previous.Id, Evidence(previous));
        }
        state.Resources[id] = resource;
        state.Assign(instruction.Target!, new[] { new ReferenceFact(id, mayBeAbsent: mayBeNull) }, mayBeNull);
    }

    private static void Borrow(AbstractState state, FlowInstruction instruction, ReferenceFact[] references,
        int blockId, int index, ISet<string> liveAfter, IssueCollector? issues)
    {
        CheckUse(state, instruction, references, issues);
        var kind = instruction.Kind == InstructionKind.BorrowShared ? BorrowKind.Shared : BorrowKind.Exclusive;
        var isReservation = instruction.Kind == InstructionKind.ReserveExclusiveBorrow;
        CheckLoans(state, instruction, references, "SS0006", isReservation
            ? "An exclusive reservation conflicts with another exclusive loan or reservation."
            : kind == BorrowKind.Exclusive ? "An exclusive loan conflicts with another live loan."
                : "A shared loan conflicts with a live active exclusive loan.",
            loan => isReservation ? loan.Kind == BorrowKind.Exclusive : kind == BorrowKind.Exclusive || IsPossiblyActiveExclusive(loan),
            true, issues, definitelyConflicts: loan => isReservation ? loan.Kind == BorrowKind.Exclusive
                : kind == BorrowKind.Exclusive || IsDefinitelyActiveExclusive(loan));
        if (kind == BorrowKind.Exclusive)
        {
            var sharedParents = references.Where(reference => reference.LoanId is not null
                && state.Loans.TryGetValue(reference.LoanId, out var parent) &&
                    (parent.Kind == BorrowKind.Shared || (parent.Phase & BorrowPhase.Reserved) != 0)).ToArray();
            if (sharedParents.Length != 0 && issues is not null)
                issues.Add("SS0006", "An exclusive loan cannot be derived from a shared loan or an unactivated reservation.", instruction.Site,
                    Certainty(sharedParents.Length == references.Length && sharedParents.All(reference => !reference.MayBeAbsent &&
                        (state.Loans[reference.LoanId!].Kind == BorrowKind.Shared || state.Loans[reference.LoanId!].Phase == BorrowPhase.Reserved)), instruction),
                    sharedParents[0].ResourceId, FactsFor(state, sharedParents));
        }
        CheckOverwrite(state, instruction, instruction.Target!, references, issues, liveAfter);
        var borrowed = new List<ReferenceFact>();
        foreach (var reference in references)
        {
            var id = "loan:" + blockId + ":" + index + ":" + reference.ResourceId + ":" + reference.Projection;
            var parents = reference.LoanId is null ? ImmutableHashSet.Create<string>(StringComparer.Ordinal)
                : ImmutableHashSet.Create(StringComparer.Ordinal, reference.LoanId);
            var loan = new LoanFact(id, reference.ResourceId, instruction.Target!, kind, instruction.Site, reference.Projection,
                (instruction.Flags & InstructionFlags.AllowsSuspension) != 0, parents, references.Length > 1 || reference.MayBeAbsent,
                isReservation ? BorrowPhase.Reserved : BorrowPhase.Active);
            if (state.Loans.TryGetValue(id, out var previous)) loan = previous.Join(loan);
            state.Loans[id] = loan;
            borrowed.Add(new ReferenceFact(reference.ResourceId, reference.Availability, id, reference.Projection,
                reference.MayBeAbsent, reference.MovedPaths, reference.DefiniteMovedPaths, reference.Evidence, reference.CoversResource));
        }
        state.Assign(instruction.Target!, borrowed, state.CanBeNull(instruction.Source));
    }

    private static void ActivateBorrow(AbstractState state, FlowInstruction instruction, ReferenceFact[] references, IssueCollector? issues)
    {
        // SafeSharp retains possible phases in its CFG lattice instead of rustc's dominance-based activation query.
        // https://rustc-dev-guide.rust-lang.org/borrow-check/two-phase-borrows.html
        // Pinned implementation reference: https://github.com/rust-lang/rust/blob/f8297e351a40c1439a467bbbb6879088047f50b3/compiler/rustc_borrowck/src/borrow_set.rs
        CheckUse(state, instruction, references, issues);
        CheckLoans(state, instruction, references, "SS0006", "An exclusive reservation cannot activate while a conflicting loan remains live.",
            _ => true, true, issues);
        foreach (var reference in references)
        {
            if (reference.LoanId is null || !state.Loans.TryGetValue(reference.LoanId, out var loan) || loan.Kind != BorrowKind.Exclusive)
            {
                issues?.Add("SS0006", "Only an exclusive loan reservation can be activated.", instruction.Site,
                    Certainty(references.Length == 1 && !reference.MayBeAbsent, instruction), reference.ResourceId, FactsFor(state, new[] { reference }));
                continue;
            }
            state.Loans[loan.Id] = loan.Activate(instruction.Site, !reference.CoversResource);
        }
    }

    private static bool IsPossiblyActiveExclusive(LoanFact loan) => loan.Kind == BorrowKind.Exclusive && (loan.Phase & BorrowPhase.Active) != 0;
    private static bool IsDefinitelyActiveExclusive(LoanFact loan) => loan.Kind == BorrowKind.Exclusive && loan.Phase == BorrowPhase.Active;

    private static void InvalidateAliases(AbstractState state, ReferenceFact moved, SourceSite site, bool conditional)
    {
        foreach (var value in state.Places.Values)
        foreach (var pair in value.ToArray())
            if (pair.Value.ResourceId == moved.ResourceId) value[pair.Key] = pair.Value.Invalidate(moved.Projection, site, conditional);
    }

    private static ResourceStatus TransferStatus(ResourceStatus before, ResourceStatus replacement, bool weak)
    {
        // Keep the explicit absent alternative; a conditional allocation is not definite on every path.
        var after = replacement | (before & ResourceStatus.Absent);
        return weak ? before | after : after;
    }

    private static void CheckUse(AbstractState state, FlowInstruction instruction, ReferenceFact[] references, IssueCollector? issues)
    {
        if (issues is null) return;
        CheckMoved(state, instruction, references, issues);
        CheckDisposed(state, instruction, references, "SS0001", "Resource is used after disposal.", issues);
        CheckScope(state, instruction, references, issues);
    }

    private static void CheckMoved(AbstractState state, FlowInstruction instruction, ReferenceFact[] references, IssueCollector? issues)
    {
        if (issues is null || references.Length == 0) return;
        var invalid = references.Where(reference => reference.MayBeMoved).ToArray();
        if (invalid.Length == 0) return;
        issues.Add("SS0004", "The value is used after ownership was moved from this alias or one of its fields.", instruction.Site,
            Certainty(invalid.Length == references.Length && invalid.All(reference => reference.DefinitelyMoved && !reference.MayBeAbsent), instruction),
            invalid[0].ResourceId, FactsFor(state, invalid));
    }

    private static void CheckDisposed(AbstractState state, FlowInstruction instruction, ReferenceFact[] references,
        string id, string message, IssueCollector? issues)
    {
        if (issues is null || references.Length == 0) return;
        var invalid = references.Where(reference => state.Resources.TryGetValue(reference.ResourceId, out var resource)
            && (resource.Status & ResourceStatus.Disposed) != 0).ToArray();
        if (invalid.Length == 0) return;
        var definite = invalid.Length == references.Length && invalid.All(reference => !reference.MayBeAbsent
            && state.Resources[reference.ResourceId].Status == ResourceStatus.Disposed);
        issues.Add(id, message, instruction.Site, Certainty(definite, instruction), invalid[0].ResourceId, FactsFor(state, invalid));
    }

    private static void CheckScope(AbstractState state, FlowInstruction instruction, ReferenceFact[] references, IssueCollector? issues)
    {
        if (issues is null || references.Length == 0) return;
        var invalid = new List<ReferenceFact>();
        var owners = new List<ResourceFact>();
        var definite = true;
        foreach (var reference in references)
        {
            var invalidOwners = InvalidOwners(state, reference.ResourceId, new HashSet<string>(StringComparer.Ordinal)).ToArray();
            if (invalidOwners.Length == 0) { definite = false; continue; }
            invalid.Add(reference);
            owners.AddRange(invalidOwners);
            definite &= !reference.MayBeAbsent && invalidOwners.All(owner => owner.Status == ResourceStatus.Disposed);
        }
        if (invalid.Count == 0) return;
        issues.Add("SS0012", "The scoped resource is used after its owning scope was disposed.", instruction.Site,
            Certainty(definite && invalid.Count == references.Length, instruction), invalid[0].ResourceId,
            FactsFor(state, invalid).Concat(owners.SelectMany(Evidence)));
    }

    private static IEnumerable<ResourceFact> InvalidOwners(AbstractState state, string resourceId, HashSet<string> visited)
    {
        if (!visited.Add(resourceId) || !state.Resources.TryGetValue(resourceId, out var resource)) yield break;
        foreach (var ownerId in resource.OwnerIds)
        {
            if (state.Resources.TryGetValue(ownerId, out var owner) && (owner.Status & ResourceStatus.Disposed) != 0) yield return owner;
            foreach (var ancestor in InvalidOwners(state, ownerId, visited)) yield return ancestor;
        }
    }

    private static void CheckBorrowedDisposal(AbstractState state, FlowInstruction instruction, ReferenceFact[] references, IssueCollector? issues)
    {
        if (issues is null || references.Length == 0) return;
        var invalid = references.Where(reference => reference.LoanId is not null || state.Resources.TryGetValue(reference.ResourceId, out var resource)
            && (resource.Ownership == ResourceOwnership.Borrowed || resource.Ownership == ResourceOwnership.Scoped)).ToArray();
        if (invalid.Length == 0) return;
        issues.Add("SS0009", "A borrowed or container-owned resource must be disposed by its owner.", instruction.Site,
            Certainty(invalid.Length == references.Length && invalid.All(reference => !reference.MayBeAbsent), instruction), invalid[0].ResourceId, FactsFor(state, invalid));
    }

    private static void CheckBorrowedEscape(AbstractState state, FlowInstruction instruction, ReferenceFact[] references, IssueCollector? issues)
    {
        if (issues is null || references.Length == 0) return;
        var borrowed = references.Where(reference => reference.LoanId is not null || state.Resources.TryGetValue(reference.ResourceId, out var resource)
            && resource.Ownership == ResourceOwnership.Borrowed).ToArray();
        if (borrowed.Length != 0)
            issues.Add("SS0008", "A borrowed value escapes or transfers ownership beyond its declared lifetime.", instruction.Site,
                Certainty(borrowed.Length == references.Length && borrowed.All(reference => !reference.MayBeAbsent), instruction), borrowed[0].ResourceId, FactsFor(state, borrowed));
        var scoped = references.Where(reference => state.Resources.TryGetValue(reference.ResourceId, out var resource)
            && resource.Ownership == ResourceOwnership.Scoped && resource.OwnerIds.Count != 0).ToArray();
        if (scoped.Length != 0)
            issues.Add("SS0012", "A scope-owned service escapes the lifetime of its owning scope.", instruction.Site,
                Certainty(scoped.Length == references.Length && scoped.All(reference => !reference.MayBeAbsent), instruction), scoped[0].ResourceId, FactsFor(state, scoped));
    }

    private static void CheckOverwrite(AbstractState state, FlowInstruction instruction, string target,
        IReadOnlyCollection<ReferenceFact> incoming, IssueCollector? issues, ISet<string>? liveAfter = null)
    {
        if (issues is null) return;
        var old = state.Resolve(target);
        foreach (var reference in old)
        {
            if (reference.LoanId is not null || reference.DefinitelyMoved
                || !state.Resources.TryGetValue(reference.ResourceId, out var resource) || resource.Ownership != ResourceOwnership.Owned
                || (resource.Flags & InstructionFlags.SuppressLeak) != 0 || !resource.HasOutstandingObligation) continue;
            var completion = resource.Kind == ResourceKind.AsyncDisposalCompletion;
            var factory = resource.Kind == ResourceKind.AsyncFactoryCarrier;
            var carried = incoming.Where(value => value.ResourceId == resource.Id).ToArray();
            if (carried.Any(value => !(completion || factory) || value.CoversResource && !value.MayBeMoved && value.Projection.Length == 0)) continue;
            // Call snapshots represent values on the frontend's evaluation stack. An exceptional
            // edge can abandon that stack without executing its normal ReleaseLoan instructions.
            // A dead snapshot is not a surviving handle; live snapshots and ordinary aliases are.
            var otherReferences = (completion || factory) && liveAfter is not null
                ? liveAfter.Where(place => place != target && !PlaceRelations.IsStrictDescendant(place, target)).SelectMany(state.Resolve)
                : state.Places.Where(pair => pair.Key != target && !PlaceRelations.IsStrictDescendant(pair.Key, target) &&
                    IsUsableResultCache(state, pair.Key, liveAfter) &&
                    (!pair.Key.StartsWith("$t:call-", StringComparison.Ordinal) || liveAfter is null ||
                     liveAfter.Any(live => live == pair.Key || PlaceRelations.IsStrictDescendant(live, pair.Key))))
                    .SelectMany(pair => pair.Value.Values);
            var retained = otherReferences.Where(value => value.ResourceId == resource.Id && value.LoanId is null && !value.DefinitelyMoved
                    && (value.Availability & ReferenceAvailability.Available) != 0).ToArray();
            if (retained.Any(value => value.CoversResource && !value.MayBeMoved && value.Projection.Length == 0)) continue;
            issues.Add(completion ? "SS0017" : "SS0011", completion
                    ? "Overwriting this place loses an asynchronous disposal completion before it is observed or transferred."
                    : factory ? "Overwriting this place may abandon an owned factory result before its carrier is received or transferred."
                    : "Overwriting this place loses the last known owner of an undisposed resource.", instruction.Site,
                factory ? IssueCertainty.Possible : Certainty(resource.Status == resource.OutstandingStatus && !reference.MayBeAbsent && old.Count == 1 && retained.Length == 0 && carried.Length == 0, instruction),
                resource.Id, Evidence(resource).Concat(reference.Evidence.Values));
        }
    }

    private static bool IsUsableResultCache(AbstractState state, string place, ISet<string>? liveAfter)
    {
        if (!place.StartsWith(AsyncResultCachePrefix, StringComparison.Ordinal)) return true;
        if (liveAfter is null) return false;
        var carrier = place.Substring(AsyncResultCachePrefix.Length);
        return liveAfter.SelectMany(state.Resolve).Any(reference => reference.ResourceId == carrier && !reference.DefinitelyMoved);
    }

    private static void CheckLoans(AbstractState state, FlowInstruction instruction, ReferenceFact[] references,
        string id, string message, Func<LoanFact, bool> conflicts, bool allowAncestors, IssueCollector? issues, bool allowOwnExclusive = false,
        Func<LoanFact, bool>? definitelyConflicts = null)
    {
        if (issues is null || references.Length == 0 || state.Loans.Count == 0) return;
        var invalid = new List<ReferenceFact>();
        var conflictingLoans = new List<LoanFact>();
        var definite = true;
        foreach (var reference in references)
        {
            var ancestors = allowAncestors ? LoanAncestors(state, reference.LoanId) : new HashSet<string>(StringComparer.Ordinal);
            var loans = state.Loans.Values.Where(loan => loan.ResourceId == reference.ResourceId && PlaceRelations.Overlaps(loan.Projection, reference.Projection)
                && conflicts(loan) && (!ancestors.Contains(loan.Id) || allowOwnExclusive &&
                    (loan.Kind == BorrowKind.Shared || (loan.Phase & BorrowPhase.Reserved) != 0))).ToArray();
            if (loans.Length == 0) { definite = false; continue; }
            invalid.Add(reference);
            conflictingLoans.AddRange(loans);
            definite &= !reference.MayBeAbsent && loans.Any(loan => !loan.MayBeAbsent && (definitelyConflicts?.Invoke(loan) ?? true) &&
                (!allowOwnExclusive || !ancestors.Contains(loan.Id) || loan.Kind == BorrowKind.Shared || loan.Phase == BorrowPhase.Reserved));
        }
        if (invalid.Count == 0) return;
        issues.Add(id, message, instruction.Site, Certainty(definite && invalid.Count == references.Length, instruction), invalid[0].ResourceId,
            FactsFor(state, invalid).Concat(conflictingLoans.SelectMany(LoanFacts)));
    }

    private static HashSet<string> LoanAncestors(AbstractState state, string? loanId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (loanId is null) return result;
        var queue = new Queue<string>();
        queue.Enqueue(loanId);
        while (queue.Count != 0)
        {
            var id = queue.Dequeue();
            if (!result.Add(id) || !state.Loans.TryGetValue(id, out var loan)) continue;
            foreach (var parent in loan.ParentIds) queue.Enqueue(parent);
        }
        return result;
    }

    private static void CheckSuspension(AbstractState state, FlowInstruction instruction, ISet<string> liveAfter, IssueCollector? issues)
    {
        if (issues is null) return;
        var directReferences = liveAfter.SelectMany(state.Resolve).ToArray();
        List<ReferenceFact>? cachedReferences = null;
        foreach (var reference in directReferences)
        {
            if (reference.DefinitelyMoved || !state.Resources.TryGetValue(reference.ResourceId, out var resource) ||
                resource.Kind != ResourceKind.AsyncFactoryCarrier || (resource.ResultState & AsyncResultState.Materialized) == 0) continue;
            foreach (var cached in state.Resolve(AsyncResultCache(reference.ResourceId)))
                (cachedReferences ??= new List<ReferenceFact>(directReferences)).Add(cached);
        }
        IReadOnlyCollection<ReferenceFact> liveReferences = cachedReferences is not null ? cachedReferences : directReferences;
        foreach (var loan in state.Loans.Values)
        {
            if (loan.AllowsSuspension || !liveReferences.Any(reference => reference.LoanId is not null && LoanAncestors(state, reference.LoanId).Contains(loan.Id))) continue;
            var evidence = LoanFacts(loan).Concat(new[] { new AnalysisEvidence("suspension", "This loan is live after an await or yield boundary.", instruction.Site) });
            issues.Add("SS0010", "A loan remains live across a suspension point; shorten its lifetime or declare an explicit suspension-safe contract.",
                instruction.Site, Certainty(!loan.MayBeAbsent, instruction), loan.ResourceId, evidence);
        }
        foreach (var resource in state.Resources.Values)
        {
            if (resource.Kind != ResourceKind.Resource || (resource.Flags & InstructionFlags.NoSuspend) == 0 || (resource.Status & (ResourceStatus.Alive | ResourceStatus.Escaped)) == 0
                || !liveReferences.Any(reference => reference.ResourceId == resource.Id && !reference.DefinitelyMoved)) continue;
            issues.Add("SS0016", "This resource's no-suspension contract is violated by a live value across await or yield.", instruction.Site,
                Certainty(resource.Status == ResourceStatus.Alive, instruction), resource.Id, Evidence(resource).Concat(new[] {
                    new AnalysisEvidence("suspension", "The resource is live after this suspension point.", instruction.Site) }));
        }
    }

    private static IssueCertainty Certainty(bool definite, FlowInstruction instruction) => definite && (instruction.Flags & InstructionFlags.Conditional) == 0
        && instruction.Kind != InstructionKind.MergeAlias
        ? IssueCertainty.Definite : IssueCertainty.Possible;

    internal static IEnumerable<AnalysisEvidence> Evidence(ResourceFact resource)
    {
        yield return new AnalysisEvidence("origin", resource.Kind == ResourceKind.AsyncDisposalCompletion
            ? "The asynchronous disposal completion originates here." : resource.Kind == ResourceKind.AsyncFactoryCarrier
                ? "The carrier of a potentially owned asynchronous result originates here." : resource.Ownership + " resource originates here.", resource.Origin);
        foreach (var item in resource.Evidence.Values) yield return item;
    }

    private static IEnumerable<AnalysisEvidence> LoanFacts(LoanFact loan)
    {
        yield return new AnalysisEvidence("loan", loan.Kind + " loan originates here; its possible current phases are " + loan.Phase + ".", loan.Origin);
        foreach (var evidence in loan.ActivationEvidence.Values) yield return evidence;
    }

    private static IEnumerable<AnalysisEvidence> FactsFor(AbstractState state, IEnumerable<ReferenceFact> references)
    {
        foreach (var reference in references)
        {
            if (state.Resources.TryGetValue(reference.ResourceId, out var resource))
            {
                foreach (var item in Evidence(resource)) yield return item;
                foreach (var owner in resource.OwnerIds)
                    if (state.Resources.TryGetValue(owner, out var ownerResource))
                        foreach (var item in Evidence(ownerResource)) yield return item;
            }
            foreach (var item in reference.Evidence.Values) yield return item;
            if (reference.LoanId is not null && state.Loans.TryGetValue(reference.LoanId, out var loan))
                foreach (var evidence in LoanFacts(loan)) yield return evidence;
        }
    }
}
