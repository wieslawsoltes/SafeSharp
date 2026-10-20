using System;

namespace SafeSharp;

/// <summary>Names the lifetime of borrowed contents at an explicit position inside a parameter or result type.</summary>
/// <remarks>
/// Paths contain dot-separated <c>arg0</c> (generic argument), <c>element</c> (array element),
/// <c>param0</c> (delegate parameter), and <c>return</c> (delegate result) segments. An empty path
/// denotes the whole type. Entering a delegate signature introduces a fresh lifetime binder.
/// Prefix a name with <c>^</c> per enclosing binder to refer to an outer region; ordinary names
/// are local to the innermost delegate signature. These are explicit boundary contracts, not CLR
/// lifetime parameters or inferred descriptions of arbitrary container implementations.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
public sealed class TypeLifetimeAttribute : Attribute
{
    public TypeLifetimeAttribute(string name, string path)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string Name { get; }
    public string Path { get; }
}

/// <summary>Requires a named generic type parameter's borrowed contents to outlive a signature region.</summary>
/// <remarks>
/// Corresponds to the explicit Rust obligation <c>T: 'a</c>. A lifetime-annotated input whose
/// type is that generic parameter supplies an implied bound. Unannotated CLR type arguments
/// do not automatically acquire borrowed-content semantics.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = true, Inherited = false)]
public sealed class TypeOutlivesAttribute : Attribute
{
    public TypeOutlivesAttribute(string typeParameter, string region)
    {
        TypeParameter = typeParameter ?? throw new ArgumentNullException(nameof(typeParameter));
        Region = region ?? throw new ArgumentNullException(nameof(region));
    }

    public string TypeParameter { get; }
    public string Region { get; }

    /// <summary>Optional callable binder path, such as <c>result</c> or <c>parameter0.arg0</c>.</summary>
    /// <remarks>
    /// Null or empty selects the outer signature. A nested scope must already be introduced by
    /// TypeLifetime paths, and <see cref="Region"/> must be declared locally in that binder.
    /// </remarks>
    public string? Scope { get; set; }
}
