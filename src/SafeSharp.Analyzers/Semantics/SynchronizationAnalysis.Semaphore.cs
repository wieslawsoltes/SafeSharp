using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using SafeSharp.Analyzers.Analysis;

namespace SafeSharp.Analyzers.Semantics;

[Flags]
internal enum SemaphoreWaitStatus { Pending = 1, Completed = 2, Faulted = 4, Missing = 8 }

/// <summary>An affine acquisition event behind a reusable Task: awaiting the same task cannot acquire twice.</summary>
internal sealed class PendingSemaphoreWait
{
    internal PendingSemaphoreWait(GuardKey key, bool conditional, Location site, SemaphoreWaitStatus status = SemaphoreWaitStatus.Pending,
        GuardValue? result = null)
    { Key = key; Conditional = conditional; Site = site; Status = status; Result = result ?? GuardValue.Unknown; }
    internal GuardKey Key { get; }
    internal bool Conditional { get; }
    internal Location Site { get; }
    internal SemaphoreWaitStatus Status { get; }
    internal GuardValue Result { get; }
    internal bool IsPending => (Status & SemaphoreWaitStatus.Pending) != 0;
    internal PendingSemaphoreWait Complete(GuardValue result) => new(Key, Conditional, Site, SemaphoreWaitStatus.Completed, result);
    internal PendingSemaphoreWait Fault() => new(Key, Conditional, Site, SemaphoreWaitStatus.Faulted);
    internal PendingSemaphoreWait WithResult(GuardValue result) => new(Key, Conditional, Site, Status, result);
    internal PendingSemaphoreWait WithoutCondition() => WithResult(Result.WithoutCondition());
    internal PendingSemaphoreWait Optional() => new(Key, Conditional, Site, Status | SemaphoreWaitStatus.Missing, Result);
    internal PendingSemaphoreWait Join(PendingSemaphoreWait other) => new(Key.Equals(other.Key) ? Key : new GuardKey(0, GuardLockKind.SemaphoreSlim),
        Conditional || other.Conditional, Site, Status | other.Status, Result.Join(other.Result));
    internal bool Same(PendingSemaphoreWait other) => Key.Equals(other.Key) && Conditional == other.Conditional && Status == other.Status && Result.Same(other.Result);
}

internal sealed partial class SynchronizationAnalysis
{
    private readonly Dictionary<IOperation, int> semaphoreWaitTokens = new();

