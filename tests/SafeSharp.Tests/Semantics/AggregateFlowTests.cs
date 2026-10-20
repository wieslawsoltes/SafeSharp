using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Tests.Semantics;

public sealed class AggregateFlowTests
{
    private const string Prefix = "using SafeSharp; using System; using System.IO; using System.Threading.Tasks; class C : IDisposable { [Owned] MemoryStream resource = new(); bool condition; ";

    [Theory]
    [InlineData("public void Dispose() { if (condition) resource.Dispose(); else resource.Dispose(); }")]
    [InlineData("public void Dispose() { if (condition) { resource.Dispose(); return; } resource.Dispose(); }")]
    [InlineData("public void Dispose() { while (condition) { condition = false; } resource.Dispose(); }")]
    [InlineData("public void Dispose() { do { resource.Dispose(); break; } while(condition); }")]
    [InlineData("public void Dispose() { while (true) { resource.Dispose(); break; } }")]
    [InlineData("public void Dispose() { try { if(condition) return; } finally { resource.Dispose(); } }")]
    [InlineData("public void Dispose() { using (resource) { if(condition) return; } }")]
    [InlineData("public void Dispose() { using var cleanup = resource; if(condition) return; }")]
    [InlineData("public void Dispose() { var alias = resource; alias.Dispose(); }")]
    [InlineData("public void Dispose() { IDisposable alias = resource; alias.Dispose(); }")]
    [InlineData("public void Dispose() { if (resource is null) return; resource.Dispose(); }")]
    [InlineData("public void Dispose() { if (resource is not null) resource.Dispose(); }")]
    [InlineData("public void Dispose() { if (resource != null) resource.Dispose(); }")]
    [InlineData("public void Dispose() { try { try { if(condition) return; } finally { condition = false; } } finally { resource.Dispose(); } }")]
    [InlineData("public void Dispose() => Release(true); void Release(bool release) { if(release) resource.Dispose(); }")]
    [InlineData("public void Dispose() => Release(resource); static void Release(IDisposable value) => value.Dispose();")]
    [InlineData("public void Dispose() { void Release() => resource.Dispose(); Release(); }")]
    [InlineData("public void Dispose() { resource.Dispose(); resource = null!; }")]
    [InlineData("public void Dispose() { Recur(); resource.Dispose(); } void Recur() { if(condition) Recur(); }")]
    [InlineData("public void Dispose() { resource.DisposeAsync().GetAwaiter().GetResult(); }")]
    [InlineData("public void Dispose() { resource.DisposeAsync().AsTask().Wait(); }")]
    [InlineData("public void Dispose() { try { resource.Dispose(); throw new Exception(); } catch { } }")]
    [InlineData("public void Dispose() { try { if(condition) throw new Exception(); } catch { } finally { resource.Dispose(); } }")]
    [InlineData("public void Dispose() { try { return; } finally { try { throw new Exception(); } catch { resource.Dispose(); } } }")]
    [InlineData("public void Dispose() { Replace(); resource.Dispose(); } void Replace() { resource.Dispose(); resource = new MemoryStream(); }")]
    [InlineData("public void Dispose() => Release(ref resource); static void Release(ref MemoryStream value) { value.Dispose(); value = null!; }")]
    [InlineData("public void Dispose() { try { resource.DisposeAsync().GetAwaiter().GetResult(); } catch { } }")]
    [InlineData("public void Dispose() { try { Release(resource); } catch { } } static void Release(IDisposable value) => value.Dispose();")]
    public Task Every_normal_path_releases_owned_field(string methods) => SemanticsAssert.NoDiagnosticAsync(Prefix + methods + "}");

    [Theory]
    [InlineData("public void Dispose() { if(condition) resource.Dispose(); }")]
    [InlineData("public void Dispose() { if(condition) return; resource.Dispose(); }")]
    [InlineData("public void Dispose() { while(condition) { resource.Dispose(); break; } }")]
    [InlineData("public void Dispose() => Release(false); void Release(bool release) { if(release) resource.Dispose(); }")]
    [InlineData("public void Dispose() => Release(); void Release() { if(condition) resource.Dispose(); }")]
    [InlineData("public void Dispose() => Release(); void Release() => Release();")]
    [InlineData("public void Dispose() { var other = new C(); other.resource.Dispose(); }")]
    [InlineData("public void Dispose() { resource.Dispose(); resource = new MemoryStream(); }")]
    [InlineData("public void Dispose() { resource = new MemoryStream(); resource.Dispose(); }")]
    [InlineData("public void Dispose() { var old = resource; resource = new MemoryStream(); old.Dispose(); }")]
    [InlineData("public void Dispose() { resource = null!; resource?.Dispose(); }")]
    [InlineData("public void Dispose() { var alias = condition ? resource : null; if(alias == null) return; alias.Dispose(); }")]
    [InlineData("public void Dispose() => Replace(); void Replace() { resource.Dispose(); resource = new MemoryStream(); }")]
    [InlineData("public void Dispose() { try { Work(); resource.Dispose(); } catch { } } static void Work() { if(DateTime.Now.Ticks == 0) throw new Exception(); }")]
    [InlineData("public void Dispose() { try { if(condition) return; } finally { if(condition) resource.Dispose(); } }")]
    [InlineData("public void Dispose() { var old = resource; resource = new MemoryStream(); using(old) {} }")]
    [InlineData("public void Dispose() { Replace(ref resource); resource.Dispose(); } static void Replace(ref MemoryStream value) { value = new MemoryStream(); }")]
    [InlineData("public void Dispose() => StartCleanup(); void StartCleanup() { _ = resource.DisposeAsync(); }")]
    [InlineData("public void Dispose() { resource.Dispose(); try { ReplaceAndThrow(); } catch { } } void ReplaceAndThrow() { resource = new MemoryStream(); throw new Exception(); }")]
    public Task Any_unreleased_normal_path_is_diagnosed_at_the_field(string methods) =>
        SemanticsAssert.DiagnosticAsync(Prefix.Replace("resource =", "[|resource|] =", StringComparison.Ordinal) + methods + "}", "SS0021");

