using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using SafeSharp.Analyzers.Analysis;

namespace SafeSharp.Analyzers.Semantics;

internal enum GuardLockKind { Monitor, ThreadingLock, Read, UpgradeableRead, Write, SemaphoreSlim }

internal readonly struct GuardKey : IEquatable<GuardKey>
{
    internal GuardKey(int identity, GuardLockKind kind) { Identity = identity; Kind = kind; }
    internal int Identity { get; }
    internal GuardLockKind Kind { get; }
    public bool Equals(GuardKey other) => Identity == other.Identity && Kind == other.Kind;
    public override bool Equals(object? obj) => obj is GuardKey other && Equals(other);
    public override int GetHashCode() => Identity * 397 ^ (int)Kind;
}

/// <summary>A finite interval of reentrant acquisitions. Infinity is only an upper bound.</summary>
internal readonly struct GuardHold : IEquatable<GuardHold>
{
    private const int Saturation = 16;
    internal GuardHold(int minimum, int maximum, Location? acquisition = null)
    { Minimum = minimum; Maximum = maximum; Acquisition = acquisition; }
    internal int Minimum { get; }
    internal int Maximum { get; }
    internal Location? Acquisition { get; }
    internal GuardHold Acquire(Location site) => new(Math.Min(Saturation, Minimum + 1),
        Maximum >= Saturation ? int.MaxValue : Maximum + 1, site);
    internal GuardHold Release(int count = 1) => new(Math.Max(0, Minimum - count),
        Maximum == int.MaxValue ? int.MaxValue : Math.Max(0, Maximum - count), Acquisition);
    internal GuardHold Join(GuardHold other) => new(Math.Min(Minimum, other.Minimum),
        Math.Max(Maximum, other.Maximum), Acquisition ?? other.Acquisition);
    public bool Equals(GuardHold other) => Minimum == other.Minimum && Maximum == other.Maximum;
    public override bool Equals(object? obj) => obj is GuardHold other && Equals(other);
    public override int GetHashCode() => Minimum * 397 ^ Maximum;
}

internal readonly struct GuardObligation : IEquatable<GuardObligation>
{
    internal GuardObligation(int identity, GuardContract contract, ISymbol member)
    { Identity = identity; Contract = contract; Member = member; }
    internal int Identity { get; }
    internal GuardContract Contract { get; }
    internal ISymbol Member { get; }
    public bool Equals(GuardObligation other) => Identity == other.Identity && Contract.Protocol == other.Contract.Protocol &&
        SynchronizationContracts.Same(Member, other.Member) && Contract.Text == other.Contract.Text;
    public override bool Equals(object? obj) => obj is GuardObligation other && Equals(other);
    public override int GetHashCode() => Identity * 397 ^ (int)Contract.Protocol ^ Contract.Text.GetHashCode();
}

/// <summary>A branch fact tied to a particular acquisition; any later lock mutation invalidates it.</summary>
internal sealed class GuardCondition
{
    internal GuardCondition(GuardKey key, GuardHold before, GuardHold acquired, bool whenTrue = true, bool observed = false)
    { Key = key; Before = before; Acquired = acquired; WhenTrue = whenTrue; Observed = observed; }
    internal GuardKey Key { get; }
    internal GuardHold Before { get; }
    internal GuardHold Acquired { get; }
    internal bool WhenTrue { get; }
    internal bool Observed { get; }
    internal GuardCondition Negate() => new(Key, Before, Acquired, !WhenTrue, Observed);
    internal bool Same(GuardCondition other) => Key.Equals(other.Key) && Before.Equals(other.Before) &&
        Acquired.Equals(other.Acquired) && WhenTrue == other.WhenTrue && Observed == other.Observed;
}

internal sealed class GuardValue
{
    internal static readonly GuardValue Unknown = new(0);
    internal GuardValue(int identity, ImmutableArray<GuardObligation> obligations = default, bool? boolean = null,
        GuardCondition? condition = null, GuardKey? scope = null, int pendingWait = 0)
    { Identity = identity; Obligations = obligations.IsDefault ? ImmutableArray<GuardObligation>.Empty : obligations;
        Boolean = boolean; Condition = condition; Scope = scope; PendingWait = pendingWait; }
    internal int Identity { get; }
    internal ImmutableArray<GuardObligation> Obligations { get; }
    internal bool? Boolean { get; }
    internal GuardCondition? Condition { get; }
    internal GuardKey? Scope { get; }
    internal int PendingWait { get; }
    internal GuardValue WithoutCondition() => Condition is null ? this : new(Identity, Obligations, Boolean, scope: Scope, pendingWait: PendingWait);
    internal GuardValue Join(GuardValue other)
    {
        var obligations = Obligations;
        foreach (var requirement in other.Obligations)
            if (!obligations.Contains(requirement)) obligations = obligations.Add(requirement);
        return new GuardValue(Identity == other.Identity ? Identity : 0, obligations,
            Boolean == other.Boolean ? Boolean : null,
            Condition is not null && other.Condition is not null && Condition.Same(other.Condition) ? Condition : null,
            Scope.HasValue && other.Scope.HasValue && Scope.Value.Equals(other.Scope.Value) ? Scope : null,
            PendingWait == other.PendingWait ? PendingWait : 0);
    }
    internal bool Same(GuardValue other) => Identity == other.Identity && Boolean == other.Boolean && PendingWait == other.PendingWait &&
        Nullable.Equals(Scope, other.Scope) && (Condition is null ? other.Condition is null :
            other.Condition is not null && Condition.Same(other.Condition)) && Obligations.Length == other.Obligations.Length &&
        Obligations.All(other.Obligations.Contains);
}

