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
                CheckOverwrite(state, instruction, instruction.Target!, references, issues);
                state.Assign(instruction.Target!, references, sourceMayBeNull);
                break;
            case InstructionKind.Move:
                CheckUse(state, instruction, references, issues);
                CheckLoans(state, instruction, references, "SS0005", "Ownership cannot move while a loan remains live.", _ => true, false, issues);
                CheckBorrowedEscape(state, instruction, references, issues);
                if (instruction.Target is not null) CheckOverwrite(state, instruction, instruction.Target, references, issues);
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
                Borrow(state, instruction, references, blockId, instructionIndex, issues);
                break;
            case InstructionKind.Read:
                CheckUse(state, instruction, references, issues);
                CheckLoans(state, instruction, references, "SS0013", "The owner cannot be read while an exclusive loan remains live.",
                    loan => loan.Kind == BorrowKind.Exclusive, true, issues);
                break;
            case InstructionKind.Write:
                CheckUse(state, instruction, references, issues);
                CheckLoans(state, instruction, references, "SS0007", "Mutation conflicts with a live shared or exclusive loan.", _ => true, true, issues, allowOwnExclusive: true);
                break;
            case InstructionKind.Dispose:
                CheckMoved(state, instruction, references, issues);
                CheckDisposed(state, instruction, references, "SS0002", "Resource is disposed more than once.", issues);
                CheckScope(state, instruction, references, issues);
                CheckBorrowedDisposal(state, instruction, references, issues);
                CheckLoans(state, instruction, references, "SS0014", "The owner cannot be disposed while a loan remains live.", _ => true, false, issues);
                foreach (var reference in references)
                    if (reference.Projection.Length == 0 && state.Resources.TryGetValue(reference.ResourceId, out var resource))
                        state.Resources[reference.ResourceId] = resource.Transition(TransferStatus(resource.Status, ResourceStatus.Disposed,
                            !reference.CoversResource), "dispose", "Resource disposal occurs here.", instruction.Site);
                break;
            case InstructionKind.Escape:
            case InstructionKind.Return:
                CheckUse(state, instruction, references, issues);
                CheckBorrowedEscape(state, instruction, references, issues);
                CheckLoans(state, instruction, references, "SS0005", "Ownership cannot escape while a loan remains live.", _ => true, false, issues);
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
                    CheckOverwrite(state, instruction, instruction.Target, Array.Empty<ReferenceFact>(), issues);
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
        CheckOverwrite(state, instruction, instruction.Target!, Array.Empty<ReferenceFact>(), issues);
        var owners = state.Resolve(instruction.Owner).Select(reference => reference.ResourceId).ToImmutableHashSet(StringComparer.Ordinal);
        var mayBeNull = (instruction.Flags & InstructionFlags.MayBeNull) != 0;
        var resource = new ResourceFact(id, instruction.Ownership, ResourceStatus.Alive | (mayBeNull ? ResourceStatus.Absent : ResourceStatus.None), instruction.Site, instruction.Flags, owners);
        // Allocation-site abstraction must not reactivate a disposed instance still reachable through an older alias.
        // Strong updates are safe only when this place is the only local representative of the site.
        if (state.Resources.TryGetValue(id, out var previous))
        {
            var retained = liveAfter.Where(place => place != instruction.Target && !PlaceRelations.IsStrictDescendant(place, instruction.Target!))
                .SelectMany(state.Resolve).Any(reference => reference.ResourceId == id && (reference.Availability & ReferenceAvailability.Available) != 0);
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
        int blockId, int index, IssueCollector? issues)
    {
        CheckUse(state, instruction, references, issues);
        var kind = instruction.Kind == InstructionKind.BorrowShared ? BorrowKind.Shared : BorrowKind.Exclusive;
        CheckLoans(state, instruction, references, "SS0006", kind == BorrowKind.Exclusive
            ? "An exclusive loan conflicts with another live loan."
            : "A shared loan conflicts with a live exclusive loan.", loan => kind == BorrowKind.Exclusive || loan.Kind == BorrowKind.Exclusive, true, issues);
        if (kind == BorrowKind.Exclusive)
        {
            var sharedParents = references.Where(reference => reference.LoanId is not null
                && state.Loans.TryGetValue(reference.LoanId, out var parent) && parent.Kind == BorrowKind.Shared).ToArray();
            if (sharedParents.Length != 0 && issues is not null)
                issues.Add("SS0006", "An exclusive loan cannot be derived from a shared loan.", instruction.Site,
                    Certainty(sharedParents.Length == references.Length && sharedParents.All(reference => !reference.MayBeAbsent), instruction),
                    sharedParents[0].ResourceId, FactsFor(state, sharedParents));
        }
        CheckOverwrite(state, instruction, instruction.Target!, references, issues);
        var borrowed = new List<ReferenceFact>();
        foreach (var reference in references)
        {
            var id = "loan:" + blockId + ":" + index + ":" + reference.ResourceId + ":" + reference.Projection;
            var parents = reference.LoanId is null ? ImmutableHashSet.Create<string>(StringComparer.Ordinal)
                : ImmutableHashSet.Create(StringComparer.Ordinal, reference.LoanId);
            var loan = new LoanFact(id, reference.ResourceId, instruction.Target!, kind, instruction.Site, reference.Projection,
                (instruction.Flags & InstructionFlags.AllowsSuspension) != 0, parents, references.Length > 1 || reference.MayBeAbsent);
            if (state.Loans.TryGetValue(id, out var previous)) loan = previous.Join(loan);
            state.Loans[id] = loan;
            borrowed.Add(new ReferenceFact(reference.ResourceId, reference.Availability, id, reference.Projection,
                reference.MayBeAbsent, reference.MovedPaths, reference.DefiniteMovedPaths, reference.Evidence, reference.CoversResource));
        }
        state.Assign(instruction.Target!, borrowed, state.CanBeNull(instruction.Source));
    }

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
        IReadOnlyCollection<ReferenceFact> incoming, IssueCollector? issues)
    {
        if (issues is null) return;
        var old = state.Resolve(target);
        foreach (var reference in old)
        {
            if (reference.LoanId is not null || reference.DefinitelyMoved || incoming.Any(value => value.ResourceId == reference.ResourceId)
                || !state.Resources.TryGetValue(reference.ResourceId, out var resource) || resource.Ownership != ResourceOwnership.Owned
                || (resource.Flags & InstructionFlags.SuppressLeak) != 0 || (resource.Status & ResourceStatus.Alive) == 0) continue;
            var retained = state.Places.Where(pair => pair.Key != target && !PlaceRelations.IsStrictDescendant(pair.Key, target))
                .SelectMany(pair => pair.Value.Values).Where(value => value.ResourceId == resource.Id && value.LoanId is null && !value.DefinitelyMoved
                    && (value.Availability & ReferenceAvailability.Available) != 0).ToArray();
            if (retained.Any(value => value.CoversResource && !value.MayBeMoved && value.Projection.Length == 0)) continue;
            issues.Add("SS0011", "Overwriting this place loses the last known owner of an undisposed resource.", instruction.Site,
                Certainty(resource.Status == ResourceStatus.Alive && !reference.MayBeAbsent && old.Count == 1 && retained.Length == 0, instruction),
                resource.Id, Evidence(resource).Concat(reference.Evidence.Values));
        }
    }

    private static void CheckLoans(AbstractState state, FlowInstruction instruction, ReferenceFact[] references,
        string id, string message, Func<LoanFact, bool> conflicts, bool allowAncestors, IssueCollector? issues, bool allowOwnExclusive = false)
    {
        if (issues is null || references.Length == 0 || state.Loans.Count == 0) return;
        var invalid = new List<ReferenceFact>();
        var conflictingLoans = new List<LoanFact>();
        var definite = true;
        foreach (var reference in references)
        {
            var ancestors = allowAncestors ? LoanAncestors(state, reference.LoanId) : new HashSet<string>(StringComparer.Ordinal);
            var loans = state.Loans.Values.Where(loan => loan.ResourceId == reference.ResourceId && PlaceRelations.Overlaps(loan.Projection, reference.Projection)
                && conflicts(loan) && (!ancestors.Contains(loan.Id) || allowOwnExclusive && loan.Id == reference.LoanId && loan.Kind == BorrowKind.Shared)).ToArray();
            if (loans.Length == 0) { definite = false; continue; }
            invalid.Add(reference);
            conflictingLoans.AddRange(loans);
            definite &= !reference.MayBeAbsent && loans.Any(loan => !loan.MayBeAbsent);
        }
        if (invalid.Count == 0) return;
        issues.Add(id, message, instruction.Site, Certainty(definite && invalid.Count == references.Length, instruction), invalid[0].ResourceId,
            FactsFor(state, invalid).Concat(conflictingLoans.Select(LoanEvidence)));
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
        var liveReferences = liveAfter.SelectMany(state.Resolve).ToArray();
        foreach (var loan in state.Loans.Values)
        {
            if (loan.AllowsSuspension || !liveReferences.Any(reference => reference.LoanId is not null && LoanAncestors(state, reference.LoanId).Contains(loan.Id))) continue;
            var evidence = new[] { LoanEvidence(loan), new AnalysisEvidence("suspension", "This loan is live after an await or yield boundary.", instruction.Site) };
            issues.Add("SS0010", "A loan remains live across a suspension point; shorten its lifetime or declare an explicit suspension-safe contract.",
                instruction.Site, Certainty(!loan.MayBeAbsent, instruction), loan.ResourceId, evidence);
        }
        foreach (var resource in state.Resources.Values)
        {
            if ((resource.Flags & InstructionFlags.NoSuspend) == 0 || (resource.Status & (ResourceStatus.Alive | ResourceStatus.Escaped)) == 0
                || !liveReferences.Any(reference => reference.ResourceId == resource.Id && !reference.DefinitelyMoved)) continue;
            issues.Add("SS0016", "This resource's no-suspension contract is violated by a live value across await or yield.", instruction.Site,
                Certainty(resource.Status == ResourceStatus.Alive, instruction), resource.Id, Evidence(resource).Concat(new[] {
                    new AnalysisEvidence("suspension", "The resource is live after this suspension point.", instruction.Site) }));
        }
    }

    private static IssueCertainty Certainty(bool definite, FlowInstruction instruction) => definite && (instruction.Flags & InstructionFlags.Conditional) == 0
        ? IssueCertainty.Definite : IssueCertainty.Possible;

    internal static IEnumerable<AnalysisEvidence> Evidence(ResourceFact resource)
    {
        yield return new AnalysisEvidence("origin", resource.Ownership + " resource originates here.", resource.Origin);
        foreach (var item in resource.Evidence.Values) yield return item;
    }

    private static AnalysisEvidence LoanEvidence(LoanFact loan) => new("loan", loan.Kind + " loan is created here and is still live.", loan.Origin);

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
            if (reference.LoanId is not null && state.Loans.TryGetValue(reference.LoanId, out var loan)) yield return LoanEvidence(loan);
        }
    }
}