    [Theory]
    [InlineData("public async ValueTask DisposeAsync() { var completion = resource.DisposeAsync(); await completion; }")]
    [InlineData("public async ValueTask DisposeAsync() { var completion = resource.DisposeAsync().ConfigureAwait(false); await completion; }")]
    [InlineData("public ValueTask DisposeAsync() => ReleaseAsync(); async ValueTask ReleaseAsync() { await resource.DisposeAsync(); }")]
    [InlineData("public async ValueTask DisposeAsync() { try { if(condition) return; } finally { await resource.DisposeAsync(); } }")]
    [InlineData("public async ValueTask DisposeAsync() { await using var cleanup = resource; if(condition) return; }")]
    [InlineData("public async ValueTask DisposeAsync() { var completion = resource.DisposeAsync(); var alias = completion; await alias.ConfigureAwait(false); }")]
    [InlineData("public async ValueTask DisposeAsync() { await ReplaceAsync(); resource.Dispose(); } async ValueTask ReplaceAsync() { await resource.DisposeAsync(); resource = new MemoryStream(); }")]
    public Task Async_completion_is_observed_on_every_normal_path(string methods) =>
        SemanticsAssert.NoDiagnosticAsync(Prefix.Replace("IDisposable", "IAsyncDisposable", StringComparison.Ordinal) + methods + "}");

    [Theory]
    [InlineData("public ValueTask DisposeAsync() { var completion = resource.DisposeAsync(); return default; }")]
    [InlineData("public async ValueTask DisposeAsync() { if(condition) await resource.DisposeAsync(); }")]
    [InlineData("public ValueTask DisposeAsync() { _ = resource.DisposeAsync().ConfigureAwait(false); return default; }")]
    [InlineData("public ValueTask DisposeAsync() { ReleaseAsync(); return default; } async ValueTask ReleaseAsync() { await resource.DisposeAsync(); }")]
    [InlineData("public async ValueTask DisposeAsync() { resource.Dispose(); await ReplaceAsync(); } async ValueTask ReplaceAsync() { await Task.Yield(); resource = new MemoryStream(); }")]
    [InlineData("public ValueTask DisposeAsync() { resource.Dispose(); _ = ReleaseAsync(); return default; } async ValueTask ReleaseAsync() { await resource.DisposeAsync(); }")]
    [InlineData("public ValueTask DisposeAsync() { resource.Dispose(); _ = ReleaseAsync(); return default; } async ValueTask ReleaseAsync() { await resource.DisposeAsync(); throw new Exception(); }")]
    public Task Starting_async_cleanup_without_observation_is_insufficient(string methods) =>
        SemanticsAssert.DiagnosticAsync(Prefix.Replace("IDisposable", "IAsyncDisposable", StringComparison.Ordinal)
            .Replace("resource =", "[|resource|] =", StringComparison.Ordinal) + methods + "}", "SS0021");

    [Fact]
    public Task Inherited_entrypoint_dispatches_to_constant_specialized_override() => SemanticsAssert.NoDiagnosticAsync(
        "using System; using System.IO; using SafeSharp; class Base : IDisposable { public void Dispose() => Dispose(true); protected virtual void Dispose(bool disposing) {} } " +
        "class Middle : Base { protected override void Dispose(bool disposing) => base.Dispose(disposing); } " +
        "class Owner : Middle { [Owned] MemoryStream resource = new(); protected override void Dispose(bool disposing) { if(disposing) resource.Dispose(); base.Dispose(disposing); } }");

    [Fact]
    public Task Each_disposal_interface_requires_its_own_complete_cleanup() => SemanticsAssert.DiagnosticAsync(
        "using System; using System.IO; using System.Threading.Tasks; using SafeSharp; class C : IDisposable, IAsyncDisposable { [Owned] MemoryStream [|resource|] = new(); public void Dispose() => resource.Dispose(); public ValueTask DisposeAsync() => default; }", "SS0021");

