using Microsoft.CodeAnalysis;
using SafeSharp.Analyzers;
using SafeSharp.Core;

namespace SafeSharp.Tests.Flow;

public sealed class OwnershipAnalyzerTests
{
    private const string Prelude = """
        using System;
        using System.IO;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable
        {
            public int Value;
            [Mutates] public void Mutate() { Value++; }
            public void Read() { }
            public void Dispose() { }
        }
        """;

    public static TheoryData<string, string> Violations => new()
    {
        { "void M() { var r = new Resource(); r.Dispose(); r.Read(); }", "SS0001" },
        { "void M() { var r = new Resource(); var a = r; a.Dispose(); r.Read(); }", "SS0001" },
        { "void M() { var r = new Resource(); r.Dispose(); r.Dispose(); }", "SS0002" },
        { "void M() { var r = new Resource(); r.Read(); }", "SS0003" },
        { "void M() { new Resource(); }", "SS0003" },
        { "void M() { _ = new Resource(); }", "SS0003" },
        { "void M(bool condition) { var r = new Resource(); if (condition) r.Dispose(); }", "SS0003" },
        { "void M(bool condition) { var r = new Resource(); if (condition) r.Dispose(); r.Read(); }", "SS0001" },
        { "void M(bool condition) { var r = new Resource(); if (condition) r.Dispose(); else r.Read(); r.Dispose(); }", "SS0002" },
        { "void M() { var r = new Resource(); var s = Ownership.Move(r); r.Read(); s.Dispose(); }", "SS0004" },
        { "void M() { var r = new Resource(); var a = r; var s = Ownership.Move(r); a.Read(); s.Dispose(); }", "SS0004" },
        { "void M() { var r = new Resource(); Consume(r); r.Read(); } void Consume([Consumes] Resource r) { r.Dispose(); }", "SS0004" },
        { "void M() { var r = new Resource(); var b = Ownership.Borrow(r); var s = Ownership.Move(r); b.Read(); s.Dispose(); }", "SS0005" },
        { "void M() { var r = new Resource(); var a = Ownership.BorrowMut(r); var b = Ownership.Borrow(r); a.Read(); b.Read(); r.Dispose(); }", "SS0006" },
        { "void M() { var r = new Resource(); var a = Ownership.Borrow(r); var b = Ownership.BorrowMut(r); a.Read(); b.Read(); r.Dispose(); }", "SS0006" },
        { "void M() { var r = new Resource(); var b = Ownership.Borrow(r); r.Mutate(); b.Read(); r.Dispose(); }", "SS0007" },
        { "void M() { var r = new Resource(); var b = Ownership.Borrow(r); r.Value = 3; b.Read(); r.Dispose(); }", "SS0007" },
        { "Resource M() { var r = new Resource(); return Ownership.Borrow(r); }", "SS0008" },
        { "Resource? field; void M() { var r = new Resource(); field = Ownership.Borrow(r); r.Dispose(); }", "SS0008" },
        { "void M([Borrowed] Resource r) { r.Dispose(); }", "SS0009" },
        { "void M([Borrowed] Resource r) { r.Mutate(); }", "SS0007" },
        { "void M() { var r = new Resource(); var b = Ownership.Borrow(r); b.Dispose(); }", "SS0009" },
        { "async Task M() { var r = new Resource(); var b = Ownership.Borrow(r); await Task.Yield(); b.Read(); r.Dispose(); }", "SS0010" },
        { "void M() { var r = new Resource(); r = new Resource(); r.Dispose(); }", "SS0011" },
        { "void M() { var r = new Resource(); var b = Ownership.BorrowMut(r); r.Read(); b.Read(); r.Dispose(); }", "SS0013" },
        { "void M() { var r = new Resource(); var b = Ownership.Borrow(r); r.Dispose(); b.Read(); }", "SS0014" },
        { "Resource M() { using var r = new Resource(); return r; }", "SS0001" },
        { "Resource M() { var r = new Resource(); try { return r; } finally { r.Dispose(); } }", "SS0001" },
        { "void M() { var r = Create(); r.Read(); } Resource Create() => new Resource();", "SS0003" },
        { "void M() { var r = new Resource(); Release(r); r.Read(); } void Release(Resource r) { r.Dispose(); }", "SS0001" },
        { "void M() { var r = new Resource(); F(r, r); r.Dispose(); } void F([BorrowedMut] Resource a, [Borrowed] Resource b) { a.Read(); b.Read(); }", "SS0006" },
        { "void M() { var r = File.OpenRead(\"data\"); }", "SS0003" },
        { "void M() { void Local() { var r = new Resource(); } Local(); }", "SS0003" },
        { "void M() { Action f = () => { var r = new Resource(); }; f(); }", "SS0003" },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public async Task ReportsSemanticViolation(string members, string expected)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "class Example { " + members + " }");
        if (!diagnostics.Any(d => d.Id == expected))
        {
            var results = OwnershipCompilationAnalysis.Analyze(AnalyzerTestHost.CreateCompilation(Prelude + "class Example { " + members + " }"), new AnalysisOptions(captureSnapshots: true));
            var text = new System.Text.StringBuilder("Expected " + expected);
            foreach (var result in results)
            {
                foreach (var block in result.Graph.Blocks)
                {
                    text.AppendLine(result.Graph.Name + " B" + block.Id + " -> " + string.Join(",", block.Successors));
                    foreach (var instruction in block.Instructions)
                        text.AppendLine(instruction.Kind + " target=" + instruction.Target + " source=" + instruction.Source + " id=" + instruction.ResourceId);
                }
                foreach (var block in result.Blocks)
                    foreach (var step in block.Steps)
                        text.AppendLine(step.Instruction.Kind + " => " + string.Join(",", step.After.Places.Select(p => p.Place + ":" + p.References.Length)));
            }
            Assert.Fail(text.ToString());
        }
        var issue = diagnostics.First(d => d.Id == expected);
        Assert.True(issue.Location.IsInSource);
        Assert.True(issue.Location.SourceSpan.Length > 0);
        Assert.True(issue.Properties.ContainsKey("SafeSharp.Certainty"));
        Assert.False(string.IsNullOrWhiteSpace(issue.GetMessage()));
    }

    public static TheoryData<string> Accepted => new()
    {
        "void M() { using var r = new Resource(); r.Read(); }",
        "void M() { using (var r = new Resource()) { r.Read(); } }",
        "void M() { var r = new Resource(); r.Dispose(); }",
        "void M() { var r = new Resource(); var a = r; a.Dispose(); }",
        "void M(bool c) { var r = new Resource(); if (c) r.Dispose(); else r.Dispose(); }",
        "void M(bool c) { using var r = new Resource(); if (c) return; r.Read(); }",
        "void M(bool c) { var r = new Resource(); try { if (c) return; r.Read(); } finally { r.Dispose(); } }",
        "Resource M() { return new Resource(); }",
        "Resource M() { var r = new Resource(); return r; }",
        "Resource M(bool c) { if(c) return new Resource(); return new Resource(); }",
        "void M() { var r = new Resource(); var s = Ownership.Move(r); s.Read(); s.Dispose(); }",
        "void M() { var r = new Resource(); var b = Ownership.Borrow(r); b.Read(); r.Mutate(); r.Dispose(); }",
        "void M() { var r = new Resource(); var b = Ownership.BorrowMut(r); b.Mutate(); r.Read(); r.Dispose(); }",
        "void M() { var r = new Resource(); var a = Ownership.BorrowMut(r); a.Read(); var b = Ownership.Borrow(r); b.Read(); r.Dispose(); }",
        "void M() { var r = new Resource(); var a = Ownership.Borrow(r); var b = Ownership.Borrow(r); a.Read(); b.Read(); r.Dispose(); }",
        "void M([Borrowed] Resource r) { r.Read(); }",
        "void M([BorrowedMut] Resource r) { r.Mutate(); }",
        "void M([Consumes] Resource r) { r.Dispose(); }",
        "void M() { using var r = File.OpenRead(\"data\"); r.ReadByte(); }",
        "void M(Resource r) { r.Dispose(); if (r == null) return; }",
        "void M() { using var first = new Resource(); using var second = new Resource(); first.Read(); second.Read(); }",
        "void M() { for(int i = 0; i < 3; i++) { using var r = new Resource(); r.Read(); } }",
        "void M() { void Local() { using var r = new Resource(); } Local(); }",
        "void M() { Action f = () => { using var r = new Resource(); }; f(); }",
        "[ReturnsBorrowed(\"r\")] Resource Identity([Borrowed] Resource r) => r; void M() { var r = new Resource(); var b = Identity(r); b.Read(); r.Dispose(); }",
        "Resource? field; void M() { field = new Resource(); }",
        "void M() { var r = new Resource(); r.Dispose(); r = new Resource(); r.Dispose(); }",
        "async Task M() { var r = new Resource(); var b = Ownership.Borrow(r); b.Read(); await Task.Yield(); r.Dispose(); }"
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task AcceptsCorrectLifetimes(string members)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "class Example { " + members + " }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ConditionalDisposalIsPossibleWithRelatedEvidence()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "class Example { void M(bool c) { var r = new Resource(); if(c) r.Dispose(); r.Read(); } }");
        var diagnostic = Assert.Single(diagnostics, d => d.Id == "SS0001");
        Assert.Equal("Possible", diagnostic.Properties["SafeSharp.Certainty"]);
        Assert.NotEmpty(diagnostic.AdditionalLocations);
        Assert.Contains("dispos", diagnostic.Properties["SafeSharp.Evidence"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportingUsesSameCoreGraphAndRetainsFinallyCleanup()
    {
        var compilation = AnalyzerTestHost.CreateCompilation(Prelude + "class Example { Resource M() { using var r = new Resource(); return r; } }");
        var results = OwnershipCompilationAnalysis.Analyze(compilation, new AnalysisOptions(captureSnapshots: true)).ToArray();
        var result = Assert.Single(results, r => r.Graph.Name.Contains("Example.M", StringComparison.Ordinal));
        Assert.Contains(result.Issues, i => i.Id == "SS0001");
        Assert.NotEmpty(result.Blocks);
        Assert.Contains(result.Graph.Blocks.SelectMany(b => b.Instructions), i => i.Kind == InstructionKind.Dispose);
    }

    [Fact]
    public void CancelledCompilationAnalysisHonorsCancellation()
    {
        var compilation = AnalyzerTestHost.CreateCompilation(Prelude + "class Example { void M() { using var r = new Resource(); } }");
        Assert.Throws<OperationCanceledException>(() => OwnershipCompilationAnalysis.Analyze(compilation, cancellationToken: new CancellationToken(true)).ToArray());
    }
}
