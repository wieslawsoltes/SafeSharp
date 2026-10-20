using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SafeSharp.Analyzers;

namespace SafeSharp.Tests.Flow;

/// <summary>Exercises framework symbols and contract inheritance through the complete Roslyn adapter.</summary>
public sealed class AdvancedOwnershipTests
{
    private const string Prelude = """
        using System;
        using System.Buffers;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable
        {
            public int Value;
            public void Read() { }
            [Mutates] public void Mutate() { Value++; }
            public void Dispose() { }
        }
        sealed class AsyncResource : IAsyncDisposable
        {
            public void Read() { }
            public ValueTask DisposeAsync() => default;
        }
        """;

    public static TheoryData<string, string> Violations => new()
    {
        { "class Example { void M() { var buffer = [|ArrayPool<byte>.Shared.Rent(16)|]; } }", "SS0003" },
        { "class Example { void M() { var buffer = ArrayPool<byte>.Shared.Rent(16); ArrayPool<byte>.Shared.Return(buffer); [|ArrayPool<byte>.Shared.Return(buffer)|]; } }", "SS0002" },
        { "class Example { void M() { var buffer = ArrayPool<byte>.Shared.Rent(16); ArrayPool<byte>.Shared.Return(buffer); _ = [|buffer.Length|]; } }", "SS0001" },
        { "class Example { void M() { var owner = [|MemoryPool<byte>.Shared.Rent(16)|]; } }", "SS0003" },
        { "class Example { void M() { var owner = MemoryPool<byte>.Shared.Rent(16); var view = owner.Memory; [|owner.Dispose()|]; _ = view.Length; } }", "SS0014" },
        { "class Example { void M() { var owner = MemoryPool<byte>.Shared.Rent(16); var view = owner.Memory; owner.Dispose(); _ = [|view.Length|]; } }", "SS0001" },
        { "class Example { async Task M() { using var owner = MemoryPool<byte>.Shared.Rent(16); var view = owner.Memory; [|await Task.Yield()|]; _ = view.Length; } }", "SS0010" },
        { "class Example { async Task M() { var r = new AsyncResource(); await r.DisposeAsync(); [|r.Read()|]; } }", "SS0001" },
        { "[NoSuspend] class AffineHandle { public void Read() { } } class Example { async Task M() { var value = new AffineHandle(); [|await Task.Yield()|]; value.Read(); } }", "SS0016" },
        { "[NoSuspend] class HandleBase { public void Read() { } } class Handle : HandleBase { } class Example { async Task M() { var value = new Handle(); [|await Task.Yield()|]; value.Read(); } }", "SS0016" },
        { "class Example { [ReturnsOwned] Resource Create() => throw new InvalidOperationException(); void M() { var r = [|Create()|]; } }", "SS0003" },
        { "interface IFactory { [ReturnsOwned] Resource Create(); } class Factory : IFactory { public Resource Create() => throw new InvalidOperationException(); } class Example { void M(Factory factory) { var r = [|factory.Create()|]; } }", "SS0003" },
        { "interface IEditor { void Edit([Borrowed] Resource original); } class Example : IEditor { public void Edit(Resource renamed) { [|renamed.Mutate()|]; } }", "SS0007" },
        { "interface IConsumer { void Consume([Consumes] Resource original); } class Consumer : IConsumer { public void Consume(Resource renamed) { renamed.Dispose(); } } class Example { void M(Consumer consumer) { var value = new Resource(); consumer.Consume(value); [|value.Read()|]; } }", "SS0004" },
        { "abstract class Editor { public abstract void Edit([Borrowed] Resource value); } class Example : Editor { public override void Edit(Resource renamed) { [|renamed.Mutate()|]; } }", "SS0007" },
        { "interface IIdentity { [ReturnsBorrowed(\"original\")] Resource Get(Resource original); } class Identity : IIdentity { public Resource Get(Resource renamed) => throw new InvalidOperationException(); } class Example { void M(Identity identity) { var owner = new Resource(); var view = identity.Get(owner); [|owner.Dispose()|]; view.Read(); } }", "SS0014" },
        { "abstract class Owner : IDisposable { [ReturnsBorrowed(\"this\")] public abstract Resource Item { get; } public void Dispose() { } } class Derived : Owner { public override Resource Item => throw new InvalidOperationException(); } class Example { void M() { var owner = new Derived(); var view = owner.Item; [|owner.Dispose()|]; view.Read(); } }", "SS0014" },
        { "class Example { void M() { var owner = new Resource(); var view = Ownership.Borrow(owner); Rewrite([|ref owner|]); view.Read(); owner.Dispose(); } void Rewrite(ref Resource value) { } }", "SS0007" },
        { "class Example { void M([Borrowed] Resource value) { [|value.Mutate()|]; } }", "SS0007" },
        { "class Example { void M() { var owner = new Resource(); Rewrite([|out owner|]); } void Rewrite(out Resource value) { value = new Resource(); } }", "SS0011" },
        { "class Mixed : IDisposable { void IDisposable.Dispose() { } public int Dispose() => 0; } class Example { void M() { var r = [|new Mixed()|]; r.Dispose(); } }", "SS0003" },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public async Task ReportsFrameworkAndInheritedContractViolationsAtTheActualOperation(string body, string expectedId)
    {
        var (source, expectedSpan) = ExtractMarker(Prelude + body);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, new OwnershipAnalyzer());
        var matching = diagnostics.Where(diagnostic => diagnostic.Id == expectedId && diagnostic.Location.SourceSpan == expectedSpan).ToArray();
        Assert.True(matching.Length == 1, "Expected " + expectedId + " at " + expectedSpan + Environment.NewLine +
            string.Join(Environment.NewLine, diagnostics.Select(diagnostic => diagnostic.Id + " " + diagnostic.Location.SourceSpan + " " + diagnostic.GetMessage())));
        var diagnostic = matching[0];
        Assert.Contains(diagnostic.Properties["SafeSharp.Certainty"], new[] { "Definite", "Possible" });
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Properties["SafeSharp.Evidence"]));
        Assert.DoesNotContain(diagnostics, issue => issue.Id == "SS0015");
    }

    public static TheoryData<string> Accepted => new()
    {
        "class Example { void M() { var buffer = ArrayPool<byte>.Shared.Rent(16); _ = buffer.Length; ArrayPool<byte>.Shared.Return(buffer); } }",
        "class Example { void M() { var buffer = ArrayPool<byte>.Shared.Rent(16); var alias = buffer; ArrayPool<byte>.Shared.Return(alias); } }",
        "class Example { void M() { using var owner = MemoryPool<byte>.Shared.Rent(16); var view = owner.Memory; _ = view.Length; } }",
        "class Example { void M() { using var owner = MemoryPool<byte>.Shared.Rent(16); var view = owner.Memory.Span; _ = view.Length; } }",
        "class Example { async Task M() { await using var value = new AsyncResource(); value.Read(); await Task.Yield(); } }",
        "class Example { async Task M() { var value = new AsyncResource(); value.Read(); await value.DisposeAsync(); } }",
        "[NoSuspend] class Handle { public void Read() { } } class Example { async Task M() { var value = new Handle(); value.Read(); await Task.Yield(); } }",
        "class Example { [ReturnsOwned] Resource Create() => throw new InvalidOperationException(); void M() { using var value = Create(); value.Read(); } }",
        "interface IFactory { [ReturnsOwned] Resource Create(); } class Factory : IFactory { public Resource Create() => throw new InvalidOperationException(); } class Example { void M(Factory factory) { using var r = factory.Create(); r.Read(); } }",
        "class Example { void M([BorrowedMut] Resource value) { value.Mutate(); } }",
        "interface IEditor { void Edit([BorrowedMut] Resource original); } class Example : IEditor { public void Edit(Resource renamed) { renamed.Mutate(); } }",
        "interface IIdentity { [ReturnsBorrowed(\"source\")] Resource Get(Resource source); } class Identity : IIdentity { public Resource Get(Resource renamed) => throw new InvalidOperationException(); } class Example { void M(Identity identity) { using var owner = new Resource(); var view = identity.Get(owner); view.Read(); } }",
        "namespace Foreign { class ArrayPool<T> { public static ArrayPool<T> Shared => new ArrayPool<T>(); public T[] Rent(int count) => new T[count]; public void Return(T[] buffer) { } } } class Example { void M() { var buffer = Foreign.ArrayPool<byte>.Shared.Rent(16); Foreign.ArrayPool<byte>.Shared.Return(buffer); _ = buffer.Length; } }",
        "namespace Foreign { sealed class NoSuspendAttribute : Attribute { } } [Foreign.NoSuspend] class Handle { public void Read() { } } class Example { async Task M() { var value = new Handle(); await Task.Yield(); value.Read(); } }",
        "class Mixed : IDisposable { void IDisposable.Dispose() { } public int Dispose() => 0; } class Example { void M() { using var r = new Mixed(); _ = r.Dispose(); } }",
        "ref struct PatternOwner { public void Dispose() { } } class Example { void M() { using var value = new PatternOwner(); } }",
        "ref struct PatternOwner { public void Dispose() { } } class Example { void M() { var value = new PatternOwner(); value.Dispose(); } }",
        "class Owner : IDisposable { public virtual void Dispose() { } } class Derived : Owner { public override void Dispose() { } } class Example { void M() { var value = new Derived(); value.Dispose(); } }",
        "class Owner : IDisposable { void IDisposable.Dispose() { } } class Example { void M() { var value = new Owner(); ((IDisposable)value).Dispose(); } }",
        "class Example { void M() { var value = new Resource(); var loan = Ownership.Borrow(value); loan.Read(); Rewrite(ref value); value.Dispose(); } void Rewrite(ref Resource value) { } }",
        "class Example { void M() { var value = new Resource(); value.Dispose(); Rewrite(out value); value.Dispose(); } void Rewrite(out Resource value) { value = new Resource(); } }",
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task AcceptsFrameworkLifetimesAndInheritsCompatibleContracts(string body)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + body, new OwnershipAnalyzer());
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ConditionalPoolReturnProducesPossibleUseAfterReturn()
    {
        var (source, span) = ExtractMarker(Prelude + "class Example { void M(bool release) { var value = ArrayPool<byte>.Shared.Rent(16); if (release) ArrayPool<byte>.Shared.Return(value); _ = [|value.Length|]; } }");
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan == span);
        Assert.Equal("Possible", diagnostic.Properties["SafeSharp.Certainty"]);
        Assert.NotEmpty(diagnostic.AdditionalLocations);
    }

    private static (string Source, TextSpan Span) ExtractMarker(string marked)
    {
        var start = marked.IndexOf("[|", StringComparison.Ordinal);
        var end = marked.IndexOf("|]", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "A fixture must contain one nonempty diagnostic span marker.");
        return (marked.Remove(end, 2).Remove(start, 2), new TextSpan(start, end - start - 2));
    }
}
