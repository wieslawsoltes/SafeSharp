using SafeSharp;

namespace SafeSharp.Sample;

internal static class Program
{
    private static async Task Main()
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

        // An implicit mutable receiver is reserved before argument evaluation and activated
        // at the call. Reading the owner to compute a scalar argument is permitted here.
        opened.Append(opened.Length.ToString());

        using var alternative = Create();
        var selected = Choose(opened, alternative, DateTime.UtcNow.Ticks % 2 == 0);
        Console.WriteLine(selected.Length);
        // Both possible input owners remain live until the returned loan's final use.

        var asynchronous = new AsyncHandle();
        var completion = asynchronous.DisposeAsync();
        await completion.ConfigureAwait(false);

        var snapshot = new Snapshot(opened.Length);
        await Publish(() => Console.WriteLine(snapshot.Length));

        var parcel = new TransferableHandle();
        await Send(() => parcel.Dispose());
        // Send transfers the tracked capture. A later caller use would produce SS0004;
        // omitting Dispose inside this visible callback would produce SS0003 there.
    }

    private static void Release([Consumes] DocumentBuffer buffer) => buffer.Dispose();

    private static void Inspect([Borrowed] DocumentBuffer buffer) => Console.WriteLine(buffer.Length);

    [ReturnsOwned]
    private static DocumentBuffer Create() => new();

    [return: Lifetime("data")]
    private static DocumentBuffer Choose([Lifetime("data")] DocumentBuffer first,
        [Lifetime("data")] DocumentBuffer second, bool chooseFirst) => chooseFirst ? first : second;

    private static Task Publish([SharesAcrossThreads] Action callback) => Task.Run(callback);

    private static Task Send([TransfersToThread] Action callback) => Task.Run(callback);
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

internal sealed class AsyncHandle : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[Send, Sync]
internal sealed class Snapshot
{
    public Snapshot(long length) => Length = length;
    public long Length { get; }
}

[Send]
internal sealed class TransferableHandle : IDisposable
{
    public void Dispose() { }
}