/// <summary>Flattened storage targets, separate from a by-value snapshot of a guard receiver.</summary>
internal sealed class GuardStorage
{
    internal GuardStorage(ImmutableHashSet<int> targets, GuardValue? external = null)
    {
        if (targets.Count > 256) throw new AnalysisBudgetException("Synchronization reference target budget exceeded 256.");
        Targets = targets; External = external;
    }
    internal ImmutableHashSet<int> Targets { get; }
    internal GuardValue? External { get; }
    internal GuardStorage Join(GuardStorage other) => new(Targets.Union(other.Targets),
        External is null ? other.External : other.External is null ? External : External.Join(other.External));
    internal bool Same(GuardStorage other) => Targets.SetEquals(other.Targets) &&
        (External is null ? other.External is null : other.External is not null && External.Same(other.External));
}

/// <summary>A compiler-omitted lexical Lock.Scope acquisition, with its pre-entry depth.</summary>
internal readonly struct LexicalGuard : IEquatable<LexicalGuard>
{
    internal LexicalGuard(GuardKey key, GuardHold before, bool definite = true)
    { Key = key; Before = before; Definite = definite; }
    internal GuardKey Key { get; }
    internal GuardHold Before { get; }
    internal bool Definite { get; }
    internal LexicalGuard Join(LexicalGuard other) => new(Key.Equals(other.Key) ? Key : new GuardKey(0, Key.Kind),
        Before.Join(other.Before), Definite && other.Definite);
    internal LexicalGuard Optional() => new(Key, Before, false);
    public bool Equals(LexicalGuard other) => Key.Equals(other.Key) && Before.Equals(other.Before) && Definite == other.Definite;
    public override bool Equals(object? obj) => obj is LexicalGuard other && Equals(other);
    public override int GetHashCode() => Key.GetHashCode() * 397 ^ Before.GetHashCode();
}

