using System;

namespace SafeSharp;

/// <summary>The minimum access granted by a synchronization capability.</summary>
public enum LockAccess
{
    /// <summary>A read, upgradeable-read, or exclusive lock permits observation.</summary>
    Read,
    /// <summary>An exclusive/write lock is required for mutation.</summary>
    Write
}

/// <summary>
/// Requires the named guard when accessing a field/property, or when using a returned reference.
/// The path names <c>this</c>, a method parameter, or a chain of readonly fields. Guards are matched
/// by receiver and symbol identity. Monitor, System.Threading.Lock, and ReaderWriterLockSlim
/// protocols remain distinct; a lock on another instance does not satisfy this contract.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.ReturnValue,
    AllowMultiple = true, Inherited = false)]
public sealed class GuardedByAttribute : Attribute
{
    public GuardedByAttribute(string guard) => Guard = guard;
    public string Guard { get; }
}

/// <summary>
/// Requires the caller to hold the named guard, and assumes that capability while checking the
/// method body. The requirement is inherited by overrides and implementations. This contract
/// describes synchronization, not ownership transfer or permission to retain borrowed arguments.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Delegate,
    AllowMultiple = true, Inherited = false)]
public sealed class RequiresLockAttribute : Attribute
{
    public RequiresLockAttribute(string guard, LockAccess access = LockAccess.Write)
    { Guard = guard; Access = access; }
    public string Guard { get; }
    public LockAccess Access { get; }
}
