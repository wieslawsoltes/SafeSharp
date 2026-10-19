using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Tests.LifetimeContracts;

public sealed class LifetimeContractAnalyzerTests
{
    private const string Prelude = """
        using System;
        using SafeSharp;
        sealed class Resource : IDisposable
        {
            public void Dispose() { }
            public void Read() { }
        }
        """;

    public static TheoryData<string, string> Violations => new()
    {
        { "class Example { void M([[|Lifetime(\"\")|]] Resource value) { } }", "SS0040" },
        { "class Example { void M([[|Lifetime(\"not valid\")|]] Resource value) { } }", "SS0040" },
        { "class Example { [[|Outlives(\"a\", \"missing\")|]] void M([Lifetime(\"a\")] Resource value) { } }", "SS0040" },
        { "class Example { static void M([[|Lifetime(\"this\")|]] Resource value) { } }", "SS0040" },
        { "class Example { [return: [|Lifetime(\"a\")|]] void M([Lifetime(\"a\")] Resource value) { } }", "SS0040" },
        { "class Example { [ReturnsOwned] [return: [|Lifetime(\"a\")|]] Resource M([Lifetime(\"a\")] Resource value) => new Resource(); }", "SS0040" },
        { "class Example { void M([Consumes, [|Lifetime(\"a\")|]] Resource value) { } }", "SS0040" },
        { "class Example { [return: Lifetime(\"a\"), Borrowed, [|BorrowedMut|]] Resource M([Lifetime(\"a\")] Resource value) => value; }", "SS0040" },
        { "class Example { [ReturnsBorrowed(\"value\", true)] [return: [|Borrowed|]] Resource M(Resource value) => value; }", "SS0040" },
        { "class Example { [ReturnsBorrowed(\"value\")] [return: [|BorrowedMut|]] Resource M(Resource value) => value; }", "SS0040" },
        { "class Example { [return: Lifetime(\"b\")] Resource M([Lifetime(\"a\")] Resource value) => [|value|]; }", "SS0041" },
        { "class Example { [Outlives(\"b\", \"a\")] [return: Lifetime(\"b\")] Resource M([Lifetime(\"a\")] Resource value) => [|value|]; }", "SS0041" },
        { "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource value) => [|new Resource()|]; }", "SS0041" },
        { "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource value) { var local = new Resource(); var alias = local; return [|alias|]; } }", "SS0041" },
        { "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second, bool choose) { var alias = first; if (choose) alias = second; return [|alias|]; } }", "SS0041" },
        { "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second, bool choose) => [|choose ? first : second|]; }", "SS0041" },
        { "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second) { var alias = first; alias = second; return [|alias|]; } }", "SS0041" },
        { "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second) { ref Resource alias = ref first; alias = second; return [|first|]; } }", "SS0041" },
        { "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second) { ref Resource alias = ref first; alias = ref second; return [|alias|]; } }", "SS0041" },
        { "class Example { [ReturnsBorrowed(\"first\")] Resource M(Resource first, Resource second) => [|second|]; }", "SS0041" },
        { "class Example { [ReturnsBorrowed(\"source\")] Resource Identity(Resource source) => source; [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input) => [|Identity(new Resource())|]; }", "SS0041" },
        { "interface IFactory { Resource Get(); } class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input, IFactory factory) => [|factory.Get()|]; }", "SS0041" },
        { "interface IRewrite { [ReturnsBorrowed(\"value\")] Resource Rewrite(ref Resource value); } class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input, IRewrite rewrite) => [|rewrite.Rewrite(ref input)|]; }", "SS0041" },
        { "class Change { public Change(ref Resource value) { } } class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input) { _ = new Change(ref input); return [|input|]; } }", "SS0041" },
        { "interface IRewrite { Resource Rewrite(ref Resource input); } class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input, IRewrite rewrite) { _ = new[] { rewrite.Rewrite(ref input) }; return [|input|]; } }", "SS0041" },
        { "class UnknownOwner { public Memory<int> Memory => new int[1]; } class Example { [return: Lifetime(\"a\")] Memory<int> M([Lifetime(\"a\")] UnknownOwner owner) => [|owner.Memory|]; }", "SS0041" },
        { "class Example { void Outer() { [return: Lifetime(\"a\")] static Resource Nested([Lifetime(\"a\")] Resource input) => [|new Resource()|]; } }", "SS0041" },
        { "class Example { void Outer(Resource captured) { [return: Lifetime(\"a\")] Resource Nested([Lifetime(\"a\")] Resource input) => [|captured|]; } }", "SS0041" },
        { "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input) { try { input = new Resource(); throw new InvalidOperationException(); } catch (Exception) { return [|input|]; } } }", "SS0041" },
        { "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input) { ref Resource alias = ref input; try { alias = new Resource(); throw new InvalidOperationException(); } catch (Exception) { return [|input|]; } } }", "SS0041" },
        { "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input) { void Replace() { input = new Resource(); } Replace(); return [|input|]; } }", "SS0041" },
        { "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input) { Action replace = () => input = new Resource(); replace(); return [|input|]; } }", "SS0041" },
        { "class Example { void Invoke(Action action) => action(); [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input) { Invoke(() => input = new Resource()); return [|input|]; } }", "SS0041" },
        { "class Token { public static Token operator +(Token left, Token right) => new(); } class Example { [return: Lifetime(\"a\")] Token M([Lifetime(\"a\")] Token first, Token second) { first += second; return [|first|]; } }", "SS0041" },
        { "class Token { public static Token operator ++(Token value) => new(); } class Example { [return: Lifetime(\"a\")] Token M([Lifetime(\"a\")] Token input) { input++; return [|input|]; } }", "SS0041" },
        { "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second) { (first, second) = (second, first); return [|first|]; } }", "SS0041" },
        { "class Example { Resource Choose(Resource first, Resource second, bool choose) => choose ? first : second; [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second, bool choose) => [|Choose(first, second, choose)|]; }", "SS0041" },
        { "interface IChoice { [return: Lifetime(\"x\")] Resource Choose([Lifetime(\"x\")] Resource first, [Lifetime(\"x\")] Resource second); } class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second, IChoice choice) => [|choice.Choose(first, second)|]; }", "SS0041" },
        { "interface IReader { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second); } class Example : IReader { public Resource M(Resource renamed, Resource other) => [|other|]; }", "SS0041" },
        { "interface IReader { [ReturnsBorrowed(\"first\")] Resource M(Resource first, Resource second); } class Example : IReader { [ReturnsBorrowed(\"second\")] public Resource [|M|](Resource first, Resource second) => second; }", "SS0042" },
        { "interface IFactory { [ReturnsOwned] Resource M(); } class Example : IFactory { [ReturnsBorrowed(\"this\")] public Resource [|M|]() => throw new InvalidOperationException(); }", "SS0042" },
        { "interface IReader { [ReturnsBorrowed(\"source\")] Resource M(Resource source); } class Example : IReader { [ReturnsOwned] public Resource [|M|](Resource source) => new Resource(); }", "SS0042" },
        { "interface IReader { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second); } class Example : IReader { [return: Lifetime(\"b\")] public Resource [|M|](Resource first, Resource second) => throw new InvalidOperationException(); }", "SS0042" },
        { "interface IReader { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second); } class Example : IReader { [Outlives(\"a\", \"b\")] public Resource [|M|](Resource first, Resource second) => first; }", "SS0042" },
        { "interface IReader { [return: Lifetime(\"a\"), BorrowedMut] Resource M([Lifetime(\"a\")] Resource source); } class Example : IReader { [return: Borrowed] public Resource [|M|](Resource source) => source; }", "SS0042" },
        { "interface IReader { [return: Lifetime(\"a\"), Borrowed] Resource M([Lifetime(\"a\")] Resource source); } class Example : IReader { [return: BorrowedMut] public Resource [|M|](Resource source) => source; }", "SS0042" },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public async Task ReportsInvalidPromisesAtTheExactSourceSpan(string body, string id)
    {
        var (source, span) = ExtractMarker(Prelude + body);
        var diagnostics = await AnalyzeAsync(source);
        var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Id == id);
        Assert.Equal(span, diagnostic.Location.SourceSpan);
        Assert.EndsWith("#" + id.ToLowerInvariant(), diagnostic.Descriptor.HelpLinkUri, StringComparison.Ordinal);
        if (id == "SS0041")
        {
            Assert.True(diagnostic.Properties.ContainsKey("SafeSharp.RequiredRegion"));
            Assert.True(diagnostic.Properties.ContainsKey("SafeSharp.SourceRegions"));
            Assert.NotEmpty(diagnostic.AdditionalLocations);
        }
        if (id == "SS0042") Assert.NotEmpty(diagnostic.AdditionalLocations);
    }

