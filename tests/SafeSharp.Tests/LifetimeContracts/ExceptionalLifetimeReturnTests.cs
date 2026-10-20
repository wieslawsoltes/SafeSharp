using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Tests.LifetimeContracts;

public sealed class ExceptionalLifetimeReturnTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } }
        class Example {
            static void MayThrow() { }
            static ref Resource Identity(ref Resource value) => ref value;
            static ref readonly Resource ReadOnly(in Resource value) => ref value;
            static ref Resource Select(bool choose, ref Resource first, ref Resource second) {
                if (choose) return ref first; return ref second;
            }
            static Task<Task<Resource>> Wrap(Resource value) => Task.FromResult(Task.FromResult(value));
        """;

    [Theory]
    [InlineData("var alias = input; try { MayThrow(); } catch { return alias; } return input;")]
    [InlineData("try { return input; } finally { input = other; }")]
    [InlineData("var original = input; try { input = other; throw new Exception(); } catch { return original; }")]
    [InlineData("var alias = other; try { MayThrow(); alias = input; } catch { alias = input; } return alias;")]
    [InlineData("var alias = input; try { MayThrow(); } catch when (choose) { return alias; } return input;")]
    [InlineData("try { return input; } finally { try { MayThrow(); } catch { } }")]
    [InlineData("try { return input; } finally { if (choose) throw new Exception(); }")]
    [InlineData("ref Resource alias = ref Identity(ref input); return alias;")]
    [InlineData("ref readonly Resource alias = ref ReadOnly(in input); return alias;")]
    [InlineData("ref Resource alias = ref Select(choose, ref input, ref input); return alias;")]
    [InlineData("ref Resource alias = ref Identity(ref other); alias = input; return other;")]
    public async Task NormalReturnsPreserveProvenSourceAfterCleanup(string body)
    {
        var diagnostics = await Analyze(body);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0041");
    }

    [Theory]
    [InlineData("try { return input; } finally { input.Dispose(); }")]
    [InlineData("var alias = input; try { return alias; } finally { input.Dispose(); }")]
    [InlineData("try { return input; } finally { if (choose) input.Dispose(); }")]
    [InlineData("var alias = input; try { MayThrow(); } catch { alias.Dispose(); } return input;")]
    [InlineData("try { MayThrow(); return input; } catch { return other; }")]
    [InlineData("var alias = input; try { MayThrow(); } catch { alias = other; } return alias;")]
    [InlineData("var alias = input; try { MayThrow(); } catch when ((alias = other) != null) { } return alias;")]
    [InlineData("ref Resource alias = ref Identity(ref input); alias = other; return input;")]
    [InlineData("ref Resource alias = ref Select(choose, ref input, ref other); return alias;")]
    public async Task InvalidatedOrAlternativeSourcesDoNotAcquireAContract(string body)
    {
        var diagnostics = await Analyze(body);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0041");
    }

    [Theory]
    [InlineData("return await await Wrap(input);", false)]
    [InlineData("return await await Wrap(other);", true)]
    [InlineData("var pending = Wrap(input); try { return await await pending; } finally { input.Dispose(); }", true)]
    public async Task SourceSummariesComposeNestedPayloadLifetimes(string body, bool violation)
    {
        var source = Prelude + "[return: Lifetime(\"a\")] async Task<Resource> M([Lifetime(\"a\")] Resource input, Resource other) { " + body + " } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, new LifetimeContractAnalyzer());
        Assert.Equal(violation, diagnostics.Any(diagnostic => diagnostic.Id == "SS0041"));
    }

    [Theory]
    [InlineData("try { return ref input; } finally { input = other; }", true)]
    [InlineData("ref Resource alias = ref input; try { return ref alias; } finally { alias = other; }", true)]
    [InlineData("ref Resource alias = ref input; try { return ref alias; } finally { alias = ref other; }", false)]
    public async Task RefReturnsCaptureStorageAndValidateItsValueAfterCleanup(string body, bool violation)
    {
        var source = Prelude + "[ReturnsBorrowed(\"input\")] static ref Resource M(ref Resource input, ref Resource other) { " + body + " } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, new LifetimeContractAnalyzer());
        Assert.Equal(violation, diagnostics.Any(diagnostic => diagnostic.Id == "SS0041"));
    }

    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> Analyze(string body) =>
        AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "[return: Lifetime(\"a\")] Resource M([Lifetime(\"a\")] Resource input, Resource other, bool choose) { " + body + " } }",
            new LifetimeContractAnalyzer());
}
