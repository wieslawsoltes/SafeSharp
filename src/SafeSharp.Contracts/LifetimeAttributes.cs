using System;

namespace SafeSharp;

/// <summary>Names the caller-visible region for which a parameter or borrowed result is valid.</summary>
/// <remarks>
/// Names are scoped to a method signature. Equal names share a region; outlives relations are declared
/// with <see cref="OutlivesAttribute"/>. This metadata does not change CLR object or C# reference semantics.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, Inherited = false)]
public sealed class LifetimeAttribute : Attribute
{
    /// <summary>Creates a named lifetime contract.</summary>
    public LifetimeAttribute(string name) => Name = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>Gets the method-scoped region name.</summary>
    public string Name { get; }
}

/// <summary>Requires one named region to remain valid for at least as long as another named region.</summary>
/// <remarks>
/// <c>[Outlives("a", "b")]</c> corresponds to the explicit relation <c>'a: 'b</c> in Rust.
/// Constraints may be repeated and imply transitive relations; cycles establish equivalent regions.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class OutlivesAttribute : Attribute
{
    /// <summary>Creates an outlives relation between regions appearing in the method signature.</summary>
    public OutlivesAttribute(string longer, string shorter)
    {
        Longer = longer ?? throw new ArgumentNullException(nameof(longer));
        Shorter = shorter ?? throw new ArgumentNullException(nameof(shorter));
    }

    /// <summary>Gets the region which must outlive <see cref="Shorter"/>.</summary>
    public string Longer { get; }

    /// <summary>Gets the region bounded by <see cref="Longer"/>.</summary>
    public string Shorter { get; }
}
