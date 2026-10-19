using Microsoft.CodeAnalysis;
using SafeSharp.Analyzers;
using SafeSharp.Core;

namespace SafeSharp.Tests.Flow;

public sealed class ExceptionFlowTests
{
    private const string Prelude = """
        using System;
        using SafeSharp;
        sealed class Resource : IDisposable
        {
            public void Read() { }
            public void Dispose() { }
        }
        """;

    private static string Source(string members) => Prelude + "class Example { " + members + " }";

    public static TheoryData<string> CorrectExceptionalLifetimes => new()
    {
        "void M() { var r = new Resource(); try { throw new InvalidOperationException(); } catch { r.Dispose(); } }",
        "void M(Exception ex) { var r = new Resource(); try { throw ex; } finally { r.Dispose(); } }",
        "void M(InvalidOperationException ex) { var r = new Resource(); try { throw ex; } catch(ArgumentException) { r.Read(); } catch(InvalidOperationException) { r.Dispose(); } }",
        "void M(Exception ex) { var r = new Resource(); try { throw ex; } catch(Exception) when(false) { r.Read(); } catch { r.Dispose(); } }",
        "bool Filter(Resource r) { r.Read(); return true; } void M(Exception ex) { var r = new Resource(); try { throw ex; } catch(Exception) when(Filter(r)) { r.Dispose(); } catch { r.Dispose(); } }",
        "Resource M(Exception ex) { var r = new Resource(); try { try { return r; } finally { throw ex; } } catch { return r; } }",
        "Resource M(Exception ex) { var r = new Resource(); try { return r; } finally { try { throw ex; } catch { } } }",
        "void MayThrow() { } void M() { var r = new Resource(); try { MayThrow(); } catch { r.Read(); } r.Dispose(); }",
        "void M() { Resource? r = null; try { r = new Resource(); } catch { } r?.Dispose(); }",
        "void M(bool stop) { for(int i = 0; i < 3; i++) { using var r = new Resource(); if(stop) break; r.Read(); } }",
        "void M(bool skip) { for(int i = 0; i < 3; i++) { using var r = new Resource(); if(skip) continue; r.Read(); } }",
        "void M(Exception ex) { try { using var r = new Resource(); throw ex; } catch { } }",
        "Resource M(Exception ex) { try { throw ex; } catch { return new Resource(); } }",
        "Resource M(Exception ex) { try { throw ex; } catch(Exception) when(false) { return new Resource(); } catch { return new Resource(); } }"
    };