    public static TheoryData<string> Accepted => new()
    {
        "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource value) => value; }",
        "class Example { [Outlives(\"a\", \"b\")] [return: Lifetime(\"b\")] Resource M([Lifetime(\"a\")] Resource value) => value; }",
        "class Example { [Outlives(\"a\", \"b\"), Outlives(\"b\", \"c\")] [return: Lifetime(\"c\")] Resource M([Lifetime(\"a\")] Resource value, [Lifetime(\"b\")] Resource bridge) => value; }",
        "class Example { [Outlives(\"a\", \"b\"), Outlives(\"b\", \"a\")] [return: Lifetime(\"a\")] Resource M([Lifetime(\"b\")] Resource value) => value; }",
        "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource value) { var alias = value; var second = alias; return second; } }",
        "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first) { ref Resource alias = ref first; return alias; } }",
        "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second) { ref Resource alias = ref first; alias = ref second; return first; } }",
        "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"a\")] Resource second, bool choose) => choose ? first : second; }",
        "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second) { var alias = second; alias = first; return alias; } }",
        "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"a\")] Resource second, bool choose) { var alias = first; while (choose) { alias = second; choose = false; } return alias; } }",
        "class Example { [return: Lifetime(\"a\")] Resource? M([Lifetime(\"a\")] Resource? first, [Lifetime(\"a\")] Resource? second) => first ?? second; }",
        "class Example { [return: Lifetime(\"a\")] Resource? M([Lifetime(\"a\")] Resource value) => null; }",
        "class Example { [ReturnsBorrowed(\"value\")] Resource M(Resource value) { var alias = value; return alias; } }",
        "class Example { [ReturnsBorrowed(\"source\")] Resource Identity(Resource source) => source; [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input) => Identity(input); }",
        "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input) => Ownership.Borrow(input); }",
        "class Example { Resource Choose(Resource first, Resource second, bool choose) => choose ? first : second; [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"a\")] Resource second, bool choose) => Choose(first, second, choose); }",
        "interface IChoice { [return: Lifetime(\"x\")] Resource Choose([Lifetime(\"x\")] Resource first, [Lifetime(\"x\")] Resource second); } class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"a\")] Resource second, IChoice choice) => choice.Choose(first, second); }",
        "interface IReader { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource value); } class Example : IReader { public Resource M(Resource renamed) => renamed; }",
        "interface IReader { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource value); } class Example : IReader { [return: Lifetime(\"x\")] public Resource M([Lifetime(\"x\")] Resource renamed) => renamed; }",
        "abstract class Reader { [return: Lifetime(\"a\")] public abstract Resource M([Lifetime(\"a\")] Resource value); } class Example : Reader { public override Resource M(Resource renamed) => renamed; }",
        "interface IReader<T> { [return: Lifetime(\"a\")] T M([Lifetime(\"a\")] T value); } class Example : IReader<Resource> { public Resource M(Resource renamed) => renamed; }",
        "interface IReader { [ReturnsBorrowed(\"source\")] Resource M(Resource source); } class Example : IReader { public Resource M(Resource renamed) => renamed; }",
        "class Example { [Owned] Resource value = new Resource(); [return: Lifetime(\"this\")] Resource M() => value; }",
        "class Example { [return: Lifetime(\"this\")] Example M() => this; }",
        "class Example { [return: Lifetime(\"a\")] Span<int> M([Lifetime(\"a\")] Memory<int> memory) => memory.Span; }",
        "class Example { [return: Lifetime(\"a\")] ReadOnlySpan<int> M([Lifetime(\"a\")] ReadOnlyMemory<int> memory) => memory.Span; }",
        "class Example { [return: Lifetime(\"a\")] Memory<int> M([Lifetime(\"a\")] System.Buffers.IMemoryOwner<int> owner) => owner.Memory; }",
        "class BufferOwner : System.Buffers.IMemoryOwner<int> { public Memory<int> Memory => new int[1]; public void Dispose() { } } class Example { [return: Lifetime(\"a\")] Memory<int> M([Lifetime(\"a\")] BufferOwner owner) => owner.Memory; }",
        "interface IReader { [return: Lifetime(\"a\"), BorrowedMut] Resource M([Lifetime(\"a\")] Resource source); } class Example : IReader { public Resource M(Resource renamed) => renamed; }",
        "class Example { void Outer() { [return: Lifetime(\"a\")] static Resource Nested([Lifetime(\"a\")] Resource input) => input; } }",
        "class Example { void Outer(Resource captured) { [return: Lifetime(\"a\")] Resource Nested([Lifetime(\"a\")] Resource input) => input; } }",
        "class Example { void Outer(Resource captured) { [return: Lifetime(\"a\")] Resource Nested([Lifetime(\"a\")] Resource input) { captured = input; return captured; } } }",
        "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, Resource second) { try { second = new Resource(); throw new InvalidOperationException(); } catch (Exception) { return first; } } }",
        "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input) { static Resource Identity(Resource value) => value; return Identity(input); } }",
        "namespace Foreign { sealed class LifetimeAttribute : Attribute { public LifetimeAttribute(string name) { } } } class Example { [return: Foreign.Lifetime(\"a\")] Resource M([Foreign.Lifetime(\"a\")] Resource value) => new Resource(); }",
        "namespace Test { using Life = SafeSharp.LifetimeAttribute; class Example { [return: Life(\"a\")] Resource M([Life(\"a\")] Resource value) => value; } }",
        "class Example { [return: global::SafeSharp.Lifetime(\"a\")] Resource M([global::SafeSharp.Lifetime(\"a\")] Resource value) => value; }",
        "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource value) { try { return value; } catch (Exception) { return value; } } }",
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task AcceptsProvenRelationshipsAndAllValidReturnAlternatives(string body) =>
        Assert.Empty(await AnalyzeAsync(Prelude + body));

    [Fact]
    public void ContractsExposeTheDeclaredMetadata()
    {
        Assert.Equal("a", new LifetimeAttribute("a").Name);
        var bound = new OutlivesAttribute("a", "b");
        Assert.Equal("a", bound.Longer);
        Assert.Equal("b", bound.Shorter);
        Assert.Throws<ArgumentNullException>(() => new LifetimeAttribute(null!));
        Assert.Throws<ArgumentNullException>(() => new OutlivesAttribute(null!, "b"));
        Assert.Throws<ArgumentNullException>(() => new OutlivesAttribute("a", null!));
    }

    [Fact]
    public async Task NamedRegionBudgetReportsAnExplicitIncompleteResult()
    {
        var (source, span) = ExtractMarker(Prelude + "class Example { [return: Lifetime(\"a\")] static Resource [|M|]([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second) => first; }");
        var diagnostics = await AnalyzeAsync(source, new Dictionary<string, string> { ["safesharp_max_regions"] = "1" });
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SS0043", diagnostic.Id);
        Assert.Equal(span, diagnostic.Location.SourceSpan);
        Assert.Contains("named-region count", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvenanceBudgetDoesNotEmitPartialReturnFindings()
    {
        var body = "class Example { [return: Lifetime(\"a\")] static Resource M([Lifetime(\"a\")] Resource value) { " +
            string.Join(" ", Enumerable.Range(0, 40).Select(index => "var alias" + index + " = value;")) + " return value; } }";
        var diagnostics = await AnalyzeAsync(Prelude + body, new Dictionary<string, string> { ["safesharp_max_region_steps"] = "50" });
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SS0043", diagnostic.Id);
        Assert.Contains("return-provenance", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InheritedSignatureComparisonHasItsOwnCancelableStepBudget()
    {
        var declared = string.Join(", ", Enumerable.Range(0, 40).Select(index => "[Lifetime(\"a\")] Resource p" + index));
        var inherited = string.Join(", ", Enumerable.Range(0, 40).Select(index => "Resource renamed" + index));
        var source = Prelude + "interface IReader { [return: Lifetime(\"a\")] Resource M(" + declared + "); } " +
            "abstract class Reader : IReader { public abstract Resource M(" + inherited + "); }";
        var diagnostic = Assert.Single(await AnalyzeAsync(source, new Dictionary<string, string> { ["safesharp_max_region_steps"] = "1000" }));
        Assert.Equal("SS0043", diagnostic.Id);
        Assert.Contains("inherited-contract comparison", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnannotatedBodiesDoNotConsumeTheLifetimeFlowBudget()
    {
        var statements = string.Join(" ", Enumerable.Range(0, 500).Select(index => "var local" + index + " = new Resource();"));
        var diagnostics = await AnalyzeAsync(Prelude + "class Example { void M() { " + statements + " } }",
            new Dictionary<string, string> { ["safesharp_max_region_steps"] = "1" });
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task RegionGraphMetadataPreservesNamesComponentsAndOriginalProofs()
    {
        const string body = "class Example { [Outlives(\"a\", \"b\"), Outlives(\"b\", \"a\")] [return: Lifetime(\"b\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"c\")] Resource second, bool choose) => choose ? first : second; }";
        var diagnostic = Assert.Single(await AnalyzeAsync(Prelude + body));
        Assert.Equal("SS0041", diagnostic.Id);
        Assert.Equal("a, b, c, this", diagnostic.Properties["SafeSharp.RegionNames"]);
        Assert.Equal("a, b; c; this", diagnostic.Properties["SafeSharp.RegionComponents"]);
        Assert.Equal("a : b; b : a", diagnostic.Properties["SafeSharp.RegionConstraints"]);
        Assert.Contains("a -> b", diagnostic.Properties["SafeSharp.RegionProof"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOriginLabelsCannotCollideWithValidRegionNames()
    {
        const string body = "interface IFactory { Resource Get(); } class Example { [return: Lifetime(\"Unknown\")] Resource M([Lifetime(\"Unknown\")] Resource input, IFactory factory) => factory.Get(); }";
        var diagnostic = Assert.Single(await AnalyzeAsync(Prelude + body));
        Assert.Equal("Unknown, this", diagnostic.Properties["SafeSharp.RegionNames"]);
        Assert.Equal("<Unknown>", diagnostic.Properties["SafeSharp.SourceRegions"]);
    }

    [Fact]
    public async Task GeneratedDeclarationsAndBodiesAreSkipped() =>
        Assert.Empty(await AnalyzeAsync("// <auto-generated/>\n" + Prelude + "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource value) => new Resource(); }"));

    [Fact]
    public async Task DiagnosticCanBeSuppressedWithAPragma() =>
        Assert.Empty(await AnalyzeAsync(Prelude + """

            #pragma warning disable SS0041
            class Example { [return: Lifetime("a")] Resource M([Lifetime("a")] Resource value) => new Resource(); }
            """));

    [Fact]
    public async Task ConcurrentRunsHaveIdenticalEvidence()
    {
        const string body = "class Example { [return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource first, [Lifetime(\"b\")] Resource second, bool choose) => choose ? first : second; }";
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => AnalyzeAsync(Prelude + body)));
        var expected = string.Join("\n", results[0].Select(diagnostic => diagnostic.ToString() + diagnostic.Properties["SafeSharp.SourceRegions"]));
        Assert.All(results, diagnostics => Assert.Equal(expected, string.Join("\n", diagnostics.Select(diagnostic => diagnostic.ToString() + diagnostic.Properties["SafeSharp.SourceRegions"]))));
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, IReadOnlyDictionary<string, string>? settings = null)
    {
        var compilation = AnalyzerTestHost.CreateCompilation(source);
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(diagnostic => diagnostic.ToString())));
        var analyzerOptions = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, new ConfigProvider(settings));
        var options = new CompilationWithAnalyzersOptions(analyzerOptions, onAnalyzerException: null, concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false);
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new LifetimeContractAnalyzer()), options).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
        return diagnostics.OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start).ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    private static (string Source, TextSpan Span) ExtractMarker(string marked)
    {
        var start = marked.IndexOf("[|", StringComparison.Ordinal);
        var end = marked.IndexOf("|]", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return (marked.Remove(end, 2).Remove(start, 2), new TextSpan(start, end - start - 2));
    }

    private sealed class ConfigProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions values;
        internal ConfigProvider(IReadOnlyDictionary<string, string>? settings) => values = new ConfigValues(settings ?? new Dictionary<string, string>());
        public override AnalyzerConfigOptions GlobalOptions => values;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => values;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => values;
    }

    private sealed class ConfigValues : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> values;
        internal ConfigValues(IReadOnlyDictionary<string, string> values) => this.values = values;
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