/// <summary>Mutable method-local state; identities are interned integers, never display strings.</summary>
internal sealed class SynchronizationState
{
    internal Dictionary<int, GuardValue> Values { get; } = new();
    internal Dictionary<int, GuardStorage> References { get; } = new();
    internal Dictionary<int, GuardStorage> CaptureTargets { get; } = new();
    internal Dictionary<int, LexicalGuard> LexicalScopes { get; } = new();
    internal Dictionary<GuardKey, GuardHold> Held { get; } = new();
    internal Dictionary<GuardKey, GuardHold> Expected { get; } = new();
    internal HashSet<int> SemaphoreMutexes { get; } = new();
    internal Dictionary<int, PendingSemaphoreWait> PendingWaits { get; } = new();
    internal GuardValue Returned { get; set; } = GuardValue.Unknown;
    internal SynchronizationState Clone()
    {
        var result = new SynchronizationState { Returned = Returned };
        foreach (var pair in Values) result.Values.Add(pair.Key, pair.Value);
        foreach (var pair in References) result.References.Add(pair.Key, pair.Value);
        foreach (var pair in CaptureTargets) result.CaptureTargets.Add(pair.Key, pair.Value);
        foreach (var pair in LexicalScopes) result.LexicalScopes.Add(pair.Key, pair.Value);
        foreach (var pair in Held) result.Held.Add(pair.Key, pair.Value);
        foreach (var pair in Expected) result.Expected.Add(pair.Key, pair.Value);
        result.SemaphoreMutexes.UnionWith(SemaphoreMutexes);
        foreach (var pair in PendingWaits) result.PendingWaits.Add(pair.Key, pair.Value);
        return result;
    }
    internal GuardValue Get(int slot) => References.TryGetValue(slot, out var storage) ? Read(storage) : Direct(slot);
    internal GuardValue Direct(int slot) => Values.TryGetValue(slot, out var value) ? value : GuardValue.Unknown;
    internal GuardStorage Address(int slot) => References.TryGetValue(slot, out var storage) ? storage :
        new GuardStorage(ImmutableHashSet.Create(slot));
    internal GuardValue Read(GuardStorage storage)
    {
        var value = storage.External;
        foreach (var target in storage.Targets) value = value is null ? Direct(target) : value.Join(Direct(target));
        return value ?? GuardValue.Unknown;
    }
    internal void Write(GuardStorage storage, GuardValue value)
    {
        var exact = storage.Targets.Count == 1 && storage.External is null;
        foreach (var target in storage.Targets) Values[target] = exact ? value : Direct(target).Join(value);
    }
    internal void Assign(int slot, GuardValue value)
    { if (References.TryGetValue(slot, out var storage)) Write(storage, value); else Values[slot] = value; }
    internal GuardHold Get(GuardKey key) => Held.TryGetValue(key, out var value) ? value : default;
    internal void Set(GuardKey key, GuardHold hold)
    {
        // A saved TryEnter/IsEntered result is historical after another operation on this lock.
        foreach (var slot in Values.Where(pair => pair.Value.Condition?.Key.Equals(key) == true).Select(pair => pair.Key).ToArray())
            Values[slot] = Values[slot].WithoutCondition();
        foreach (var token in PendingWaits.Where(pair => pair.Value.Key.Equals(key)).Select(pair => pair.Key).ToArray())
            PendingWaits[token] = PendingWaits[token].WithoutCondition();
        Refine(key, hold);
    }
    internal void Refine(GuardKey key, GuardHold hold)
    { if (hold.Maximum == 0) Held.Remove(key); else Held[key] = hold; }
    internal bool Has(GuardObligation obligation, bool write) => Has(obligation.Identity, obligation.Contract.Protocol, write);
    internal bool Has(int identity, GuardProtocol protocol, bool write)
    {
        if (identity == 0) return false;
        return protocol switch
        {
            GuardProtocol.Monitor => Get(new GuardKey(identity, GuardLockKind.Monitor)).Minimum > 0,
            GuardProtocol.ThreadingLock => Get(new GuardKey(identity, GuardLockKind.ThreadingLock)).Minimum > 0,
            GuardProtocol.SemaphoreSlim => SemaphoreMutexes.Contains(identity) && Get(new GuardKey(identity, GuardLockKind.SemaphoreSlim)).Minimum > 0,
            _ => Get(new GuardKey(identity, GuardLockKind.Write)).Minimum > 0 || !write &&
                (Get(new GuardKey(identity, GuardLockKind.Read)).Minimum > 0 || Get(new GuardKey(identity, GuardLockKind.UpgradeableRead)).Minimum > 0)
        };
    }
    internal bool Join(SynchronizationState other)
    {
        var changed = false;
        foreach (var identity in SemaphoreMutexes.Where(identity => !other.SemaphoreMutexes.Contains(identity)).ToArray())
        { SemaphoreMutexes.Remove(identity); changed = true; }
        foreach (var token in PendingWaits.Keys.Union(other.PendingWaits.Keys).ToArray())
        {
            var hasBefore = PendingWaits.TryGetValue(token, out var before);
            var hasOther = other.PendingWaits.TryGetValue(token, out var incoming);
            var after = hasBefore && hasOther ? before!.Join(incoming!) : (hasBefore ? before! : incoming!).Optional();
            if (hasBefore && before!.Same(after)) continue;
            PendingWaits[token] = after; changed = true;
        }
        foreach (var slot in Values.Keys.Union(other.Values.Keys).ToArray())
        {
            var before = Direct(slot); var after = before.Join(other.Direct(slot));
            if (before.Same(after)) continue;
            Values[slot] = after; changed = true;
        }
        changed |= JoinStorage(References, other.References);
        changed |= JoinStorage(CaptureTargets, other.CaptureTargets);
        foreach (var key in LexicalScopes.Keys.Union(other.LexicalScopes.Keys).ToArray())
        {
            var hasBefore = LexicalScopes.TryGetValue(key, out var before);
            var hasOther = other.LexicalScopes.TryGetValue(key, out var incoming);
            var after = hasBefore && hasOther ? before.Join(incoming) : (hasBefore ? before : incoming).Optional();
            if (hasBefore && before.Equals(after)) continue;
            LexicalScopes[key] = after; changed = true;
        }
        foreach (var key in Held.Keys.Union(other.Held.Keys).ToArray())
        {
            var before = Get(key); var after = before.Join(other.Get(key));
            if (before.Equals(after)) continue;
            if (after.Maximum == 0) Held.Remove(key); else Held[key] = after;
            changed = true;
        }
        foreach (var key in Expected.Keys.Union(other.Expected.Keys).ToArray())
        {
            var before = Expected.TryGetValue(key, out var first) ? first : default;
            var after = before.Join(other.Expected.TryGetValue(key, out var second) ? second : default);
            if (before.Equals(after)) continue;
            Expected[key] = after; changed = true;
        }
        var returned = Returned.Join(other.Returned);
        if (!Returned.Same(returned)) { Returned = returned; changed = true; }
        return changed;
    }
    private static bool JoinStorage(Dictionary<int, GuardStorage> target, Dictionary<int, GuardStorage> source)
    {
        var changed = false;
        foreach (var slot in target.Keys.Union(source.Keys).ToArray())
        {
            var hasBefore = target.TryGetValue(slot, out var before);
            var unknown = new GuardStorage(ImmutableHashSet<int>.Empty, GuardValue.Unknown);
            var incoming = source.TryGetValue(slot, out var found) ? found : unknown;
            var after = (hasBefore ? before! : unknown).Join(incoming);
            if (hasBefore && before!.Same(after)) continue;
            target[slot] = after; changed = true;
        }
        return changed;
    }
}
