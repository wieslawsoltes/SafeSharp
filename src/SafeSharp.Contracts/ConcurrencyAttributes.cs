using System;

namespace SafeSharp;

/// <summary>Declares that an exclusively owned value may be transferred to another thread.</summary>
/// <remarks>SafeSharp validates declared structural composition. The contract does not create exclusive ownership or change C# reference copying.</remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.GenericParameter, Inherited = false)]
public sealed class SendAttribute : Attribute { }

/// <summary>Declares that a value supports shared access across threads.</summary>
/// <remarks>Source declarations require immutable, recursively shareable state unless a trusted <see cref="ThreadSafeAttribute"/> supplies synchronization.</remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.GenericParameter, Inherited = false)]
public sealed class SyncAttribute : Attribute { }

/// <summary>Declares that a callback boundary transfers exclusive use of its captured values to another thread.</summary>
/// <remarks>The callback implementation and caller must uphold exclusive transfer. SafeSharp checks Send composition and captured variable writes.</remarks>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class TransfersToThreadAttribute : Attribute { }

/// <summary>Declares that a callback may share its captured values across threads.</summary>
/// <remarks>SafeSharp checks Sync composition and rejects mutable captured variable cells at this boundary.</remarks>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class SharesAcrossThreadsAttribute : Attribute { }
