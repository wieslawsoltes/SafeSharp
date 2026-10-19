using SafeSharp.Analyzers;

namespace SafeSharp.Tests.Flow;

public sealed class LifetimeCallTests
{
    private const string Prelude = """
        using System;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } public void Read() { } [Mutates] public void Change() { } }
        """;

    private const string Choose = """
        [return: Lifetime("a")]
        Resource Choose([Lifetime("a")] Resource left, [Lifetime("a")] Resource right, bool condition = true) => condition ? left : right;
        """;

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    public async Task EitherLifetimeInputCanInvalidateTheReturnedLoan(string disposed)
    {
        var source = Prelude + "class Example { " + Choose +
            " void M() { var first = new Resource(); var second = new Resource(); var result = Choose(first, second); " +
            disposed + ".Dispose(); result.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, SafeSharpAnalyzerCatalog.Create().ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0014" && diagnostic.Location.SourceSpan.Start == source.IndexOf(disposed + ".Dispose()", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0040" or "SS0041");
    }

    [Fact]
    public async Task AnExplicitSourcePromiseCanNarrowARegionUnion()
    {
        var source = Prelude + """
            class Example {
                [ReturnsBorrowed("left")]
                [return: Lifetime("a")]
                Resource First([Lifetime("a")] Resource left, [Lifetime("a")] Resource right) => left;
                void M() {
                    var first = new Resource(); var second = new Resource();
                    var result = First(first, second); second.Dispose(); result.Read(); first.Dispose();
                }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, SafeSharpAnalyzerCatalog.Create().ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task AnOutlivesConstraintContributesAnotherPossibleInput()
    {
        var source = Prelude + """
            class Example {
                [Outlives("longer", "shorter")]
                [return: Lifetime("shorter")]
                Resource Choose([Lifetime("longer")] Resource first, [Lifetime("shorter")] Resource second, bool condition = true) => condition ? first : second;
                void M() {
                    var first = new Resource(); var second = new Resource(); var result = Choose(first, second);
                    first.Dispose(); result.Read(); second.Dispose();
                }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, SafeSharpAnalyzerCatalog.Create().ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0014");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0040" or "SS0041");
    }

    [Fact]
    public async Task RenamedImplementationParametersKeepTheInheritedSourceSet()
    {
        var source = Prelude + """
            interface IChoose {
                [return: Lifetime("a")]
                Resource Choose([Lifetime("a")] Resource left, [Lifetime("a")] Resource right);
            }
            sealed class Selector : IChoose { public Resource Choose(Resource one, Resource two) => one; }
            class Example {
                void M(Selector selector) {
                    var first = new Resource(); var second = new Resource(); var result = selector.Choose(first, second);
                    second.Dispose(); result.Read(); first.Dispose();
                }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, SafeSharpAnalyzerCatalog.Create().ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0014");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0040" or "SS0041" or "SS0042");
    }

    [Fact]
    public async Task IdenticalActualArgumentsDoNotCreateConflictingAlternativeLoans()
    {
        var source = Prelude + """
            class Example {
                [return: Lifetime("a"), BorrowedMut]
                Resource Choose([Lifetime("a")] Resource left, [Lifetime("a")] Resource right, bool condition = true) => condition ? left : right;
                void M() {
                    var owner = new Resource(); var alias = owner; var result = Choose(owner, alias);
                    result.Change(); owner.Dispose();
                }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, SafeSharpAnalyzerCatalog.Create().ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task TheUnionLoanEndsAtItsLastUse()
    {
        var source = Prelude + "class Example { " + Choose +
            " void M() { var first = new Resource(); var second = new Resource(); var result = Choose(first, second); result.Read(); first.Dispose(); second.Dispose(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, SafeSharpAnalyzerCatalog.Create().ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task AnInheritedGetterKeepsItsReceiverLifetimeAtTheCallSite()
    {
        var source = Prelude + """
            interface IView { Resource Value { [return: Lifetime("this")] get; } }
            sealed class View : IView, IDisposable {
                [Owned] readonly Resource value = new Resource();
                public Resource Value => value;
                public void Dispose() => value.Dispose();
            }
            class Example {
                void M() {
                    var owner = new View(); var result = owner.Value;
                    owner.Dispose(); result.Read();
                }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, SafeSharpAnalyzerCatalog.Create().ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0014");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0040" or "SS0041" or "SS0042");
    }

    [Fact]
    public async Task InheritedRegionRenamingAcrossSeveralOverridesKeepsEverySource()
    {
        var source = Prelude + """
            abstract class Base {
                [Outlives("a", "b")]
                [return: Lifetime("b")]
                public abstract Resource Choose([Lifetime("a")] Resource first, [Lifetime("b")] Resource second);
            }
            class Middle : Base {
                public override Resource Choose([Lifetime("x")] Resource one, [Lifetime("y")] Resource two) => one;
            }
            sealed class Selector : Middle {
                public override Resource Choose(Resource left, Resource right) => left;
            }
            class Example {
                void M(Selector selector) {
                    var first = new Resource(); var second = new Resource(); var result = selector.Choose(first, second);
                    second.Dispose(); result.Read(); first.Dispose();
                }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, SafeSharpAnalyzerCatalog.Create().ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0014");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0040" or "SS0041" or "SS0042");
    }

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    public async Task AnInheritedIndexerRetainsBothExplicitlyBorrowedInputs(string disposed)
    {
        var source = Prelude + """
            interface IChoose {
                Resource this[[Borrowed, Lifetime("a")] Resource left, [Borrowed, Lifetime("a")] Resource right] {
                    [return: Lifetime("a")] get;
                }
            }
            sealed class Selector : IChoose { public Resource this[Resource one, Resource two] => one; }
            class Example {
                void M(Selector selector) {
                    var first = new Resource(); var second = new Resource(); var result = selector[first, second];
            """ + disposed + ".Dispose(); result.Read(); " + (disposed == "first" ? "second" : "first") + ".Dispose(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, SafeSharpAnalyzerCatalog.Create().ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0014" && diagnostic.Location.SourceSpan.Start == source.IndexOf(disposed + ".Dispose()", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0001");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0040" or "SS0041" or "SS0042");
    }

    [Fact]
    public async Task AnIndexerLoanKeepsItsArgumentOriginsAfterTemporaryInputLoansEnd()
    {
        var source = Prelude + """
            sealed class Selector {
                public Resource this[[Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("a")] Resource second] {
                    [return: Lifetime("a")] get => first;
                }
            }
            class Example {
                void M(Selector selector) {
                    var first = new Resource(); var second = new Resource(); var result = selector[first, second];
                    result.Read(); first.Dispose(); second.Dispose();
                }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, SafeSharpAnalyzerCatalog.Create().ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task AnIndexerCreatesOneMutableLoanForIdenticalAlternativeArguments()
    {
        var source = Prelude + """
            sealed class Selector {
                public Resource this[[Lifetime("a")] Resource first, [Lifetime("a")] Resource second] {
                    [return: Lifetime("a"), BorrowedMut] get => first;
                }
            }
            class Example {
                void M(Selector selector) {
                    var owner = new Resource(); var alias = owner; var result = selector[owner, alias];
                    result.Change(); owner.Dispose();
                }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, SafeSharpAnalyzerCatalog.Create().ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }
}