    private bool SemaphoreIntrinsic(IInvocationOperation call, GuardValue receiver, SynchronizationState state, out GuardValue result)
    {
        result = GuardValue.Unknown;
        var key = new GuardKey(receiver.Identity, GuardLockKind.SemaphoreSlim);
        switch (call.TargetMethod.Name)
        {
            case "Wait":
            {
                var conditional = call.TargetMethod.ReturnType.SpecialType == SpecialType.System_Boolean;
                CheckPendingSemaphoreCompetition(key, call, state);
                var before = state.Get(key);
                if (before.Minimum > 0 && state.SemaphoreMutexes.Contains(key.Identity))
                {
                    // Under the checked local permit discipline, the held binary permit
                    // leaves no capacity for a second acquisition. A timed probe can only
                    // complete normally with false; an unbounded wait cannot reenter.
                    if (conditional) { result = new GuardValue(0, boolean: false); return true; }
                    Report(call, "Wait", Name(key.Identity), "a non-reentrant binary semaphore permit is already held before this acquisition", state,
                        GuardProtocol.SemaphoreSlim.ToString(), requiredIdentity: key.Identity);
                }
                Acquire(key, call, state, conditional);
                if (conditional) result = new GuardValue(0, condition: new GuardCondition(key, before, before.Acquire(call.Syntax.GetLocation())));
                return true;
            }
            case "WaitAsync":
            {
                if (!semaphoreWaitTokens.TryGetValue(call, out var token)) semaphoreWaitTokens.Add(call, token = semaphoreWaitTokens.Count + 1);
                if (!state.PendingWaits.ContainsKey(token) && state.PendingWaits.Count >= 256)
                    throw new AnalysisBudgetException("Synchronization pending semaphore acquisition budget exceeded 256.");
                if (state.PendingWaits.TryGetValue(token, out var previous) && previous.IsPending)
                    ReportAt(previous.Site, "WaitAsync", Name(previous.Key.Identity), "another acquisition at this loop site can replace an unobserved pending acquisition", state,
                        GuardProtocol.SemaphoreSlim.ToString(), requiredIdentity: previous.Key.Identity);
                // A repeated allocation site denotes a fresh runtime Task. Older aliases cannot use
                // that site's new completion to manufacture a second permit for an old Task.
                foreach (var slot in state.Values.Where(pair => pair.Value.PendingWait == token).Select(pair => pair.Key).ToArray())
                {
                    var old = state.Values[slot];
                    state.Values[slot] = new GuardValue(old.Identity, old.Obligations, old.Boolean, old.Condition, old.Scope);
                }
                var conditional = call.TargetMethod.ReturnType is INamedTypeSymbol { Arity: 1 } task && task.TypeArguments[0].SpecialType == SpecialType.System_Boolean;
                state.Set(key, state.Get(key));
                state.PendingWaits[token] = new PendingSemaphoreWait(key, conditional, call.Syntax.GetLocation());
                result = new GuardValue(Allocation(call), pendingWait: token);
                return true;
            }
            case "Release":
            {
                var count = SemaphoreReleaseCount(call);
                var hold = state.Get(key);
                if (count is null)
                {
                    Report(call, "Release", Name(key.Identity), "the release count is not a constant; the remaining permit count is not established", state,
                        GuardProtocol.SemaphoreSlim.ToString(), requiredIdentity: key.Identity);
                    state.Set(key, new GuardHold(0, hold.Release().Maximum, hold.Acquisition));
                    return true;
                }
                if (count <= 0)
                {
                    Report(call, "Release", Name(key.Identity), "the release count must be positive", state, GuardProtocol.SemaphoreSlim.ToString(), requiredIdentity: key.Identity);
                    return true;
                }
                if (key.Identity == 0 || hold.Minimum < count)
                    Report(call, "Release", Name(key.Identity), "the requested number of permits is not held on every incoming path", state,
                        GuardProtocol.SemaphoreSlim.ToString(), requiredIdentity: key.Identity);
                var after = hold.Release(count.Value);
                if (state.Expected.TryGetValue(key, out var expected) && after.Minimum < expected.Minimum)
                    Report(call, method.Name, Name(key.Identity), "the operation releases a caller-supplied synchronization capability", state,
                        GuardProtocol.SemaphoreSlim.ToString(), expected.Acquisition, key.Identity);
                state.Set(key, after);
                return true;
            }
            case "Dispose":
                if (state.Get(key).Maximum > 0 || state.PendingWaits.Values.Any(wait => wait.Key.Equals(key) && wait.IsPending))
                    Report(call, "Dispose", Name(key.Identity), "a semaphore may be disposed while a permit or pending acquisition remains outstanding", state,
                        GuardProtocol.SemaphoreSlim.ToString(), requiredIdentity: key.Identity);
                state.Set(key, new GuardHold(0, state.Get(key).Maximum, state.Get(key).Acquisition));
                return true;
            default:
                return false;
        }
    }

    private void CheckPendingSemaphoreCompetition(GuardKey key, IOperation operation, SynchronizationState state)
    {
        if (state.SemaphoreMutexes.Contains(key.Identity) && state.PendingWaits.Values.Any(wait => wait.Key.Equals(key) && wait.IsPending))
            Report(operation, "Wait", Name(key.Identity), "an earlier unobserved acquisition may already hold this binary semaphore permit", state,
                GuardProtocol.SemaphoreSlim.ToString(), requiredIdentity: key.Identity);
    }

    private static int? SemaphoreReleaseCount(IInvocationOperation call) => call.Arguments.Length == 0 ? 1 :
        call.Arguments.FirstOrDefault(argument => argument.Parameter?.Ordinal == 0)?.Value.ConstantValue is { HasValue: true, Value: int value } ? value : null;

