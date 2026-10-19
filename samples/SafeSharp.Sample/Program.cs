using SafeSharp;

namespace SafeSharp.Sample;

internal static class Program
{
    private static void Main()
    {
        using var owner = new DocumentBuffer();
        var view = Ownership.Borrow(owner);
        Console.WriteLine(view.Length);
        // The shared loan ends at its final use above, so mutation is now legal.
        owner.Append("SafeSharp");

        var exclusive = Ownership.BorrowMut(owner);
        exclusive.Append(" checks ownership");
        Console.WriteLine(exclusive.Length);

        // Passing a value to a consuming parameter transfers the caller's ownership.
        var transferred = new DocumentBuffer();
        Release(transferred);
        // Reading 'transferred' here would produce SS0004.

        using var opened = Create();
        Inspect(opened);
        Console.WriteLine(opened.Length);
    }

    private static void Release([Consumes] DocumentBuffer buffer) => buffer.Dispose();

    private static void Inspect([Borrowed] DocumentBuffer buffer) => Console.WriteLine(buffer.Length);

    [ReturnsOwned]
    private static DocumentBuffer Create() => new();
}

internal sealed class DocumentBuffer : IDisposable
{
    [Owned] private readonly MemoryStream _stream = new();
    public long Length => _stream.Length;

    [Mutates]
    public void Append(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        _stream.Write(bytes, 0, bytes.Length);
    }

    public void Dispose() => _stream.Dispose();
}
