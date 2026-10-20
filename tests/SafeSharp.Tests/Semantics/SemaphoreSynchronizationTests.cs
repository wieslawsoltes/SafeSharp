using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Tests.Semantics;

public sealed class SemaphoreSynchronizationTests
{
    private const string Imports = "using System; using System.Threading; using System.Threading.Tasks; using SafeSharp; ";
    private const string Fields = "readonly SemaphoreSlim gate = new(1, 1); readonly SemaphoreSlim other = new(1, 1); [GuardedBy(nameof(gate))] int value; static void Throwing() {} ";

    [Theory]
    [InlineData("void M() { gate.Wait(); try { value++; } finally { gate.Release(); } }")]
    [InlineData("void M(C target) { var semaphore = target.gate; semaphore.Wait(); try { target.value++; } finally { semaphore.Release(); } }")]
    [InlineData("void M() { var alias = gate; alias.Wait(); try { value++; } finally { alias.Release(); } }")]
    [InlineData("void M() { if (gate.Wait(0)) { try { value++; } finally { gate.Release(); } } }")]
    [InlineData("void M() { var taken = gate.Wait(1); if (!taken) return; try { value++; } finally { gate.Release(); } }")]
    [InlineData("void M(CancellationToken token) { gate.Wait(token); try { value++; } finally { gate.Release(1); } }")]
    [InlineData("void M() { gate.Wait(); try { if (gate.Wait(0)) gate.Release(2); value++; } finally { gate.Release(); } }")]
    [InlineData("async Task M() { await gate.WaitAsync(); try { if (await gate.WaitAsync(0)) gate.Release(2); value++; } finally { gate.Release(); } }")]
    [InlineData("async Task M() { await gate.WaitAsync(); try { value++; await Task.Yield(); value++; } finally { gate.Release(); } }")]
    [InlineData("async Task M(CancellationToken token) { await gate.WaitAsync(token).ConfigureAwait(false); try { value++; } finally { gate.Release(); } }")]
    [InlineData("async Task M() { if (await gate.WaitAsync(0)) { try { value++; } finally { gate.Release(); } } }")]
    [InlineData("async Task M(CancellationToken token) { bool taken = false; try { taken = await gate.WaitAsync(0, token); if (taken) value++; } finally { if (taken) gate.Release(); } }")]
    [InlineData("async Task M() { var pending = gate.WaitAsync(); var copy = pending; await copy; try { value++; } finally { gate.Release(); } }")]
    [InlineData("async Task M() { var pending = gate.WaitAsync(); var configured = pending.ConfigureAwait(false); await configured; try { value++; } finally { gate.Release(); } }")]
    [InlineData("async Task M() { var pending = gate.WaitAsync(); await pending; try { await pending; value++; } finally { gate.Release(); } }")]
    [InlineData("async Task M() { var pending = gate.WaitAsync(0); if (!await pending) return; try { if (await pending) value++; } finally { gate.Release(); } }")]
    [InlineData("void M() { gate.WaitAsync().GetAwaiter().GetResult(); try { value++; } finally { gate.Release(); } }")]
    [InlineData("void M() { if (gate.WaitAsync(0).Result) { try { value++; } finally { gate.Release(); } } }")]
    [InlineData("async Task M() { await gate.WaitAsync(); try { try { Throwing(); } catch { value++; } } finally { gate.Release(); } }")]
    [InlineData("async Task M(bool again) { while (again) { await gate.WaitAsync(); try { value++; } finally { gate.Release(); } } }")]
    [InlineData("[RequiresLock(nameof(gate))] void Update() { value++; } async Task M() { await gate.WaitAsync(); try { Update(); } finally { gate.Release(); } }")]
    public async Task ProvenBinaryPermitsProtectSynchronousAndAsynchronousAccess(string method)
    {
        var diagnostics = await Analyze(Imports + "class C { " + Fields + method + " }");
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics));
    }

    [Theory]
    [InlineData("void M() { gate.Wait(); value++; }")]
    [InlineData("void M() { gate.Wait(); Throwing(); gate.Release(); }")]
    [InlineData("void M() { gate.Wait(0); value++; }")]
    [InlineData("void M() { if (!gate.Wait(0)) value++; }")]
    [InlineData("void M() { if (gate.Wait(0)) gate.Release(); value++; }")]
    [InlineData("void M() { gate.Release(); }")]
    [InlineData("void M() { gate.Wait(); gate.Release(2); }")]
    [InlineData("void M() { gate.Wait(); try { gate.Wait(); try { value++; } finally { gate.Release(); } } finally { gate.Release(); } }")]
    [InlineData("async Task M() { await gate.WaitAsync(); try { await gate.WaitAsync(); try { value++; } finally { gate.Release(); } } finally { gate.Release(); } }")]
    [InlineData("void M(int count) { gate.Wait(); gate.Release(count); value++; }")]
    [InlineData("void M() { var taken = gate.Wait(0); if (taken) gate.Release(); if (taken) value++; }")]
    [InlineData("void M() { other.Wait(); try { value++; } finally { other.Release(); } }")]
    [InlineData("void M() { lock (gate) value++; }")]
    [InlineData("void M() { gate.WaitAsync(); value++; }")]
    [InlineData("async Task M() { dynamic pending = gate.WaitAsync(); await pending.ConfigureAwait(false); value++; }")]
    [InlineData("void M() { var pending = gate.WaitAsync(); gate.Wait(); try { value++; } finally { gate.Release(); } }")]
    [InlineData("async Task M() { await gate.WaitAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); value++; }")]
    [InlineData("async Task M() { await gate.WaitAsync(); value++; }")]
    [InlineData("async Task M() { await gate.WaitAsync(0); value++; }")]
    [InlineData("async Task M() { if (!await gate.WaitAsync(0)) value++; }")]
    [InlineData("async Task M(CancellationToken token) { try { await gate.WaitAsync(token); } catch (OperationCanceledException) { value++; } }")]
    [InlineData("async Task M(CancellationToken token) { try { await gate.WaitAsync(token); value++; } finally { gate.Release(); } }")]
    [InlineData("async Task M() { var pending = gate.WaitAsync(); await pending; gate.Release(); await pending; value++; }")]
    [InlineData("async Task M() { var pending = gate.WaitAsync(0); if (await pending) gate.Release(); if (await pending) value++; }")]
    [InlineData("void M() { gate.Wait(); try { gate.Dispose(); value++; } finally { gate.Release(); } }")]
    [InlineData("[RequiresLock(nameof(gate))] void M() { gate.Release(); }")]
    [InlineData("async Task M() { var monitor = new object(); Monitor.Enter(monitor); try { await gate.WaitAsync(); try { value++; } finally { gate.Release(); } } finally { Monitor.Exit(monitor); } }")]
    public async Task MissingReleasedPendingAndExceptionalCapabilitiesAreDiagnosed(string method)
    {
        var diagnostics = await Analyze(Imports + "class C { " + Fields + method + " }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0049");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0046");
    }

    [Theory]
    [InlineData("new(1)")]
    [InlineData("new(2, 2)")]
    [InlineData("new(1, 2)")]
    public async Task CountingAndUnboundedSemaphoresDoNotProveExclusiveAccess(string creation)
    {
        var source = Imports + "class C { readonly SemaphoreSlim gate = " + creation + "; [GuardedBy(nameof(gate))] int value; " +
            "void M() { gate.Wait(); try { value++; } finally { gate.Release(); } } }";
        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0049" && diagnostic.Location.SourceSpan.Start == source.IndexOf("value++;", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("gate = new SemaphoreSlim(1, 2);")]
    [InlineData("gate = external;")]
    [InlineData("Replace(ref gate);")]
    [InlineData("ref var alias = ref gate; alias = new SemaphoreSlim(2, 2);")]
    [InlineData("ref var alias = ref (gate); alias = external;")]
    public async Task ConstructorOverwritesCannotLeaveAFalseBinaryProof(string assignment)
    {
        var source = Imports + "class C { " + Fields + "C(SemaphoreSlim external) { " + assignment +
            " } static void Replace(ref SemaphoreSlim semaphore) {} void M() { gate.Wait(); try { value++; } finally { gate.Release(); } } }";
        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0049" && diagnostic.Location.SourceSpan.Start == source.IndexOf("value++;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WholeStructAssignmentCannotLeaveAFalseFieldInitializerProof()
    {
        var source = Imports + "struct C { readonly SemaphoreSlim gate = new(1, 1); [GuardedBy(nameof(gate))] int value; " +
            "public C(C replacement) { this = replacement; } void M() { gate.Wait(); try { value++; } finally { gate.Release(); } } }";
        Assert.Contains(await Analyze(source), diagnostic => diagnostic.Id == "SS0049");
    }

    [Fact]
    public async Task InitAccessorOverwritesCannotLeaveAFalseBinaryProof()
    {
        var source = Imports + "class C { " + Fields + "public SemaphoreSlim Gate { init { gate = value; } } " +
            "void M() { gate.Wait(); try { this.value++; } finally { gate.Release(); } } }";
        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0049" && diagnostic.Location.SourceSpan.Start == source.IndexOf("this.value++;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitAccessorThatPreservesTheMaximumRetainsTheBinaryProof()
    {
        var source = Imports + "class C { " + Fields + "public bool Reset { init { gate = new SemaphoreSlim(0, 1); } } " +
            "void M() { gate.Wait(); try { this.value++; } finally { gate.Release(); } } }";
        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task EveryConstructorPreservingTheMaximumRetainsTheBinaryProof()
    {
        var source = Imports + "class C { " + Fields + "C() { gate = new SemaphoreSlim(0, 1); } " +
            "void M() { gate.Wait(); try { value++; } finally { gate.Release(); } } }";
        Assert.Empty(await Analyze(source));
    }

    [Theory]
    [InlineData("C() { gate.Wait(); gate = new SemaphoreSlim(1, 1); gate.Release(); }")]
    [InlineData("public bool Reset { init { gate.Wait(); gate = new SemaphoreSlim(1, 1); gate.Release(); } }")]
    public async Task ReplacingReadonlyStorageCannotBalanceDifferentSemaphoreObjects(string body)
    {
        Assert.Contains(await Analyze(Imports + "class C { " + Fields + body + " }"), diagnostic => diagnostic.Id == "SS0049");
    }

    [Theory]
    [InlineData("C() { var original = gate; original.Wait(); try { gate = new SemaphoreSlim(1, 1); } finally { original.Release(); } }")]
    [InlineData("public bool Reset { init { var original = gate; original.Wait(); try { gate = new SemaphoreSlim(1, 1); } finally { original.Release(); } } }")]
    public async Task SavedReadonlyFieldSnapshotsKeepTheirOriginalPermitIdentity(string body)
    {
        Assert.Empty(await Analyze(Imports + "class C { " + Fields + body + " }"));
    }

    [Fact]
    public async Task WholeStructReplacementCannotBalanceDifferentSemaphoreObjects()
    {
        var source = Imports + "struct C { readonly SemaphoreSlim gate = new(1, 1); public C() {} " +
            "void M(C replacement) { gate.Wait(); this = replacement; gate.Release(); } }";
        Assert.Contains(await Analyze(source), diagnostic => diagnostic.Id == "SS0049");
    }

    [Fact]
    public async Task SavedStructFieldSnapshotKeepsItsOriginalPermitIdentity()
    {
        var source = Imports + "struct C { readonly SemaphoreSlim gate = new(1, 1); public C() {} " +
            "void M(C replacement) { var original = gate; original.Wait(); try { this = replacement; } finally { original.Release(); } } }";
        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task ReadonlyFieldReplacementCannotReuseAnExclusivePrecondition()
    {
        var source = Imports + "class C { " + Fields + "public bool Reset { [RequiresLock(nameof(gate))] init { gate = new SemaphoreSlim(1, 1); this.value++; } } }";
        Assert.Contains(await Analyze(source), diagnostic => diagnostic.Id == "SS0049");
    }

    [Fact]
    public async Task LocalConstructionCanEstablishACalleePrecondition()
    {
        var source = Imports + "class C { [RequiresLock(nameof(sync))] static void Touch(SemaphoreSlim sync) {} " +
            "void M() { var sync = new SemaphoreSlim(1, 1); var alias = sync; alias.Wait(); try { Touch(sync); } finally { alias.Release(); } } }";
        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task LocalReassignmentDoesNotTransferThePermit()
    {
        var source = Imports + "class C { [RequiresLock(nameof(sync))] static void Touch(SemaphoreSlim sync) {} " +
            "void M() { var sync = new SemaphoreSlim(1, 1); var original = sync; original.Wait(); try { sync = new SemaphoreSlim(1, 1); Touch(sync); } finally { original.Release(); } } }";
        Assert.Contains(await Analyze(source), diagnostic => diagnostic.Id == "SS0049");
    }

    [Fact]
    public async Task UnknownMaximumAtParameterBoundaryCannotProveExclusivity()
    {
        var source = Imports + "class C { [RequiresLock(nameof(sync))] static void Touch(SemaphoreSlim sync) {} " +
            "void M(SemaphoreSlim sync) { sync.Wait(); try { Touch(sync); } finally { sync.Release(); } } }";
        Assert.Contains(await Analyze(source), diagnostic => diagnostic.Id == "SS0049");
    }

    [Fact]
    public async Task CountingPermitsAreBalancedWithoutClaimingMutexSafety()
    {
        var source = Imports + "class C { readonly SemaphoreSlim gate = new(2, 2); " +
            "void M() { gate.Wait(); try { gate.Wait(); try { } finally { gate.Release(); } } finally { gate.Release(); } } }";
        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task FailedReleaseRetainsTheOriginalPermitInTheCatch()
    {
        var source = Imports + "class C { " + Fields + "void M() { gate.Wait(); try { try { gate.Release(0); } catch (ArgumentOutOfRangeException) { value++; } } finally { gate.Release(); } } }";
        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0049" && diagnostic.GetMessage().Contains("positive", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Location.SourceSpan.Start == source.IndexOf("value++;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FaultedWaitDoesNotCreateAReleaseObligation()
    {
        var source = Imports + "class C { " + Fields + "async Task M(CancellationToken token) { try { await gate.WaitAsync(token); } catch (OperationCanceledException) { return; } try { value++; } finally { gate.Release(); } } }";
        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task OneTaskCannotDischargeAnotherPendingWait()
    {
        var source = Imports + "class C { " + Fields + "async Task M() { var first = gate.WaitAsync(); var second = other.WaitAsync(); await first; try { value++; } finally { gate.Release(); } } }";
        Assert.Contains(await Analyze(source), diagnostic => diagnostic.Id == "SS0049" && diagnostic.GetMessage().Contains("pending acquisition", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UserDefinedAwaitableNamesCannotManufactureAPermit()
    {
        var source = Imports + "class Fake { public Task ConfigureAwait(bool value) => Task.CompletedTask; } class C { " + Fields +
            "async Task M() { var pending = gate.WaitAsync(); await new Fake().ConfigureAwait(false); value++; } }";
        Assert.Contains(await Analyze(source), diagnostic => diagnostic.Id == "SS0049");
    }

    [Fact]
    public async Task FakeFrameworkSemaphoreDoesNotAcquireACapability()
    {
        var source = Imports + "namespace System.Threading { class SemaphoreSlim { public SemaphoreSlim(int x, int y) {} public void Wait() {} public void Release() {} } } " +
            "class C { " + Fields + "void M() { gate.Wait(); try { value++; } finally { gate.Release(); } } }";
        Assert.Contains(await Analyze(source), diagnostic => diagnostic.Id == "SS0049");
    }

    [Fact]
    public async Task SemaphoreWitnessRetainsItsProtocolAndHeldInterval()
    {
        var source = Imports + "class C { " + Fields + "void M() { other.Wait(); try { value++; } finally { other.Release(); } } }";
        var diagnostic = (await Analyze(source)).First(diagnostic => diagnostic.Location.SourceSpan.Start == source.IndexOf("value++;", StringComparison.Ordinal));
        Assert.Equal("SemaphoreSlim", diagnostic.Properties["SafeSharp.SynchronizationProtocol"]);
        Assert.Contains("this.other [SemaphoreSlim: 1..1]", diagnostic.Properties["SafeSharp.SynchronizationHeld"]);
        Assert.NotEmpty(diagnostic.AdditionalLocations);
    }

    [Fact]
    public async Task SemaphoreBudgetFailureIsObservable()
    {
        var compilation = AnalyzerTestHost.CreateCompilation(Imports + "class C { " + Fields + "async Task M() { await gate.WaitAsync(); try { value++; } finally { gate.Release(); } } }");
        var diagnostics = await compilation.WithAnalyzers([new ConcurrencyContractAnalyzer()], new AnalyzerOptions([], new OneStepOptions())).GetAnalyzerDiagnosticsAsync();
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0046");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "AD0001" or "SS0049");
    }

    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> Analyze(string source) =>
        AnalyzerTestHost.GetDiagnosticsAsync(source, new ConcurrencyContractAnalyzer());
    private sealed class OneStepOptions : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions Values = new Options();
        public override AnalyzerConfigOptions GlobalOptions => Values;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Values;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Values;
        private sealed class Options : AnalyzerConfigOptions
        { public override bool TryGetValue(string key, out string value) { value = "1"; return key == "safesharp_max_contract_steps"; } }
    }
}