    [Fact]
    public Task When_all_observes_both_async_field_completions() => SemanticsAssert.NoDiagnosticAsync(
        "using System; using System.IO; using System.Threading.Tasks; using SafeSharp; class C : IAsyncDisposable { [Owned] MemoryStream first = new(); [Owned] MemoryStream second = new(); public async ValueTask DisposeAsync() { await Task.WhenAll(first.DisposeAsync().AsTask(), second.DisposeAsync().AsTask()); } }");

    [Fact]
    public Task Derived_override_must_preserve_inherited_cleanup() => SemanticsAssert.DiagnosticAsync(
        "using System; using System.IO; using SafeSharp; class Base : IDisposable { [Owned] protected MemoryStream resource = new(); public virtual void Dispose() => resource.Dispose(); } class [|Derived|] : Base { public override void Dispose() {} }", "SS0021");

    [Fact]
    public Task Derived_override_can_delegate_to_base_cleanup() => SemanticsAssert.NoDiagnosticAsync(
        "using System; using System.IO; using SafeSharp; class Base : IDisposable { [Owned] protected MemoryStream resource = new(); public virtual void Dispose() => resource.Dispose(); } class Derived : Base { public override void Dispose() => base.Dispose(); }");

    [Fact]
    public Task Abstract_cleanup_contract_is_checked_at_concrete_implementation() => SemanticsAssert.NoDiagnosticAsync(
        "using System; using System.IO; using SafeSharp; abstract class Base : IDisposable { [Owned] protected MemoryStream resource = new(); public abstract void Dispose(); } sealed class Derived : Base { public override void Dispose() => resource.Dispose(); }");

    [Fact]
    public Task Constructed_generic_base_fields_keep_source_identity() => SemanticsAssert.NoDiagnosticAsync(
        "using System; using System.IO; using SafeSharp; class Base<T> : IDisposable where T : IDisposable { [Owned] protected T resource = default!; public void Dispose() => resource.Dispose(); } class Derived : Base<MemoryStream> { }");

    [Theory]
    [InlineData("public void Dispose() { using var temporary = new MemoryStream(); void Release() => resource.Dispose(); Release(); }")]
    [InlineData("public void Dispose() { try { void Release() => resource.Dispose(); Release(); } catch { } }")]
    [InlineData("public void Dispose() { try { void Work() { if(condition) throw new Exception(); } Work(); } finally { resource.Dispose(); } }")]
    [InlineData("public void Dispose() { try { return; } finally { void Release() { try { throw new Exception(); } catch { resource.Dispose(); } } Release(); } }")]
    public Task Nested_aggregate_helpers_keep_exception_regions_inside_their_graph(string methods) =>
        SemanticsAssert.NoDiagnosticAsync(Prefix + methods + "}");

    [Theory]
    [InlineData("public void Dispose() { using var temporary = new MemoryStream(); void Release() { if(condition) resource.Dispose(); } Release(); }")]
    [InlineData("public void Dispose() { try { using var temporary = new MemoryStream(); void Release() { if(condition) throw new Exception(); resource.Dispose(); } Release(); } catch { } }")]
    [InlineData("public void Dispose() { try { return; } finally { void Release() { if(condition) resource.Dispose(); } Release(); } }")]
    public Task Nested_aggregate_helpers_cannot_borrow_outer_cleanup_as_a_local_proof(string methods) =>
        SemanticsAssert.DiagnosticAsync(Prefix.Replace("resource =", "[|resource|] =", StringComparison.Ordinal) + methods + "}", "SS0021");

    [Fact]
    public Task Nested_async_aggregate_helper_keeps_outer_await_using_regions_separate() => SemanticsAssert.NoDiagnosticAsync(
        Prefix.Replace("IDisposable", "IAsyncDisposable", StringComparison.Ordinal) +
        "public async ValueTask DisposeAsync() { await using var temporary = new MemoryStream(); async ValueTask ReleaseAsync() => await resource.DisposeAsync(); await ReleaseAsync(); } }");

    [Fact]
    public async Task Failed_entrypoints_and_normal_exit_evidence_are_preserved()
    {
        var diagnostic = Assert.Single(await SemanticsAssert.AnalyzeAsync(Prefix + "public void Dispose() { if(condition) return; resource.Dispose(); }}"));
        Assert.Equal("normalExitMustRelease", diagnostic.Properties["SafeSharp.CleanupProof"]);
        Assert.Contains("C.Dispose()", diagnostic.Properties["SafeSharp.CleanupEntrypoints"]);
        Assert.NotEmpty(diagnostic.AdditionalLocations);
    }

    [Fact]
    public async Task Budget_exhaustion_is_an_explicit_incomplete_result()
    {
        var compilation = AnalyzerTestHost.CreateCompilation(Prefix + "public void Dispose() => resource.Dispose(); }");
        var options = new AnalyzerOptions([], new BudgetOptions());
        var diagnostics = await compilation.WithAnalyzers([new SafeSharpSemanticAnalyzer()], options).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0046");
    }

    private sealed class BudgetOptions : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions Values = new SmallBudget();
        public override AnalyzerConfigOptions GlobalOptions => Values;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Values;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Values;
        private sealed class SmallBudget : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value) { value = "1"; return key == "safesharp_max_contract_steps"; }
        }
    }
}
