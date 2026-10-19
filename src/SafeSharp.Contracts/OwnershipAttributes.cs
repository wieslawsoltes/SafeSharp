using System;

namespace SafeSharp;

/// <summary>Declares responsibility for an object's lifetime and, for disposable objects, its release.</summary>
/// <remarks>C# assignments still copy references or values. SafeSharp checks the declared ownership convention at compile time.</remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.ReturnValue, Inherited = false)]
public sealed class OwnedAttribute : Attribute { }

/// <summary>Declares a shared, non-owning loan that cannot be consumed or retained beyond its owner's lifetime.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, Inherited = false)]
public sealed class BorrowedAttribute : Attribute { }

/// <summary>Declares an exclusive, mutable, non-owning loan.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, Inherited = false)]
public sealed class BorrowedMutAttribute : Attribute { }

/// <summary>Transfers the caller's ownership of the argument to the callee.</summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class ConsumesAttribute : Attribute { }

/// <summary>Declares that the caller owns the returned value.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = false)]
public sealed class ReturnsOwnedAttribute : Attribute { }

/// <summary>Connects a returned loan's lifetime to a named parameter or to the instance receiver, named <c>this</c>.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = false)]
public sealed class ReturnsBorrowedAttribute : Attribute
{
    /// <summary>Creates a returned loan contract.</summary>
    /// <param name="parameterName">The source parameter's name, or <c>this</c> for an instance receiver.</param>
    /// <param name="mutable">Whether the returned loan is exclusive and permits mutation.</param>
    public ReturnsBorrowedAttribute(string parameterName, bool mutable = false)
    {
        ParameterName = parameterName ?? throw new ArgumentNullException(nameof(parameterName));
        Mutable = mutable;
    }

    /// <summary>Gets the parameter or receiver whose lifetime bounds the result.</summary>
    public string ParameterName { get; }

    /// <summary>Gets whether the result is an exclusive loan.</summary>
    public bool Mutable { get; }
}

/// <summary>Declares that an instance method mutates its receiver.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class MutatesAttribute : Attribute { }

/// <summary>Declares that a callee does not retain the argument after returning.</summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class DoesNotEscapeAttribute : Attribute { }

/// <summary>Requires a result to be consumed; an explicit discard acknowledges intentional non-use.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class MustUseAttribute : Attribute { }

/// <summary>Declares that a value must not remain live across an asynchronous or iterator suspension point.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
public sealed class NoSuspendAttribute : Attribute { }

/// <summary>Declares that a value may only be accessed from its owning thread or context.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
public sealed class ThreadAffineAttribute : Attribute { }

/// <summary>Declares that a type supports sharing and use across threads.</summary>
/// <remarks>This is a trusted contract, not a proof of the implementation's synchronization.</remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
public sealed class ThreadSafeAttribute : Attribute { }

/// <summary>The expected lifetime of a dependency injection service.</summary>
public enum ServiceLifetime
{
    /// <summary>A new instance is created for each resolution.</summary>
    Transient = 0,
    /// <summary>One instance is shared within a service scope.</summary>
    Scoped = 1,
    /// <summary>One instance is shared for the root provider's lifetime.</summary>
    Singleton = 2,
}

/// <summary>Declares a service lifetime for static dependency analysis without requiring a container package.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, Inherited = false)]
public sealed class ServiceLifetimeAttribute : Attribute
{
    /// <summary>Creates a service lifetime contract.</summary>
    public ServiceLifetimeAttribute(ServiceLifetime lifetime) => Lifetime = lifetime;

    /// <summary>Gets the declared lifetime.</summary>
    public ServiceLifetime Lifetime { get; }
}
