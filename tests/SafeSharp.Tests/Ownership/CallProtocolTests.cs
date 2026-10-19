using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SafeSharp.Analyzers;
using SafeSharp.Core;

namespace SafeSharp.Tests.Flow;

/// <summary>Checks call evaluation order and disposal completion through the real Roslyn adapter.</summary>
public sealed class CallProtocolTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable
        {
            public int Count => 1;
            public void Read() { }
            [Mutates] public void Mutate() { }
            [Mutates] public void Append(int count) { }
            [Mutates] public void Combine([Borrowed] Resource other) { }
            [Mutates, ReturnsBorrowed("this", true)] public Resource EditAndReturn(int count) => this;
            public void Dispose() { }
        }
        sealed class AsyncResource : IAsyncDisposable
        {
            public void Read() { }
            public ValueTask DisposeAsync() => default;
        }
        static class BorrowCalls
        {
            public static void Edit([BorrowedMut] Resource value, int count) { }
            public static void Both([BorrowedMut] Resource first, [BorrowedMut] Resource second) { }
            public static void Plain(Resource value, int count) { }
            public static int Count([Borrowed] Resource value) => value.Count;
            [ReturnsBorrowed("value", true)] public static Resource MutableView([BorrowedMut] Resource value) => value;
            [ReturnsBorrowed("value")] public static Resource SharedView([BorrowedMut] Resource value) => value;
        }
        """;

    private static string Source(string members) => Prelude + "\nclass Example\n{\n" + members + "\n}\n";

    public static TheoryData<string> AcceptedBorrows => new()
    {
        "void M() { using var r = new Resource(); r.Append(r.Count); }",
        "void M() { using var r = new Resource(); r.Append(BorrowCalls.Count(Ownership.Borrow(r))); }",
        "void M() { using var r = new Resource(); var view = Ownership.Borrow(r); r.Append(BorrowCalls.Count(view)); }",
        "void M() { using var r = new Resource(); BorrowCalls.Edit(r, r.Count); }",
        "void M() { using var r = new Resource(); BorrowCalls.Edit(count: r.Count, value: r); }",
        "void M() { using var first = new Resource(); using var second = new Resource(); BorrowCalls.Both(first, second); }",
        "void M() { using var r = new Resource(); var view = BorrowCalls.MutableView(r); view.Mutate(); view.Read(); }",
        "void M() { using var r = new Resource(); var view = BorrowCalls.SharedView(r); view.Read(); r.Mutate(); }",
        "void M() { using var r = new Resource(); var view = r.EditAndReturn(r.Count); view.Mutate(); view.Read(); }",
        "void M([BorrowedMut] Resource value) { value.Append(value.Count); }",
        "void M() { using var r = new Resource(); var view = Ownership.BorrowMut(r); BorrowCalls.Edit(view, view.Count); }",
        "void M() { using var r = new Resource(); var view = Ownership.BorrowMut(r); view.Read(); r.Append(r.Count); }",
        "void M() { using var r = new Resource(); BorrowCalls.Plain(Ownership.Borrow(r), r.Count); }",
    };

    [Theory]
    [MemberData(nameof(AcceptedBorrows))]
    public async Task AcceptsTwoPhaseCallsAndReturnedReborrows(string members)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Source(members));
        Assert.True(diagnostics.IsEmpty, Format(diagnostics));
    }

    public static TheoryData<string, string> BorrowViolations => new()
    {
        { "void M() { using var r = new Resource(); [|r.Combine(r)|]; }", "SS0006" },
        { "void M() { using var r = new Resource(); [|r.Combine(r)|]; }", "SS0007" },
        { "void M() { using var r = new Resource(); var alias = r; BorrowCalls.Both(r, [|alias|]); }", "SS0006" },
        { "void M() { using var r = new Resource(); var view = Ownership.Borrow(r); [|view.Mutate()|]; }", "SS0007" },
        { "void M([Borrowed] Resource value) { [|value.Mutate()|]; }", "SS0007" },
        { "void M() { using var r = new Resource(); BorrowCalls.Edit(Ownership.BorrowMut(r), [|r.Count|]); }", "SS0013" },
        { "void M() { using var r = new Resource(); BorrowCalls.Plain(Ownership.BorrowMut(r), [|r.Count|]); }", "SS0013" },
        { "void M() { using var r = new Resource(); var exclusive = Ownership.BorrowMut(r); [|r.Read()|]; exclusive.Read(); }", "SS0013" },
        { "void M() { using var r = new Resource(); var view = BorrowCalls.MutableView(r); [|r.Read()|]; view.Read(); }", "SS0013" },
        { "void M() { using var r = new Resource(); var view = BorrowCalls.SharedView(r); [|view.Mutate()|]; }", "SS0007" },
        { "void M() { var r = new Resource(); var view = r.EditAndReturn(r.Count); [|r.Dispose()|]; view.Read(); }", "SS0014" },
    };

    [Theory]
    [MemberData(nameof(BorrowViolations))]
    public async Task ReportsCallConflictAtTheActualOperation(string members, string id)
    {
        var (source, span) = ExtractMarker(Source(members));
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        AssertDiagnosticAt(diagnostics, id, span);
    }

    [Fact]
    public void MutableReceiverHasReservedAndActiveSnapshotsAroundReadArguments()
    {
        var (source, span) = ExtractMarker(Source("void M() { using var r = new Resource(); [|r.Append(r.Count)|]; }"));
        var result = AnalyzeMethod(source);
        Assert.Empty(result.Issues);
        var steps = result.Blocks.SelectMany(block => block.Steps).Where(step => At(step.Instruction.Site, span)).ToArray();
        var reserved = Assert.Single(steps, step => step.Instruction.Kind == InstructionKind.ReserveExclusiveBorrow);
        var activated = Assert.Single(steps, step => step.Instruction.Kind == InstructionKind.ActivateBorrow);
        var loan = Assert.Single(reserved.After.Loans);
        Assert.Equal(BorrowPhase.Reserved, loan.Phase);
        var active = Assert.Single(activated.After.Loans);
        Assert.Equal(loan.Id, active.Id);
        Assert.Equal(BorrowPhase.Active, active.Phase);
        Assert.Contains(active.ActivationSites, site => At(site, span));
        var countStart = source.LastIndexOf("r.Count", StringComparison.Ordinal);
        var countRead = Assert.Single(result.Blocks.SelectMany(block => block.Steps), step =>
            step.Instruction.Kind == InstructionKind.Read && step.Instruction.Site.Start == countStart);
        Assert.Equal(BorrowPhase.Reserved, Assert.Single(countRead.Before.Loans).Phase);
    }

    [Fact]
    public void ExplicitMutableBorrowIsActiveBeforeLaterArguments()
    {
        var (source, span) = ExtractMarker(Source("void M() { using var r = new Resource(); BorrowCalls.Plain([|Ownership.BorrowMut(r)|], r.Count); }"));
        var result = AnalyzeMethod(source);
        var borrow = Assert.Single(result.Blocks.SelectMany(block => block.Steps), step =>
            step.Instruction.Kind == InstructionKind.BorrowExclusive && At(step.Instruction.Site, span));
        Assert.Equal(BorrowPhase.Active, Assert.Single(borrow.After.Loans).Phase);
        Assert.DoesNotContain(result.Graph.Blocks.SelectMany(block => block.Instructions), instruction =>
            instruction.Kind == InstructionKind.ReserveExclusiveBorrow && At(instruction.Site, span));
        Assert.Contains(result.Issues, issue => issue.Id == "SS0013");
    }

    public static TheoryData<string> AcceptedCompletions => new()
    {
        "async Task M() { var r = new AsyncResource(); await r.DisposeAsync(); }",
        "async Task M() { var r = new AsyncResource(); var pending = r.DisposeAsync(); await pending; }",
        "async Task M() { var r = new AsyncResource(); var pending = r.DisposeAsync(); var alias = pending; await alias; }",
        "async Task M() { var r = new AsyncResource(); var pending = r.DisposeAsync(); var alias = pending; pending = default; await alias; }",
        "async Task M() { var r = new AsyncResource(); var configured = r.DisposeAsync().ConfigureAwait(false); await configured; }",
        "async Task M() { var r = new AsyncResource(); var pending = r.DisposeAsync(); await pending.ConfigureAwait(false); }",
        "async Task M() { var r = new AsyncResource(); var task = r.DisposeAsync().AsTask(); await task; }",
        "void M() { var r = new AsyncResource(); r.DisposeAsync().GetAwaiter().GetResult(); }",
        "void M() { var r = new AsyncResource(); var awaiter = r.DisposeAsync().GetAwaiter(); awaiter.GetResult(); }",
        "void M() { var r = new AsyncResource(); var task = r.DisposeAsync().AsTask(); task.Wait(); }",
        "void M() { var r = new AsyncResource(); r.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult(); }",
        "async Task M(bool first) { var r = new AsyncResource(); var pending = r.DisposeAsync(); if (first) await pending; else pending.GetAwaiter().GetResult(); }",
        "async Task M(bool first) { var r = new AsyncResource(); var pending = r.DisposeAsync(); var alias = first ? pending : pending; await alias; }",
        "ValueTask M() { var r = new AsyncResource(); return r.DisposeAsync(); }",
        "Task M() { var r = new AsyncResource(); var pending = r.DisposeAsync(); return pending.AsTask(); }",
        "async Task M() { var r = new AsyncResource(); try { var pending = r.DisposeAsync(); await pending; } catch { } }",
        "async Task M() { var r = new AsyncResource(); ValueTask pending; try { pending = r.DisposeAsync(); } catch { return; } await pending; }",
        "async Task M() { await using var r = new AsyncResource(); r.Read(); }",
    };

    [Theory]
    [MemberData(nameof(AcceptedCompletions))]
    public async Task ObservesOrTransfersAsyncCompletionAcrossSupportedAliases(string members)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Source(members));
        Assert.True(diagnostics.IsEmpty, Format(diagnostics));
    }

    public static TheoryData<string, string> CompletionViolations => new()
    {
        { "void M() { var r = new AsyncResource(); [|r.DisposeAsync()|]; }", "SS0017" },
        { "void M() { var r = new AsyncResource(); _ = [|r.DisposeAsync()|]; }", "SS0017" },
        { "void M() { var r = new AsyncResource(); _ = [|r.DisposeAsync()|].ConfigureAwait(false); }", "SS0017" },
        { "void M() { var r = new AsyncResource(); var pending = [|r.DisposeAsync()|]; }", "SS0017" },
        { "void M() { var r = new AsyncResource(); var pending = [|r.DisposeAsync()|]; var alias = pending; _ = alias.IsCompleted; }", "SS0017" },
        { "void M() { var r = new AsyncResource(); var pending = r.DisposeAsync(); [|pending = default|]; }", "SS0017" },
        { "void M() { var r = new AsyncResource(); var task = [|r.DisposeAsync()|].AsTask(); task.Dispose(); }", "SS0017" },
        { "void M() { var r = new AsyncResource(); var task = [|r.DisposeAsync()|].AsTask(); _ = task.Wait(0); }", "SS0017" },
        { "async Task M(bool complete) { var r = new AsyncResource(); var pending = [|r.DisposeAsync()|]; if (complete) await pending; }", "SS0017" },
        { "async Task M() { var r = new AsyncResource(); var pending = r.DisposeAsync(); [|r.Read()|]; await pending; }", "SS0001" },
    };

    [Theory]
    [MemberData(nameof(CompletionViolations))]
    public async Task ReportsIgnoredOrLostCompletionAndInvocationLifecycle(string members, string id)
    {
        var (source, span) = ExtractMarker(Source(members));
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        AssertDiagnosticAt(diagnostics, id, span);
    }

    [Fact]
    public async Task ConditionalObservationRetainsPossibleCompletionObligation()
    {
        var (source, span) = ExtractMarker(Source("async Task M(bool observe) { var r = new AsyncResource(); var pending = [|r.DisposeAsync()|]; if (observe) await pending; }"));
        var diagnostic = AssertDiagnosticAt(await AnalyzerTestHost.GetDiagnosticsAsync(source), "SS0017", span);
        Assert.Equal("Possible", diagnostic.Properties["SafeSharp.Certainty"]);
        var result = AnalyzeMethod(source);
        Assert.Contains(result.Blocks.SelectMany(block => block.Entry.Resources), resource =>
            resource.Kind == ResourceKind.AsyncDisposalCompletion && resource.Status == (ResourceStatus.Pending | ResourceStatus.Completed));
    }

    [Fact]
    public async Task ObservingOneOfTwoIndependentCompletionsDoesNotDischargeBoth()
    {
        var source = Source("async Task M(bool first) { var left = new AsyncResource(); var right = new AsyncResource(); var a = left.DisposeAsync(); var b = right.DisposeAsync(); var selected = first ? a : b; await selected; }");
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var pending = diagnostics.Where(diagnostic => diagnostic.Id == "SS0017").ToArray();
        Assert.Equal(2, pending.Length);
        Assert.All(pending, diagnostic => Assert.Equal("Possible", diagnostic.Properties["SafeSharp.Certainty"]));
        Assert.Equal(new[] { "left.DisposeAsync()", "right.DisposeAsync()" }, pending.Select(diagnostic => source.Substring(
            diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length)).OrderBy(text => text).ToArray());
    }

    [Fact]
    public void SuccessfulInvocationCreatesCompletionOnlyAfterDisposalBoundary()
    {
        var result = AnalyzeMethod(Source("async Task M() { var r = new AsyncResource(); ValueTask pending; try { pending = r.DisposeAsync(); } catch { return; } await pending; }"));
        Assert.Empty(result.Issues);
        var steps = result.Blocks.SelectMany(block => block.Steps).ToArray();
        var begin = Assert.Single(steps, step => step.Instruction.Kind == InstructionKind.BeginAsyncDispose);
        Assert.True((begin.Instruction.Flags & InstructionFlags.DisposalAlreadyInvoked) != 0);
        Assert.DoesNotContain(begin.Before.Resources, resource => resource.Kind == ResourceKind.AsyncDisposalCompletion);
        Assert.Contains(begin.Before.Resources, resource => resource.Ownership == ResourceOwnership.Owned && resource.Status == ResourceStatus.Disposed);
        var pending = Assert.Single(begin.After.Resources, resource => resource.Kind == ResourceKind.AsyncDisposalCompletion);
        Assert.Equal(ResourceStatus.Pending, pending.Status);
        Assert.Single(pending.DisposedResourceIds);
        var complete = Assert.Single(steps, step => step.Instruction.Kind == InstructionKind.CompleteAsyncDispose);
        Assert.Equal(ResourceStatus.Pending, Assert.Single(complete.Before.Resources, resource => resource.Kind == ResourceKind.AsyncDisposalCompletion).Status);
        Assert.Equal(ResourceStatus.Completed, Assert.Single(complete.After.Resources, resource => resource.Kind == ResourceKind.AsyncDisposalCompletion).Status);
        Assert.Contains(result.Graph.Blocks.SelectMany(block => block.Instructions), instruction =>
            instruction.Kind == InstructionKind.Nop && (instruction.Flags & InstructionFlags.MayThrow) != 0 && instruction.Site.Equals(begin.Instruction.Site));
    }

    [Fact]
    public async Task UserMethodNamedGetResultDoesNotObserveFrameworkCompletion()
    {
        var (source, span) = ExtractMarker(Source("void GetResult(ValueTask pending) { } void M() { var r = new AsyncResource(); var pending = [|r.DisposeAsync()|]; GetResult(pending); }"));
        AssertDiagnosticAt(await AnalyzerTestHost.GetDiagnosticsAsync(source), "SS0017", span);
    }

    [Fact]
    public async Task UserDefinedConversionAcquiresItsOwnReturnedOwner()
    {
        var (source, span) = ExtractMarker(Prelude + """
            sealed class Convertible {
                public static explicit operator Resource(Convertible value) => new Resource();
            }
            class Example {
                void M() { var input = new Convertible(); var result = [|(Resource)input|]; }
            }
            """);
        AssertDiagnosticAt(await AnalyzerTestHost.GetDiagnosticsAsync(source), "SS0003", span);
    }

    [Fact]
    public async Task UserDefinedConversionDoesNotInventAnAliasOfItsOperand()
    {
        var source = Prelude + """
            sealed class Converted : IDisposable {
                public static Converted Existing = null!;
                public void Read() { }
                public void Dispose() { }
                public static explicit operator Converted(Resource value) => Existing;
            }
            class Example {
                void M() {
                    var input = new Resource(); var result = (Converted)input;
                    input.Dispose(); result.Read();
                }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start ==
            source.LastIndexOf("result.Read()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UserDefinedConversionAppliesItsInputTransferContract()
    {
        var (source, span) = ExtractMarker(Prelude + """
            sealed class Converted : IDisposable {
                public void Dispose() { }
                public static explicit operator Converted([Consumes] Resource value) {
                    value.Dispose(); return new Converted();
                }
            }
            class Example {
                void M() {
                    var input = new Resource(); using var result = (Converted)input; [|input.Read()|];
                }
            }
            """);
        AssertDiagnosticAt(await AnalyzerTestHost.GetDiagnosticsAsync(source), "SS0004", span);
    }

    [Theory]
    [InlineData("Close(first, second)")]
    [InlineData("Close(later: second, earlier: first)")]
    public async Task AnEarlierCleanupExceptionRetainsTheLaterInputObligation(string call)
    {
        var source = Source("void Close(Resource earlier, Resource later) { earlier.Dispose(); later.Dispose(); } " +
            "void M() { var first = new Resource(); var second = new Resource(); try { " + call + "; } catch { } }");
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var allocation = source.LastIndexOf("new Resource()", StringComparison.Ordinal);
        var leak = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == allocation);
        Assert.Equal("Possible", leak.Properties["SafeSharp.Certainty"]);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start != allocation);
    }

    [Fact]
    public async Task AllDirectCleanupInputsAreReleasedOnTheSuccessfulCallPath()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Source("void Close(Resource earlier, Resource later) { earlier.Dispose(); later.Dispose(); } " +
            "void M() { var first = new Resource(); var second = new Resource(); Close(later: second, earlier: first); }"));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task RepeatedCalleeDisposalIsNotCollapsedIntoOneEffect()
    {
        var (source, span) = ExtractMarker(Source("void Close(Resource value) { value.Dispose(); value.Dispose(); } " +
            "void M() { var value = new Resource(); Close([|value|]); }"));
        AssertDiagnosticAt(await AnalyzerTestHost.GetDiagnosticsAsync(source), "SS0002", span);
    }

    [Fact]
    public async Task AnUncontractedMemoryTypedGetterDoesNotInventAReceiverLoan()
    {
        var source = Prelude + """
            sealed class ViewProvider : IDisposable {
                public Memory<int> Independent => new int[4];
                public void Dispose() { }
            }
            class Example {
                void M() {
                    var provider = new ViewProvider(); var view = provider.Independent;
                    provider.Dispose(); _ = view.Length;
                }
            }
            """;
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(source));
    }

    [Theory]
    [InlineData("var")]
    [InlineData("System.Buffers.IMemoryOwner<int>")]
    public async Task TheActualMemoryOwnerPropertyRetainsItsReceiverLoan(string declaration)
    {
        var source = Prelude + """
            sealed class ViewProvider : System.Buffers.IMemoryOwner<int> {
                public Memory<int> Memory => new int[4];
                public void Dispose() { }
            }
            """ + "class Example { void M() { " + declaration + " provider = new ViewProvider(); var view = provider.Memory; " +
            "provider.Dispose(); _ = view.Length; } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0014" && diagnostic.Location.SourceSpan.Start ==
            source.LastIndexOf("provider.Dispose()", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start ==
            source.LastIndexOf("view.Length", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHidingMemoryPropertyDoesNotInheritTheInterfaceImplementationLoan()
    {
        var source = Prelude + """
            class Owner : System.Buffers.IMemoryOwner<int> {
                public Memory<int> Memory => new int[4];
                public void Dispose() { }
            }
            sealed class ViewProvider : Owner { public new Memory<int> Memory => new int[4]; }
            class Example {
                void M() {
                    var provider = new ViewProvider(); var view = provider.Memory;
                    provider.Dispose(); _ = view.Length;
                }
            }
            """;
        Assert.Empty(await AnalyzerTestHost.GetDiagnosticsAsync(source));
    }

    private static AnalysisResult AnalyzeMethod(string source)
    {
        var compilation = AnalyzerTestHost.CreateCompilation(source);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return Assert.Single(OwnershipCompilationAnalysis.Analyze(compilation, new(captureSnapshots: true)), result =>
            result.Graph.Name.StartsWith("Example.M(", StringComparison.Ordinal));
    }

    private static Diagnostic AssertDiagnosticAt(IEnumerable<Diagnostic> diagnostics, string id, TextSpan span)
    {
        var items = diagnostics.ToArray();
        var matching = items.Where(diagnostic => diagnostic.Id == id && diagnostic.Location.SourceSpan == span).ToArray();
        Assert.True(matching.Length == 1, "Expected " + id + " at " + span + Environment.NewLine + Format(items));
        var diagnostic = matching[0];
        Assert.Contains(diagnostic.Properties["SafeSharp.Certainty"], new[] { "Definite", "Possible" });
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Properties["SafeSharp.Evidence"]));
        Assert.DoesNotContain(items, item => item.Id is "SS0015" or "AD0001");
        return diagnostic;
    }

    private static bool At(SourceSite site, TextSpan span) => site.Start == span.Start && site.Length == span.Length;
    private static string Format(IEnumerable<Diagnostic> diagnostics) => string.Join(Environment.NewLine,
        diagnostics.Select(diagnostic => diagnostic.Id + " " + diagnostic.Location.SourceSpan + " " + diagnostic.GetMessage()));

    private static (string Source, TextSpan Span) ExtractMarker(string marked)
    {
        var start = marked.IndexOf("[|", StringComparison.Ordinal);
        var end = marked.IndexOf("|]", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "A fixture must contain one nonempty diagnostic span marker.");
        return (marked.Remove(end, 2).Remove(start, 2), new TextSpan(start, end - start - 2));
    }
}
