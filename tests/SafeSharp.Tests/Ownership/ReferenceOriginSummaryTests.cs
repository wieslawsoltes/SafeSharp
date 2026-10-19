namespace SafeSharp.Tests.Flow;

/// <summary>Checks caller-visible postconditions through the real Roslyn analyzer pipeline.</summary>
public sealed class ReferenceOriginSummaryTests
{
    private const string Prelude = """
        using System;
        using SafeSharp;
        sealed class Resource : IDisposable {
            public void Dispose() { }
            public void Read() { }
            [ReturnsBorrowed("this")] public Resource Keep(Resource other) => this;
            public static Resource operator +(Resource left, Resource right) => right;
            public static Resource operator ++(Resource value) => External;
            public static Resource External = null!;
        }
        class Example {
            Resource field = null!;
            static Resource First(Resource value, Resource ignored) => value;
            static void Replace(ref Resource value, Resource replacement) => value = replacement;
            static void Fill([Owned] out Resource value, Resource ignored) => value = new Resource();
            static void FillBoth([Owned] out Resource first, [Owned] out Resource second) { first = new Resource(); second = new Resource(); }
            static void Mixed(ref Resource unknown, [Owned] out Resource owned) { owned = new Resource(); unknown = Resource.External; }
            static void MixedReverse([Owned] out Resource owned, ref Resource unknown) { owned = new Resource(); unknown = Resource.External; }
            static void Invoke(Action action) => action();
            static Resource Execute(Action action, Resource value) { action(); return value; }
            static ref Resource RefReturn(ref Resource value) => ref value;
            sealed class Box {
                public Resource Value { get; set; } = null!;
                public Resource this[int index] { get => Value; set => Value = value; }
                [ReturnsBorrowed("value")] public Resource this[Resource value] => value;
                public ref Resource RefValue => ref Resource.External;
            }
            static Box ReplaceBox(ref Resource value, Resource replacement) { value = replacement; return new Box(); }
            static int ReplaceIndex(ref Resource value, Resource replacement) { value = replacement; return 0; }
            static Action? OnAdd;
            event Action Changed { add => OnAdd?.Invoke(); remove => OnAdd?.Invoke(); }
        """;

