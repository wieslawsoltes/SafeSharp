namespace SafeSharp.Tests.Flow;

/// <summary>Exception paths are real Roslyn CFGs, including filters and suspended returns.</summary>
public sealed class ExceptionalOriginSummaryTests
{
    private const string Prelude = """
        using System;
        using SafeSharp;
        sealed class Resource : IDisposable {
            public void Dispose() { }
            public void Read() { }
        }
        class Example {
            static void MayThrow() { }
            static bool Filter(ref Resource value, Resource other) { value = other; return true; }
            static void Fill([Owned] out Resource value) { value = new Resource(); }
            static ref Resource Identity(ref Resource value) => ref value;
            static ref readonly Resource ReadOnly(in Resource value) => ref value;
            static ref Resource Select(bool choose, ref Resource first, ref Resource second) {
                if (choose) return ref first;
                return ref second;
            }
        """;

    [Theory]
    [InlineData("try { return new Resource(); } catch { return new Resource(); }")]
    [InlineData("var value = new Resource(); return Ownership.Move(value);")]
    [InlineData("var value = new Resource(); try { return Ownership.Move(value); } finally { value = external; }")]
    [InlineData("var value = new Resource(); try { return value; } finally { value = external; }")]
    [InlineData("Resource value; try { MayThrow(); value = new Resource(); } catch { value = new Resource(); } return value;")]
    [InlineData("var value = new Resource(); try { MayThrow(); } catch { value = new Resource(); } return value;")]
    [InlineData("var value = external; try { throw new InvalidOperationException(); } catch (InvalidOperationException) { value = new Resource(); } return value;")]
    [InlineData("var error = new InvalidOperationException(); try { throw error; } catch (ArgumentException) { return external; } catch (Exception) { return new Resource(); }")]
    [InlineData("var value = external; try { MayThrow(); value = new Resource(); } catch when (choose) { value = new Resource(); } return value;")]
    [InlineData("var value = external; try { value = new Resource(); } finally { value = new Resource(); } return value;")]
    [InlineData("var value = new Resource(); try { return value; } finally { try { MayThrow(); } catch { } }")]
    [InlineData("var value = new Resource(); try { return value; } finally { if (choose) throw new Exception(); }")]
    [InlineData("var value = external; try { throw new InvalidOperationException(); } catch when (false) { return external; } catch { return new Resource(); }")]
    [InlineData("var value = new Resource(); ref Resource alias = ref Identity(ref value); return alias;")]
    [InlineData("var value = external; ref Resource alias = ref Identity(ref value); alias = new Resource(); return value;")]
    [InlineData("var value = new Resource(); ref readonly Resource alias = ref ReadOnly(in value); return alias;")]
    [InlineData("var first = new Resource(); var second = new Resource(); ref Resource alias = ref Select(choose, ref first, ref second); return alias;")]
    public async Task EveryNormalCompletionProvesTheReturnedOwner(string body)
    {
        var source = Source(body);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var call = source.LastIndexOf("Factory(external, choose)", StringComparison.Ordinal);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }

    [Theory]
    [InlineData("try { MayThrow(); return new Resource(); } catch { return external; }")]
    [InlineData("var value = external; try { value = new Resource(); } catch { } return value;")]
    [InlineData("var value = new Resource(); try { return value; } finally { value.Dispose(); }")]
    [InlineData("var value = new Resource(); var alias = value; try { return value; } finally { alias.Dispose(); }")]
    [InlineData("var value = new Resource(); try { return value; } finally { if (choose) value.Dispose(); }")]
    [InlineData("var value = new Resource(); value.Dispose(); return value;")]
    [InlineData("var value = new Resource(); try { return Ownership.Move(value); } finally { value.Dispose(); }")]
    [InlineData("var value = new Resource(); value.Dispose(); return Ownership.Move(value);")]
    [InlineData("var value = new Resource(); var moved = Ownership.Move(value); try { return moved; } finally { value.Dispose(); }")]
    [InlineData("var value = new Resource(); try { MayThrow(); } catch { value.Dispose(); } return value;")]
    [InlineData("var value = external; try { Fill(out value); } catch { } return value;")]
    [InlineData("var value = new Resource(); try { MayThrow(); } catch when (Filter(ref value, external)) { } return value;")]
    [InlineData("var value = new Resource(); try { return value; } finally { try { value.Dispose(); } catch { } }")]
    [InlineData("var value = new Resource(); ref Resource alias = ref Identity(ref value); alias = external; return value;")]
    [InlineData("var first = new Resource(); ref Resource alias = ref Select(choose, ref first, ref external); return alias;")]
    public async Task ExceptionalAndCleanupEffectsCannotManufactureOwnership(string body)
    {
        var source = Source(body);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var call = source.LastIndexOf("Factory(external, choose)", StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }

    [Theory]
    [InlineData("try { result = new Resource(); } catch { result = new Resource(); }", true)]
    [InlineData("try { result = external; } finally { result = new Resource(); }", true)]
    [InlineData("try { result = new Resource(); } finally { result = external; }", false)]
    [InlineData("result = new Resource(); try { MayThrow(); } catch { result.Dispose(); }", false)]
    [InlineData("result = new Resource(); try { return; } finally { result.Dispose(); }", false)]
    public async Task OutputPostconditionsApplyAfterCleanup(string body, bool owned)
    {
        var source = Prelude + "void Factory(Resource external, out Resource result) { " + body +
            " } void Caller(Resource external) { Factory(external, out var result); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var call = source.LastIndexOf("out var result", StringComparison.Ordinal);
        Assert.Equal(owned, diagnostics.Any(diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call));
    }

    private static string Source(string body) => Prelude + "Resource Factory(Resource external, bool choose) { " + body +
        " } void Caller(Resource external, bool choose) { var result = Factory(external, choose); } }";
}