    [Theory]
    [MemberData(nameof(CorrectExceptionalLifetimes))]
    public async Task AcceptsVerifiedExceptionalLifetimes(string members)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Source(members));
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics));
    }

    public static TheoryData<string, string> ExceptionalViolations => new()
    {
        { "void M(Exception ex) { var r = new Resource(); r.Dispose(); try { throw ex; } catch { r.Read(); } }", "SS0001" },
        { "void M(Exception ex) { var r = new Resource(); try { try { throw ex; } finally { r.Dispose(); } } catch { r.Read(); } }", "SS0001" },
        { "void M(InvalidOperationException ex) { var r = new Resource(); r.Dispose(); try { try { throw ex; } catch(InvalidOperationException) { throw; } catch(ArgumentException) { r.Dispose(); } } catch { r.Read(); } }", "SS0001" },
        { "void M(Exception ex) { var r = new Resource(); try { try { } finally { r.Dispose(); throw ex; } } catch { r.Read(); } }", "SS0001" },
        { "void MayThrow() { } void M() { var r = new Resource(); r.Dispose(); try { MayThrow(); } catch { r.Read(); } }", "SS0001" },
        { "void M(Exception ex) { var r = new Resource(); try { throw ex; } catch { } }", "SS0003" },
        { "void MayThrow() { } void M() { var r = new Resource(); try { MayThrow(); r.Dispose(); } catch { } }", "SS0003" },
        { "Resource M(Exception ex) { using var r = new Resource(); try { throw ex; } catch { return r; } }", "SS0001" },
        { "void M(InvalidOperationException ex) { var r = new Resource(); try { throw ex; } catch(ArgumentException) { r.Dispose(); } }", "SS0003" }
    };

    [Theory]
    [MemberData(nameof(ExceptionalViolations))]
    public async Task ReportsExceptionalPathViolation(string members, string id)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Source(members));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == id);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "AD0001" or "SS0015");
    }

    [Fact]
    public async Task FiltersObserveStateBeforeInnerFinallyUnwinds()
    {
        var source = Source("bool Filter(Resource r) { r.Read(); return true; } void M(Exception ex) { var r = new Resource(); try { try { throw ex; } finally { r.Dispose(); } } catch(Exception) when(Filter(r)) { } }");
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var result = AnalyzeMethod(source);
        Assert.Contains(result.Graph.Blocks.SelectMany(block => block.Instructions), instruction => instruction.Kind == InstructionKind.Dispose);
        var filterStart = source.LastIndexOf("Filter(r)", StringComparison.Ordinal);
        var filterReads = result.Blocks.SelectMany(block => block.Steps).Where(step => step.Instruction.Kind == InstructionKind.Read
            && step.Instruction.Site.Start >= filterStart && step.Instruction.Site.Start < filterStart + "Filter(r)".Length).ToArray();
        Assert.Contains(filterReads, step => step.Before.Resources.Any(resource => resource.Ownership == ResourceOwnership.Owned && resource.Status == ResourceStatus.Alive));
        // If Dispose itself throws, the outer filter is evaluated again after the disposal invocation.
        Assert.Contains(filterReads, step => step.Before.Resources.Any(resource => resource.Ownership == ResourceOwnership.Owned && resource.Status == ResourceStatus.Disposed));
        var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0001");
        Assert.Equal("Possible", diagnostic.Properties["SafeSharp.Certainty"]);
        Assert.False(result.BudgetExceeded);
    }

    [Fact]
    public async Task ExceptionDuringFinallyDoesNotExecuteSameFinallyTwice()
    {
        var source = Source("void M(Exception ex) { var r = new Resource(); try { try { } finally { r.Dispose(); throw ex; } } catch { r.Read(); } }");
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0002");
    }

    [Fact]
    public async Task FailedDisposalStillDischargesInvocationObligation()
    {
        var source = Source("void M() { var r = new Resource(); try { r.Dispose(); } catch { r.Read(); } }");
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Fact]
    public void ImplicitUnhandledExceptionsHaveExplicitNonNormalBoundary()
    {
        var result = AnalyzeMethod(Source("void MayThrow() { } void M() { using var r = new Resource(); MayThrow(); }"));
        Assert.Empty(result.Issues);
        Assert.Contains(result.Graph.Blocks, block => block.Successors.Contains(block.Id)
            && block.Instructions.Any(instruction => instruction.Description?.Contains("implicit exception boundary", StringComparison.Ordinal) == true));
    }

    [Fact]
    public void NullCheckedUsingHasExplicitPathAssumptions()
    {
        var result = AnalyzeMethod(Source("void M() { using var r = new Resource(); r.Read(); }"));
        Assert.Empty(result.Issues);
        var instructions = result.Graph.Blocks.SelectMany(block => block.Instructions).ToArray();
        Assert.Contains(instructions, instruction => instruction.Kind == InstructionKind.AssumeNull);
        Assert.Contains(instructions, instruction => instruction.Kind == InstructionKind.AssumeNotNull);
    }

    [Fact]
    public async Task ReturnAfterFinallyDiagnosticPointsToReturnedExpression()
    {
        var source = Source("Resource M() { using var r = new Resource(); return r; }");
        var diagnostic = Assert.Single(await AnalyzerTestHost.GetDiagnosticsAsync(source), diagnostic => diagnostic.Id == "SS0001");
        Assert.Equal("r", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        Assert.Equal(source.LastIndexOf("return r", StringComparison.Ordinal) + "return ".Length, diagnostic.Location.SourceSpan.Start);
        Assert.NotEmpty(diagnostic.AdditionalLocations);
    }

    [Fact]
    public async Task DistinctReturnsThroughSharedFinallyRetainDistinctDiagnosticSpans()
    {
        var source = Source("Resource M(bool condition) { var first = new Resource(); var second = new Resource(); try { if(condition) return first; return second; } finally { first.Dispose(); second.Dispose(); } }");
        var diagnostics = (await AnalyzerTestHost.GetDiagnosticsAsync(source)).Where(diagnostic => diagnostic.Id == "SS0001").ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.Equal(new[] { "first", "second" }, diagnostics.Select(diagnostic => source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length)).OrderBy(value => value).ToArray());
    }

    private static AnalysisResult AnalyzeMethod(string source)
    {
        var compilation = AnalyzerTestHost.CreateCompilation(source);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return Assert.Single(OwnershipCompilationAnalysis.Analyze(compilation, new(captureSnapshots: true)), result => result.Graph.Name.StartsWith("Example.M(", StringComparison.Ordinal));
    }
}
