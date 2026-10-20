using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Tests.LifetimeContracts;

public sealed class DirectCallLifetimeTests
{
    private const string Prelude = """
        using System;
        using System.Collections.Generic;
        using SafeSharp;
        sealed class Resource { }
        """;

    public static TheoryData<string, string> Accepted => new()
    {
        { "covariant-input", """class C { static void Use([TypeLifetime("item", "arg0")] IEnumerable<Resource> values) {} void M([TypeLifetime("a", "arg0")] IEnumerable<Resource> values) { Use(values); } }""" },
        { "invariant-input", """class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {} void M([TypeLifetime("a", "arg0")] List<Resource> values) { Use(values); } }""" },
        { "shared-invariant-lifetime", """class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> first, [TypeLifetime("item", "arg0")] List<Resource> second) {} void M([TypeLifetime("a", "arg0")] List<Resource> first, [TypeLifetime("a", "arg0")] List<Resource> second) { Use(first, second); } }""" },
        { "named-argument-reordering", """class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> first, [TypeLifetime("item", "arg0")] List<Resource> second) {} void M([TypeLifetime("a", "arg0")] List<Resource> first, [TypeLifetime("a", "arg0")] List<Resource> second) { Use(second: second, first: first); } }""" },
        { "stable-local-alias", """class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {} void M([TypeLifetime("a", "arg0")] List<Resource> values) { var alias = values; var second = alias; Use(second); } }""" },
        { "whole-ref-mode-does-not-stick-to-a-value-copy", """class C { static void Use([TypeLifetime("item", "arg0")] IEnumerable<Resource> values) {} void M([TypeLifetime("a", "arg0")] ref IEnumerable<Resource> values) { Use(values); } }""" },
        { "nested-alpha-renaming", """class C { static void Use([TypeLifetime("item", "param0"), TypeLifetime("item", "return")] Func<Resource, Resource> callback) {} void M([TypeLifetime("a", "param0"), TypeLifetime("a", "return")] Func<Resource, Resource> callback) { Use(callback); } }""" },
        { "nested-parameter-ordinal-remapping", """class C { static void Use(int ignored, [TypeLifetime("item", "param0"), TypeLifetime("item", "return")] Func<Resource, Resource> callback) {} void M([TypeLifetime("a", "param0"), TypeLifetime("a", "return")] Func<Resource, Resource> callback) { Use(0, callback); } }""" },
        { "outer-reference-remapping", """class C { static void Use([Lifetime("owner")] Resource value, [TypeLifetime("^owner", "return")] Func<Resource> callback) {} void M([Lifetime("a")] Resource value, [TypeLifetime("^a", "return")] Func<Resource> callback) { Use(value, callback); } }""" },
        { "nested-invariant-callable", """class C { static void Use([TypeLifetime("item", "arg0.param0"), TypeLifetime("item", "arg0.return")] List<Func<Resource, Resource>> callback) {} void M([TypeLifetime("a", "arg0.param0"), TypeLifetime("a", "arg0.return")] List<Func<Resource, Resource>> callback) { Use(callback); } }""" },
        { "implied-generic-bound", """class C { [TypeOutlives("T", "x")] static void Use<T>([Lifetime("x")] T value) {} void M<T>([Lifetime("a")] T value) { Use(value); } }""" },
        { "explicit-generic-bound", """class C { [TypeOutlives("T", "scope")] static void Use<T>([Lifetime("value")] T value, [Lifetime("scope")] ref Resource owner) {} [TypeOutlives("T", "b")] void M<T>([Lifetime("a")] T value, [Lifetime("b")] ref Resource owner) { Use(value, ref owner); } }""" },
        { "transitive-generic-bound", """class C { [TypeOutlives("T", "scope")] static void Use<T>([Lifetime("value")] T value, [Lifetime("scope")] ref Resource owner) {} [Outlives("a", "b")] void M<T>([Lifetime("a")] T value, [Lifetime("b")] ref Resource owner) { Use(value, ref owner); } }""" },
        { "generic-contents-bound", """class C { static void Use<T>([TypeLifetime("item", "arg0")] IEnumerable<T> values) {} void M<T>([TypeLifetime("a", "arg0")] IEnumerable<T> values) { Use(values); } }""" },
        { "instance-target", """class C { void Use([TypeLifetime("item", "arg0")] List<Resource> values) {} void M(C target, [TypeLifetime("a", "arg0")] List<Resource> values) { target.Use(values); } }""" },
        { "inherited-input-contract", """interface I { void Use([TypeLifetime("item", "arg0")] List<Resource> values); } class C : I { public void Use(List<Resource> values) {} void M([TypeLifetime("a", "arg0")] List<Resource> values) { Use(values); } }""" },
        { "extension-receiver-evidence", """static class Extensions { public static void Use([TypeLifetime("item", "arg0")] this List<Resource> values) {} } class C { void M([TypeLifetime("a", "arg0")] List<Resource> values) { values.Use(); } }""" },
        { "generic-extension-receiver-evidence", """static class Extensions { public static void Use<T>([TypeLifetime("item", "arg0")] this IEnumerable<T> values) {} } class C { void M<T>([TypeLifetime("a", "arg0")] IEnumerable<T> values) { values.Use(); } }""" },
        { "unannotated-calls-unchanged", """class C { static void Use(List<Resource> values) {} void M() { Use(new List<Resource>()); } }""" },
        { "additional-input-path-is-not-a-new-callee-demand", """class C { static void Use([TypeLifetime("item", "arg0")] IEnumerable<IEnumerable<Resource>> values) {} void M([TypeLifetime("a", "arg0"), TypeLifetime("b", "arg0.arg0")] IEnumerable<IEnumerable<Resource>> values) { Use(values); } }""" },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task DeclaredCallerEvidenceSatisfiesDirectInputs(string name, string source)
    {
        Assert.NotEmpty(name);
        Assert.Empty(await Analyze(Prelude + source));
    }

    public static TheoryData<string, string> Rejected => new()
    {
        { "unannotated-parameter", """class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {} void M(List<Resource> values) { [|Use(values)|]; } }""" },
        { "unknown-factory-result", """class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {} void M() { [|Use(new List<Resource>())|]; } }""" },
        { "invariant-inputs-cannot-collapse", """class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> first, [TypeLifetime("item", "arg0")] List<Resource> second) {} void M([TypeLifetime("a", "arg0")] List<Resource> first, [TypeLifetime("b", "arg0")] List<Resource> second) { [|Use(first, second)|]; } }""" },
        { "nested-callback-origin-is-not-generalized", """class C { static void Use([TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("x", "return")] Func<Resource, Resource, Resource> callback) {} void M([TypeLifetime("a", "param0"), TypeLifetime("a", "param1"), TypeLifetime("a", "return")] Func<Resource, Resource, Resource> callback) { [|Use(callback)|]; } }""" },
        { "captured-outer-cannot-become-universal", """class C { static void Use([TypeLifetime("fresh", "return")] Func<Resource> callback) {} void M([Lifetime("a")] Resource value, [TypeLifetime("^a", "return")] Func<Resource> callback) { [|Use(callback)|]; } }""" },
        { "missing-type-outlives", """class C { [TypeOutlives("T", "scope")] static void Use<T>([Lifetime("value")] T value, [Lifetime("scope")] ref Resource owner) {} void M<T>([Lifetime("a")] T value, [Lifetime("b")] ref Resource owner) { [|Use(value, ref owner)|]; } }""" },
        { "unknown-bound-cannot-use-empty-regions", """class C { [TypeOutlives("T", "scope")] static void Use<T>([Lifetime("scope")] object owner) {} void M<T>(object owner) { [|Use<T>(owner)|]; } }""" },
        { "parameter-replacement", """class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {} void M([TypeLifetime("a", "arg0")] List<Resource> values) { values = new(); [|Use(values)|]; } }""" },
        { "local-replacement", """class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {} void M([TypeLifetime("a", "arg0")] List<Resource> values) { var alias = values; alias = new(); [|Use(alias)|]; } }""" },
        { "captured-replacement", """class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {} void M([TypeLifetime("a", "arg0")] List<Resource> values) { Action replace = () => values = new(); replace(); [|Use(values)|]; } }""" },
        { "ref-alias-invalidates-origin", """class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {} void M([TypeLifetime("a", "arg0")] List<Resource> values) { ref var alias = ref values; alias = new(); [|Use(values)|]; } }""" },
        { "opaque-ref-call-invalidates-origin", """class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {} static void Replace(ref List<Resource> value) {} void M([TypeLifetime("a", "arg0")] List<Resource> values) { Replace(ref values); [|Use(values)|]; } }""" },
        { "later-argument-replaces-ref-storage", """class C { static void Use([TypeLifetime("item", "arg0")] ref List<Resource> values, int ignored) {} static int Replace(ref List<Resource> value) { value = new(); return 0; } void M([TypeLifetime("a", "arg0")] List<Resource> values) { [|Use(ref values, Replace(ref values))|]; } }""" },
        { "user-conversion-is-not-evidence", """class Wrapper { public static implicit operator List<Resource>(Wrapper value) => new(); } class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {} void M(Wrapper value) { [|Use(value)|]; } }""" },
        { "conditional-evidence-is-not-guessed", """class C { static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {} void M(bool choose, [TypeLifetime("a", "arg0")] List<Resource> first, [TypeLifetime("b", "arg0")] List<Resource> second) { [|Use(choose ? first : second)|]; } }""" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task MissingOrIncompatibleEvidenceIsDiagnosedAtInvocation(string name, string marked)
    {
        Assert.NotEmpty(name);
        var (source, span) = Marker(Prelude + marked);
        var diagnostics = await Analyze(source);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SS0050", diagnostic.Id);
        Assert.Equal(span, diagnostic.Location.SourceSpan);
        Assert.NotEmpty(diagnostic.AdditionalLocations);
        Assert.NotEmpty(diagnostic.Properties["SafeSharp.CallTarget"]!);
        Assert.NotEmpty(diagnostic.Properties["SafeSharp.CallReason"]!);
    }

    [Fact]
    public async Task FailedTypeBoundPreservesTheProofAndActualTypeIdentity()
    {
        var example = Rejected.First(row => (string)row[0] == "missing-type-outlives")[1].ToString()!;
        var diagnostic = Assert.Single(await Analyze(Marker(Prelude + example).Source));
        Assert.NotEmpty(diagnostic.Properties["SafeSharp.RegionProof"]!);
        Assert.NotEmpty(diagnostic.Properties["SafeSharp.TypeOutlivesAssumptions"]!);
        Assert.NotEmpty(diagnostic.Properties["SafeSharp.TypeOutlivesRequirements"]!);
        Assert.Contains("T", diagnostic.Properties["SafeSharp.TypeBindings"], StringComparison.Ordinal);
        Assert.False(diagnostic.Properties.ContainsKey("SafeSharp.DelegateType"));
    }

    [Theory]
    [InlineData("safesharp_max_region_steps", "1")]
    [InlineData("safesharp_max_regions", "3")]
    [InlineData("safesharp_max_region_constraints", "3")]
    public async Task IncompleteCallProofReportsTheExistingCompletenessDiagnostic(string option, string value)
    {
        var source = Accepted.First(row => (string)row[0] == "nested-alpha-renaming")[1].ToString()!;
        var diagnostics = await Analyze(Prelude + source, new Dictionary<string, string> { [option] = value });
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0043");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0050");
    }

    [Fact]
    public async Task RecursiveGenericCallsDoNotSubstituteCallerBoundsAsCalleeBounds()
    {
        const string source = """
            class C {
                [TypeOutlives("T", "a")]
                static void M<T>([Lifetime("a")] object owner) {
                    M<System.Collections.Generic.List<T>>(owner);
                }
            }
            """;
        var diagnostic = Assert.Single(await Analyze(Prelude + source));
        Assert.Equal("SS0050", diagnostic.Id);
        Assert.Contains("List<T>", diagnostic.Properties["SafeSharp.TypeBindings"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentAnalysisProducesDeterministicCallEvidence()
    {
        var example = Rejected.First(row => (string)row[0] == "invariant-inputs-cannot-collapse")[1].ToString()!;
        var source = Marker(Prelude + example).Source;
        var outputs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Analyze(source)));
        static string Evidence(ImmutableArray<Diagnostic> diagnostics) => string.Join("\n", diagnostics.Select(diagnostic => diagnostic.ToString() +
            string.Join(";", diagnostic.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value))));
        Assert.All(outputs, output => Assert.Equal(Evidence(outputs[0]), Evidence(output)));
    }


    [Theory]
    [InlineData("values[0] = new Resource();")]
    [InlineData("var alias = values; alias[0] = new Resource();")]
    [InlineData("Mutate(values);")]
    [InlineData("var alias = values; Mutate(alias);")]
    [InlineData("values.Clear();")]
    [InlineData("_ = values.Count;")]
    [InlineData("Action action = () => values[0] = new Resource(); action();")]
    [InlineData("Stored = values;")]
    [InlineData("var pair = (Values: values, Number: 0); pair.Values.Clear();")]
    [InlineData("var nested = new[] { values }; nested[0].Clear();")]
    [InlineData("List<Resource>[] nested = [values]; nested[0].Clear();")]
    [InlineData("var alias = values ?? new(); alias.Clear();")]
    [InlineData("var alias = true switch { true => values, false => new List<Resource>() }; alias.Clear();")]
    [InlineData("var (alias, number) = (values, 0); alias.Clear();")]
    [InlineData("values?.Clear();")]
    [InlineData("dynamic receiver = new object(); receiver.Mutate(values);")]
    [InlineData("Func<List<Resource>> expose = () => values; Publish(expose);")]
    public async Task MutatedNestedContentsCannotReuseTheInputTypeContract(string mutation)
    {
        var diagnostics = await Analyze(Prelude + """
            class C {
                static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {}
                static void Mutate(List<Resource> values) {}
                static void Publish(Func<List<Resource>> source) {}
                static List<Resource> Stored = new();
                void M([TypeLifetime("a", "arg0")] List<Resource> values) {
            """ + mutation + " Use(values); } }");
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SS0050", diagnostic.Id);
        Assert.Contains("invalidated", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("values[0] = new Resource();")]
    [InlineData("ref var slot = ref values[0]; slot = new Resource();")]
    [InlineData("var alias = values; ref var slot = ref alias[0]; slot = new Resource();")]
    public async Task ArrayWritesAndReferenceExposureInvalidateContentEvidence(string mutation)
    {
        var diagnostics = await Analyze(Prelude + """
            class C {
                static void Use([TypeLifetime("item", "element")] Resource[] values) {}
                void M([TypeLifetime("a", "element")] Resource[] values) {
            """ + mutation + " Use(values); } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0050");
    }

    [Fact]
    public async Task ASubsequentMutationDoesNotInvalidateAnEarlierInvocationOutsideALoop()
    {
        Assert.Empty(await Analyze(Prelude + """
            class C {
                static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {}
                void M([TypeLifetime("a", "arg0")] List<Resource> values) { Use(values); values.Clear(); }
            }
            """));
    }

    [Fact]
    public async Task ALaterLoopMutationMayReachTheNextInvocation()
    {
        var diagnostics = await Analyze(Prelude + """
            class C {
                static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {}
                void M([TypeLifetime("a", "arg0")] List<Resource> values) { while (true) { Use(values); values.Clear(); } }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0050");
    }

    [Fact]
    public async Task ReadOnlyNonEscapingCallsPreserveNestedEvidence()
    {
        Assert.Empty(await Analyze(Prelude + """
            class C {
                static void Use([Borrowed, DoesNotEscape, TypeLifetime("item", "arg0")] List<Resource> values) {}
                void M([TypeLifetime("a", "arg0")] List<Resource> values) { Use(values); Use(values); }
            }
            """));
    }

    [Fact]
    public async Task UnmodeledContentsOfANominalConversionAreRejected()
    {
        var diagnostics = await Analyze(Prelude + """
            class C {
                static void Use([TypeLifetime("item", "arg0")] IEnumerable<object> values) {}
                void M([TypeLifetime("a", "arg0")] IEnumerable<Resource> values) { Use(values); }
            }
            """);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0050");
    }

    [Fact]
    public async Task MatchingOuterGenericTypesDoNotSynthesizeNestedCallableEvidence()
    {
        var diagnostics = await Analyze(Prelude + """
            class C {
                static void Use([TypeLifetime("item", "arg0.return")] IEnumerable<Func<object>> values) {}
                void M([TypeLifetime("a", "arg0.return")] IEnumerable<Func<Resource>> values) { Use(values); }
            }
            """);
        Assert.Equal("SS0050", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task ADirectFieldWriteInvalidatesNestedContentEvidence()
    {
        var diagnostics = await Analyze(Prelude + """
            sealed class Holder<T> { public T Value = default!; }
            class C {
                static void Use([TypeLifetime("item", "arg0")] Holder<Resource> values) {}
                void M([TypeLifetime("a", "arg0")] Holder<Resource> values) { values.Value = new(); Use(values); }
            }
            """);
        Assert.Equal("SS0050", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task AUserDefinedOperatorIsAnOpaqueMutationBoundary()
    {
        var diagnostics = await Analyze(Prelude + """
            sealed class Holder<T> {
                public T Value = default!;
                public static Holder<T> operator +(Holder<T> left, Holder<T> right) { left.Value = right.Value; return left; }
            }
            class C {
                static void Use([TypeLifetime("item", "arg0")] Holder<Resource> values) {}
                void M([TypeLifetime("a", "arg0")] Holder<Resource> values) { _ = values + values; Use(values); }
            }
            """);
        Assert.Equal("SS0050", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task AnOpaqueCallCanMutateBorrowedContentsOfAnUnconstrainedType()
    {
        var diagnostics = await Analyze(Prelude + """
            class C {
                [TypeOutlives("T", "a")]
                static void Use<T>([Lifetime("a")] T value) {}
                static void Mutate<T>(T value) {}
                void M<T>([Lifetime("a")] T value) { Mutate(value); Use(value); }
            }
            """);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SS0050", diagnostic.Id);
        Assert.Contains("invalidated", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValueTypeArgumentsCanContainBorrowedReferences()
    {
        var diagnostics = await Analyze(Prelude + """
            struct Holder<T> { public T Value; }
            class C {
                static void Use([TypeLifetime("item", "arg0")] Holder<Resource> values) {}
                static void Mutate(Holder<Resource> values) {}
                void M([TypeLifetime("a", "arg0")] Holder<Resource> values) { Mutate(values); Use(values); }
            }
            """);
        Assert.Equal("SS0050", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task ManyReadOnlyCallsReuseTheSameBoundedInputEvidence()
    {
        var calls = string.Join(" ", Enumerable.Repeat("Use(values);", 256));
        var source = Prelude + """
            class C {
                static void Use([Borrowed, DoesNotEscape, TypeLifetime("item", "arg0")] List<Resource> values) {}
                void M([TypeLifetime("a", "arg0")] List<Resource> values) {
            """ + calls + " } }";
        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task ALocalFunctionUsesItsOwnInputContract()
    {
        Assert.Empty(await Analyze(Prelude + """
            class C {
                static void Use([TypeLifetime("item", "arg0")] List<Resource> values) {}
                void M() {
                    void Local([TypeLifetime("a", "arg0")] List<Resource> values) { Use(values); }
                }
            }
            """));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmittedContractsUseTheSameCallerBoundObligations(bool provideBound)
    {
        var library = AnalyzerTestHost.CreateCompilation("""
            using SafeSharp;
            public static class ExternalApi {
                [TypeOutlives("T", "scope")]
                public static void Use<T>([Lifetime("value")] T value, [Lifetime("scope")] ref object owner) {}
            }
            """).WithAssemblyName("ExternalCallContracts");
        using var image = new MemoryStream();
        var emitted = library.Emit(image);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var attribute = provideBound ? "[TypeOutlives(\"T\", \"b\")]" : string.Empty;
        var consumer = "using SafeSharp; class C { " + attribute + """
            void M<T>([Lifetime("a")] T value, [Lifetime("b")] ref object owner) { ExternalApi.Use(value, ref owner); }
            }
            """;
        var compilation = AnalyzerTestHost.CreateCompilation(consumer).AddReferences(MetadataReference.CreateFromImage(image.ToArray()));
        var diagnostics = await Analyze(compilation);
        if (provideBound) Assert.Empty(diagnostics);
        else Assert.Equal("SS0050", Assert.Single(diagnostics).Id);
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source, IReadOnlyDictionary<string, string>? settings = null)
    {
        return await Analyze(AnalyzerTestHost.CreateCompilation(source), settings);
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(CSharpCompilation compilation, IReadOnlyDictionary<string, string>? settings = null)
    {
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(diagnostic => diagnostic.ToString())));
        var analyzerOptions = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, new ConfigProvider(settings));
        var options = new CompilationWithAnalyzersOptions(analyzerOptions, null, true, false, false);
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new LifetimeContractAnalyzer()), options).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
        return diagnostics.OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start).ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    private static (string Source, TextSpan Span) Marker(string marked)
    {
        var start = marked.IndexOf("[|", StringComparison.Ordinal);
        var end = marked.IndexOf("|]", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return (marked.Remove(end, 2).Remove(start, 2), new TextSpan(start, end - start - 2));
    }

    private sealed class ConfigProvider(IReadOnlyDictionary<string, string>? settings) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions values = new ConfigValues(settings ?? new Dictionary<string, string>());
        public override AnalyzerConfigOptions GlobalOptions => values;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => values;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => values;
    }

    private sealed class ConfigValues(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
