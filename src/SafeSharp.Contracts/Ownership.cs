namespace SafeSharp;

/// <summary>Expresses ownership transitions that SafeSharp recognizes at compile time.</summary>
/// <remarks>
/// All methods return their argument unchanged; they do not enforce access restrictions at runtime.
/// The design follows Rust's moves and shared/exclusive references:
/// https://doc.rust-lang.org/book/ch04-02-references-and-borrowing.html.
/// </remarks>
public static class Ownership
{
    /// <summary>Transfers ownership and invalidates the previous owner's subsequent use in analyzed code.</summary>
    [ReturnsOwned]
    [return: Owned]
    public static T Move<T>([Consumes] T value) => value;

    /// <summary>Creates a shared loan whose lifetime is bounded by <paramref name="value"/>.</summary>
    [ReturnsBorrowed("value")]
    [return: Borrowed]
    public static T Borrow<T>([Borrowed] T value) => value;

    /// <summary>Creates an exclusive mutable loan whose lifetime is bounded by <paramref name="value"/>.</summary>
    [ReturnsBorrowed("value", mutable: true)]
    [return: BorrowedMut]
    public static T BorrowMut<T>([BorrowedMut] T value) => value;
}