    [Theory]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; return alias;")]
    [InlineData("var value = new Resource(); ref readonly Resource alias = ref value; return alias;")]
    [InlineData("var value = external; ref Resource alias = ref value; alias = new Resource(); return value;")]
    [InlineData("ref Resource alias = ref external; alias = new Resource(); return external;")]
    [InlineData("var value = external; ref Resource first = ref value; ref Resource second = ref first; second = new Resource(); return first;")]
    [InlineData("var value = new Resource(); var other = external; ref Resource alias = ref other; alias = ref value; return alias;")]
    [InlineData("var value = new Resource(); var other = external; ref Resource alias = ref value; alias = ref other; alias = external; return value;")]
    [InlineData("var first = new Resource(); var second = new Resource(); ref Resource alias = ref (choose ? ref first : ref second); return alias;")]
    [InlineData("var value = new Resource(); var other = external; ref Resource alias = ref value; if (choose) alias = ref other; alias = ref value; return alias;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; while (count-- > 0) alias = ref value; return alias;")]
    [InlineData("var value = external; ref Resource alias = ref value; do { alias = new Resource(); } while (count-- > 0); return value;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; while (count-- > 0) alias = ref external; alias = ref value; return alias;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; return First(value, alias = external);")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; return First(value, choose ? alias = external : alias = external);")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; return value.Keep(alias = external);")]
    [InlineData("var value = external; var other = new Resource(); ref Resource alias = ref value; Replace(ref alias, (alias = ref other)); return other;")]
    [InlineData("var value = external; var other = external; ref Resource alias = ref value; Fill(out alias, (alias = ref other)); return value;")]
    [InlineData("var value = external; var other = external; ref Resource alias = ref value; Fill(out alias, choose ? (alias = ref other) : (alias = ref other)); return value;")]
    [InlineData("var value = external; ref Resource alias = ref value; FillBoth(out value, out alias); return value;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; var snapshot = alias++; return snapshot;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; return alias++;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; return new Box()[alias];")]
    [InlineData("ref Resource alias = ref field; var value = new Resource(); alias = external; return value;")]
    [InlineData("var value = new Resource(); Action rewrite = () => value = external; return value;")]
    [InlineData("var value = new Resource(); Action rewrite = () => value = external; return First(value, Execute(rewrite, external));")]
    public async Task ProvenOriginsEstablishOwnershipAtTheCaller(string body)
    {
        var source = FactorySource(body);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var call = source.LastIndexOf("Factory(external, choose, count)", StringComparison.Ordinal);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }

    [Theory]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; alias = external; return value;")]
    [InlineData("var value = new Resource(); ref Resource first = ref value; ref Resource second = ref first; second = external; return value;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; alias = ref external; return alias;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; if (choose) alias = ref external; return alias;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref (choose ? ref value : ref external); return alias;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref (choose ? ref value : ref external); alias = external; return value;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; while (count-- > 0) alias = ref external; return alias;")]
    [InlineData("var value = external; ref Resource alias = ref value; while (count-- > 0) alias = new Resource(); return value;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; Replace(ref alias, external); return value;")]
    [InlineData("var value = new Resource(); var other = external; ref Resource alias = ref value; Replace(ref alias, (alias = ref other)); return value;")]
    [InlineData("var value = new Resource(); var other = external; ref Resource alias = ref value; Replace(ref alias, choose ? (alias = ref other) : (alias = ref other)); return value;")]
    [InlineData("var value = external; var other = external; ref Resource alias = ref value; Fill(out alias, (alias = ref other)); return other;")]
    [InlineData("var value = external; ref Resource alias = ref value; Mixed(ref value, out alias); return value;")]
    [InlineData("var value = external; ref Resource alias = ref value; MixedReverse(out value, ref alias); return value;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; alias += external; return value;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; ++alias; return value;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; (alias, _) = (external, 0); return value;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; ReplaceBox(ref alias, external).Value = external; return value;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; var box = new Box(); _ = box[ReplaceIndex(ref alias, external)]; return value;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; var box = new Box(); box[ReplaceIndex(ref alias, external)] = external; return value;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref RefReturn(ref value); alias = external; return value;")]
    [InlineData("ref Resource alias = ref new Box().RefValue; alias = new Resource(); return alias;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; try { return alias; } catch { return external; }")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; try { alias = new Resource(); } finally { alias = external; } return value;")]
    [InlineData("var value = new Resource(); Action rewrite = () => value = external; rewrite(); return value;")]
    [InlineData("var value = new Resource(); Action rewrite = () => value = external; Invoke(rewrite); return value;")]
    [InlineData("var value = new Resource(); void Rewrite() { value = external; } Rewrite(); return value;")]
    [InlineData("var value = new Resource(); Action rewrite = () => value = external; ref Resource alias = ref value; rewrite(); return alias;")]
    [InlineData("var value = new Resource(); OnAdd = () => value = external; Changed += () => { }; return value;")]
    [InlineData("var value = new Resource(); OnAdd = () => value = external; Changed -= () => { }; return value;")]
    [InlineData("var value = new Resource(); Action rewrite = () => value = external; dynamic box = new Box(); _ = box.Value; return value;")]
    [InlineData("var value = new Resource(); Action rewrite = () => value = external; dynamic box = new Box(); _ = box[0]; return value;")]
    public async Task ReplacementAndOpaqueEffectsCannotEstablishStaleOwnership(string body)
    {
        var source = FactorySource(body);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var call = source.LastIndexOf("Factory(external, choose, count)", StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }

    [Theory]
    [InlineData("result = external; ref Resource alias = ref result; alias = new Resource();", true)]
    [InlineData("result = new Resource(); ref Resource alias = ref result; alias = external;", false)]
    [InlineData("result = external; ref Resource first = ref result; ref Resource second = ref first; Fill(out second, external);", true)]
    [InlineData("result = new Resource(); ref Resource alias = ref result; Replace(ref alias, external);", false)]
    [InlineData("result = new Resource(); ref Resource alias = ref result; if (choose) alias = external;", false)]
    [InlineData("result = external; ref Resource alias = ref result; if (choose) alias = new Resource(); else alias = new Resource();", true)]
    [InlineData("result = external; ref Resource alias = ref result; do { alias = new Resource(); } while (count-- > 0);", true)]
    [InlineData("result = new Resource(); ref Resource alias = ref result; try { alias = new Resource(); } finally { alias = external; }", false)]
    public async Task RefAliasesPreserveOnlyProvenOwnedOutputs(string body, bool owned)
    {
        var source = Prelude + "void Create(Resource external, bool choose, int count, out Resource result) { " + body +
            " } void Caller(Resource external, bool choose, int count) { Create(external, choose, count, out var value); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var output = source.LastIndexOf("out var value", StringComparison.Ordinal);
        Assert.Equal(owned, diagnostics.Any(diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == output));
    }

    [Theory]
    [InlineData("ref Resource alias = ref input; return alias;", true)]
    [InlineData("ref Resource alias = ref external; alias = ref input; return alias;", true)]
    [InlineData("ref Resource alias = ref input; alias = external; return input;", false)]
    [InlineData("ref Resource alias = ref input; alias = ref external; return alias;", false)]
    [InlineData("ref Resource alias = ref input; var snapshot = alias; alias = external; return snapshot;", true)]
    [InlineData("ref Resource alias = ref input; return First(input, alias = external);", true)]
    [InlineData("ref Resource alias = ref input; Replace(ref alias, external); return input;", false)]
    public async Task BorrowedResultsNameTheEvaluatedSourceValue(string body, bool aliasesInput)
    {
        var source = Prelude + "Resource Forward(Resource input, Resource external) { " + body +
            " } void Caller(Resource external) { var owner = new Resource(); var result = Forward(owner, external); owner.Dispose(); result.Read(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var read = source.LastIndexOf("result.Read()", StringComparison.Ordinal);
        Assert.Equal(aliasesInput, diagnostics.Any(diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == read));
    }

    [Theory]
    [InlineData("first = new Resource(); second = external; return first;")]
    [InlineData("ref Resource alias = ref first; alias = new Resource(); second = external; return alias;")]
    [InlineData("first = new Resource(); field = external; return first;")]
    [InlineData("first = new Resource(); Replace(ref second, external); return first;")]
    public async Task CallerReferenceParametersCanAliasTheSameStorage(string body)
    {
        var source = Prelude + "Resource Factory(ref Resource first, ref Resource second, Resource external) { " + body +
            " } void Caller(Resource external) { var value = external; var result = Factory(ref value, ref value, external); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var call = source.LastIndexOf("Factory(ref value, ref value, external)", StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }

    [Fact]
    public async Task AFunctionPointerCallCannotPreserveAnUnprovenWritableStorageOrigin()
    {
        var source = Prelude + "unsafe Resource Factory(delegate*<ref Resource, void> pointer) { var value = new Resource(); pointer(ref value); return value; } " +
            "unsafe void Caller(delegate*<ref Resource, void> pointer) { var result = Factory(pointer); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var call = source.LastIndexOf("Factory(pointer)", StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }

    private static string FactorySource(string body) => Prelude +
        "Resource Factory(Resource external, bool choose, int count) { " + body +
        " } void Caller(Resource external, bool choose, int count) { var result = Factory(external, choose, count); } }";
}
