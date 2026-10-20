using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SafeSharp.Analyzers;
using SafeSharp.Core;

namespace SafeSharp.Tests.Flow;

public sealed class TransferredCallbackTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        [Send, Sync] sealed class Resource : IDisposable
        {
            public void Read() { }
            public void Dispose() { }
        }
        [Send] sealed class AsyncResource : IAsyncDisposable
        {
            public void Read() { }
            public ValueTask DisposeAsync() => default;
        }
        static class Dispatcher
        {
            public static void Send([TransfersToThread] Action callback) { }
            public static void SendAsync([TransfersToThread] Func<Task> callback) { }
            public static void Share([SharesAcrossThreads] Action callback) { }
        }
        """;

    private static string Source(string members) => Prelude + "\nclass Example\n{\n" + members + "\n}\n";

    public static TheoryData<string> AcceptedTransfers => new()
    {
        "void M() { var r = new Resource(); Dispatcher.Send(() => r.Dispose()); }",
        "void M() { var r = new Resource(); Action cleanup = () => r.Dispose(); Action alias = cleanup; Dispatcher.Send(alias); }",
        "void M() { var r = new Resource(); Action cleanup; cleanup = () => r.Dispose(); Dispatcher.Send(cleanup); }",
        "void M() { var r = new Resource(); void Cleanup() { r.Dispose(); } Dispatcher.Send(Cleanup); }",
        "void M() { var r = new Resource(); Dispatcher.Send(Cleanup); void Cleanup() { r.Dispose(); } }",
        "void M() { var r = new Resource(); void Cleanup() { r.Dispose(); } Action alias = Cleanup; Dispatcher.Send(alias); }",
        "void M([Owned] Resource r) { Dispatcher.Send(() => r.Dispose()); }",
        "void M(bool first) { var r = new Resource(); Dispatcher.Send(first ? () => r.Dispose() : () => r.Dispose()); }",
        "void M() { var r = new AsyncResource(); Dispatcher.SendAsync(async () => await r.DisposeAsync()); }",
        "void M() { using var r = new Resource(); Action callback = () => r.Read(); Dispatcher.Share(callback); }",
        "void M() { using var r = new Resource(); void Callback() { r.Read(); } Dispatcher.Share(Callback); }",
        "void M() { using var guard = new Resource(); var sent = new Resource(); Dispatcher.Send(() => { try { sent.Read(); } finally { sent.Dispose(); } }); }",
        "void M() { using var guard = new Resource(); var sent = new Resource(); void Callback() { try { sent.Read(); } finally { sent.Dispose(); } } Dispatcher.Send(Callback); }",
        "static readonly Resource Shared = new Resource(); void M() { Dispatcher.Send(() => Shared.Read()); Shared.Read(); }",
        "void M() { var r = new Resource(); Dispatcher.Send(r.Dispose); }",
        "void M() { var r = new Resource(); var alias = r; Dispatcher.Send(() => { r.Read(); alias.Dispose(); }); }",
        "void M() { var r = new Resource(); IDisposable alias = r; Dispatcher.Send(() => { r.Read(); alias.Dispose(); }); }",
        "void M(bool choose) { var r = new Resource(); var alias = r; Dispatcher.Send(choose ? () => r.Dispose() : () => alias.Dispose()); }",
    };

    [Theory]
    [MemberData(nameof(AcceptedTransfers))]
    public async Task TransfersCallerObligationAndChecksVisibleCallbackCleanup(string members)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Source(members));
        Assert.True(diagnostics.IsEmpty, Format(diagnostics));
    }

    public static TheoryData<string, string> Violations => new()
    {
        { "void M() { var r = new Resource(); var alias = r; Dispatcher.Send(() => r.Dispose()); [|alias.Read()|]; }", "SS0004" },
        { "void M() { var r = new Resource(); Dispatcher.Send(() => r.Dispose()); [|r.Read()|]; }", "SS0004" },
        { "void M() { var r = new Resource(); Dispatcher.Send(() => [|r|].Read()); }", "SS0003" },
        { "void M() { var r = new Resource(); Action callback = () => [|r|].Read(); Action alias = callback; Dispatcher.Send(alias); }", "SS0003" },
        { "void M() { var r = new Resource(); void Callback() { [|r|].Read(); } Dispatcher.Send(Callback); }", "SS0003" },
        { "void M() { var r = new Resource(); Dispatcher.Send(() => { r.Dispose(); [|r.Read()|]; }); }", "SS0001" },
        { "void M([Borrowed] Resource value) { Dispatcher.Send([|() => value.Read()|]); }", "SS0008" },
        { "void M() { using var r = new Resource(); var borrowed = Ownership.Borrow(r); Dispatcher.Send([|() => borrowed.Read()|]); }", "SS0008" },
        { "void M() { var r = new Resource(); Dispatcher.Send(() => r.Dispose()); Dispatcher.Send([|() => r.Dispose()|]); }", "SS0004" },
        { "void M() { var r = new Resource(); var alias = r; Dispatcher.Send(() => { r.Dispose(); [|alias.Read()|]; }); }", "SS0001" },
        { "void M([Borrowed] Resource r) { var alias = r; Dispatcher.Send(() => { r.Read(); [|alias.Dispose()|]; }); }", "SS0009" },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public async Task ReportsOwnershipViolationAtCallbackCaptureOrCallerUse(string members, string id)
    {
        var (source, span) = ExtractMarker(Source(members));
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var matches = diagnostics.Where(diagnostic => diagnostic.Id == id && diagnostic.Location.SourceSpan == span).ToArray();
        Assert.True(matches.Length == 1, "Expected " + id + " at " + span + Environment.NewLine + Format(diagnostics));
        Assert.False(string.IsNullOrWhiteSpace(matches[0].Properties["SafeSharp.Evidence"]));
    }

    [Fact]
    public async Task ConditionalDifferentCapturesRemainPossibleCallerTransfersAndIndependentCallbackInputs()
    {
        var (source, span) = ExtractMarker(Source("void M(bool choose) { var first = new Resource(); var second = new Resource(); Dispatcher.Send(choose ? () => first.Dispose() : () => second.Dispose()); [|first.Read()|]; }"));
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var moved = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0004" && diagnostic.Location.SourceSpan == span);
        Assert.Equal("Possible", moved.Properties["SafeSharp.Certainty"]);
        var leaks = diagnostics.Where(diagnostic => diagnostic.Id == "SS0003").ToArray();
        Assert.Equal(2, leaks.Length);
        Assert.All(leaks, diagnostic => Assert.Equal("Possible", diagnostic.Properties["SafeSharp.Certainty"]));
        Assert.All(leaks, diagnostic => Assert.Equal("new Resource()", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length)));

        var results = Analyze(source);
        var callbacks = results.Where(result => result.Graph.Blocks.SelectMany(block => block.Instructions).Any(instruction =>
            instruction.Kind == InstructionKind.Allocate && instruction.Description?.StartsWith("Owned input", StringComparison.Ordinal) == true)).ToArray();
        Assert.Equal(2, callbacks.Length);
        foreach (var callback in callbacks)
        {
            var ownedInputs = callback.Graph.Blocks.SelectMany(block => block.Instructions).Where(instruction =>
                instruction.Kind == InstructionKind.Allocate && instruction.Description?.StartsWith("Owned input", StringComparison.Ordinal) == true).ToArray();
            Assert.Single(ownedInputs);
            Assert.Empty(callback.Issues);
        }
    }

    [Fact]
    public async Task SharedCallbackDoesNotInheritAnotherCallbacksTransferObligation()
    {
        var source = Source("void M() { var sent = new Resource(); using var shared = new Resource(); Action reader = () => shared.Read(); Action cleanup = () => sent.Dispose(); Dispatcher.Send(cleanup); Dispatcher.Share(reader); }");
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(source));
        var inputs = Analyze(source).SelectMany(result => result.Graph.Blocks).SelectMany(block => block.Instructions)
            .Where(instruction => instruction.Kind == InstructionKind.Allocate && instruction.Description?.StartsWith("Owned input", StringComparison.Ordinal) == true).ToArray();
        var input = Assert.Single(inputs);
        Assert.StartsWith("sent@", input.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReassignedCapturedReferenceCellsDoNotEstablishAStableAlias()
    {
        var source = Source("void M() { var first = new Resource(); var second = first; first = new Resource(); Dispatcher.Send(() => { first.Dispose(); second.Dispose(); }); }");
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(source));
        var inputs = Analyze(source).SelectMany(result => result.Graph.Blocks).SelectMany(block => block.Instructions)
            .Where(instruction => instruction.Kind == InstructionKind.Allocate && instruction.Description?.StartsWith("Owned input", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(2, inputs.Length);
    }

    [Fact]
    public void StaticStorageNeverProducesCallerMoveOrOwnedCallbackInput()
    {
        var source = Source("static readonly Resource Shared = new Resource(); void M() { Dispatcher.Send(() => Shared.Read()); Shared.Read(); }");
        var results = Analyze(source);
        Assert.All(results, result => Assert.Empty(result.Issues));
        Assert.DoesNotContain(results.SelectMany(result => result.Graph.Blocks).SelectMany(block => block.Instructions), instruction =>
            instruction.Kind == InstructionKind.Move && instruction.Description?.Contains("TransfersToThread", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(results.SelectMany(result => result.Graph.Blocks).SelectMany(block => block.Instructions), instruction =>
            instruction.Kind == InstructionKind.Allocate && instruction.Ownership == ResourceOwnership.Owned && instruction.Target?.StartsWith("static:", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task InheritedTransferBoundaryUsesParameterIdentity()
    {
        var source = Prelude + "interface IQueue { void Queue([TransfersToThread] Action original); } sealed class QueueImpl : IQueue { public void Queue(Action renamed) { } } class Example { void M(QueueImpl queue) { var r = new Resource(); queue.Queue(() => r.Dispose()); } }";
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task CallbackCaptureBudgetExhaustionIsAnExplicitIncompleteAnalysisDiagnostic()
    {
        var body = string.Concat(Enumerable.Repeat("r.Read();", 7500));
        var source = Source("void M() { var r = new Resource(); Dispatcher.Send(() => { " + body + " }); }");
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0015" &&
            diagnostic.GetMessage().Contains("Callback ownership", StringComparison.Ordinal));
    }

    private static AnalysisResult[] Analyze(string source)
    {
        var compilation = AnalyzerTestHost.CreateCompilation(source);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return OwnershipCompilationAnalysis.Analyze(compilation, new(captureSnapshots: true)).ToArray();
    }

    private static string Format(IEnumerable<Diagnostic> diagnostics) => string.Join(Environment.NewLine, diagnostics.Select(diagnostic =>
        diagnostic.Id + " " + diagnostic.Location.SourceSpan + " " + diagnostic.GetMessage()));

    private static (string Source, TextSpan Span) ExtractMarker(string marked)
    {
        var start = marked.IndexOf("[|", StringComparison.Ordinal);
        var end = marked.IndexOf("|]", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return (marked.Remove(end, 2).Remove(start, 2), new TextSpan(start, end - start - 2));
    }
}