    private GuardValue CompleteSemaphoreWait(int token, IOperation operation, SynchronizationState state)
    {
        if (!state.PendingWaits.TryGetValue(token, out var wait)) { MayThrow(state); return GuardValue.Unknown; }
        if (wait.Status == SemaphoreWaitStatus.Completed) return wait.Result;
        if (wait.IsPending)
        {
            // Cancellation/fault of this very Task cannot grant a permit. Other pending Tasks
            // remain outstanding: cancellation of one wait is not a cancellation proof for them.
            var exceptional = state.Clone(); exceptional.PendingWaits[token] = wait.Fault(); MayThrow(exceptional);
            var before = state.Get(wait.Key);
            if (before.Minimum > 0 && state.SemaphoreMutexes.Contains(wait.Key.Identity))
            {
                if (wait.Conditional && wait.Status == SemaphoreWaitStatus.Pending)
                {
                    var timedOut = new GuardValue(0, boolean: false);
                    state.PendingWaits[token] = wait.Complete(timedOut);
                    return timedOut;
                }
                Report(operation, "WaitAsync", Name(wait.Key.Identity), "a non-reentrant binary semaphore permit is already held before this acquisition completes", state,
                    GuardProtocol.SemaphoreSlim.ToString(), requiredIdentity: wait.Key.Identity);
            }
            Acquire(wait.Key, operation, state, wait.Conditional || wait.Status != SemaphoreWaitStatus.Pending);
            var result = wait.Conditional && wait.Status == SemaphoreWaitStatus.Pending
                ? new GuardValue(0, condition: new GuardCondition(wait.Key, before, before.Acquire(operation.Syntax.GetLocation())))
                : GuardValue.Unknown;
            state.PendingWaits[token] = wait.Complete(result);
            return result;
        }
        // A known faulted Task has no successful acquisition continuation. The conservative
        // normal-state envelope deliberately establishes no capability for that unreachable arm.
        MayThrow(state); return GuardValue.Unknown;
    }

    private bool TrySemaphoreAwaitableCall(IInvocationOperation call, GuardValue receiver, SynchronizationState state, out GuardValue result)
    {
        result = GuardValue.Unknown;
        if (receiver.PendingWait == 0 || !contracts.IsSemaphoreAwaitableMember(call.TargetMethod)) return false;
        if (call.TargetMethod.Name is "GetAwaiter" or "ConfigureAwait")
        {
            // ConfigureAwait(bool) changes scheduling, not the Task's acquisition identity.
            // SuppressThrowing can hide a fault; options overloads intentionally remain opaque.
            if (call.TargetMethod.Name == "ConfigureAwait" && (call.TargetMethod.Parameters.Length != 1 ||
                call.TargetMethod.Parameters[0].Type.SpecialType != SpecialType.System_Boolean)) return false;
            result = new GuardValue(receiver.Identity, receiver.Obligations, pendingWait: receiver.PendingWait);
            return true;
        }
        if (call.TargetMethod.Name == "GetResult" && call.TargetMethod.Parameters.IsEmpty)
        { result = CompleteSemaphoreWait(receiver.PendingWait, call, state); return true; }
        return false;
    }

    private bool TrySemaphoreAwaitableProperty(IPropertyReferenceOperation property, GuardValue receiver, SynchronizationState state, out GuardValue result)
    {
        result = GuardValue.Unknown;
        if (receiver.PendingWait == 0 || property.Property.Name != "Result" || property.Property.GetMethod is not { } getter ||
            !contracts.IsSemaphoreAwaitableMember(getter)) return false;
        result = CompleteSemaphoreWait(receiver.PendingWait, property, state);
        return true;
    }

    private void ValidatePendingSemaphoreWaits(SynchronizationState state)
    {
        foreach (var wait in state.PendingWaits.Values.Where(wait => wait.IsPending))
            ReportAt(wait.Site, "WaitAsync", Name(wait.Key.Identity),
                "a pending acquisition can complete without an observed result and matching release" + (context.Exceptional ? " on an exception path" : ""), state,
                GuardProtocol.SemaphoreSlim.ToString(), requiredIdentity: wait.Key.Identity);
    }
}
