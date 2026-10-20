using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Tests.Semantics;

public sealed class SynchronizationTests
{
    private const string Imports = "using System; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks; using SafeSharp; ";
    private const string Fields = "readonly object gate = new(); readonly object other = new(); [GuardedBy(nameof(gate))] int value; " +
        "[GuardedBy(nameof(gate))] readonly List<int> items = new(); static void Throwing() {} static void Use([DoesNotEscape] List<int> list) {} ";

    [Theory]
    [InlineData("void M() { lock (gate) { value++; } }")]
    [InlineData("int M() { lock (gate) return value; }")]
    [InlineData("void M() { var alias = gate; lock (alias) value++; }")]
    [InlineData("void M() { lock (gate) { lock (gate) value++; value++; } }")]
    [InlineData("void M(bool choose) { lock (gate) { if (choose) value++; else value--; } }")]
    [InlineData("void M(bool choose) { lock (gate) { while (choose) { value++; if (value > 5) break; } } }")]
    [InlineData("void M() { lock (gate) { try { Throwing(); } catch { value++; } finally { value++; } } }")]
    [InlineData("void M() { lock (gate) { try { Throwing(); } catch when (++value > 0) { value++; } } }")]
    [InlineData("void M() { Monitor.Enter(gate); try { value++; Throwing(); } finally { Monitor.Exit(gate); } }")]
    [InlineData("void M() { bool taken = false; try { Monitor.Enter(gate, ref taken); value++; } finally { if (taken) Monitor.Exit(gate); } }")]
    [InlineData("void M() { if (Monitor.TryEnter(gate)) { try { value++; } finally { Monitor.Exit(gate); } } }")]
    [InlineData("void M() { bool taken = false; try { Monitor.TryEnter(gate, ref taken); if (taken) value++; } finally { if (taken) Monitor.Exit(gate); } }")]
    [InlineData("void M() { if (!Monitor.TryEnter(gate)) return; try { value++; } finally { Monitor.Exit(gate); } }")]
    [InlineData("void M() { lock (gate) { var alias = items; alias.Add(1); } }")]
    [InlineData("void M() { lock (gate) Use(items); }")]
    [InlineData("void M() { int copy; lock (gate) copy = value; Console.WriteLine(copy); }")]
    [InlineData("[RequiresLock(nameof(gate))] void Increment() { value++; } void M() { lock (gate) Increment(); }")]
    [InlineData("void M() { var copy = this; lock (copy.gate) value++; }")]
    [InlineData("void M(C target) { lock (target.gate) target.value++; }")]
    [InlineData("void M() { lock (gate) { Monitor.Pulse(gate); value++; } }")]
    [InlineData("void M() { lock (gate) { Monitor.Wait(gate, 1); value++; } }")]
    [InlineData("void M() { lock (gate) { ref int alias = ref value; alias++; } }")]
    public async Task MatchingCapabilitiesProtectEveryAccess(string method) => Assert.Empty(await Analyze(Imports + "class C { " + Fields + method + " }"));

    [Theory]
    [InlineData("void M() { value++; }")]
    [InlineData("int M() => value;")]
    [InlineData("void M() { lock (other) value++; }")]
    [InlineData("void M(C target) { lock (gate) target.value++; }")]
    [InlineData("void M(C target) { lock (target.gate) value++; }")]
    [InlineData("void M(bool choose) { if (choose) Monitor.Enter(gate); value++; }")]
    [InlineData("void M() { Monitor.TryEnter(gate); value++; }")]
    [InlineData("void M() { if (Monitor.TryEnter(gate)) Monitor.Exit(gate); value++; }")]
    [InlineData("void M() { List<int> alias; lock (gate) alias = items; alias.Add(1); }")]
    [InlineData("List<int> M() { lock (gate) return items; }")]
    [InlineData("object? saved; void M() { lock (gate) saved = items; }")]
    [InlineData("void Store(List<int> list) {} void M() { lock (gate) Store(items); }")]
    [InlineData("void M() { lock (gate) { var alias = items; Action escape = () => alias.Add(1); } }")]
    [InlineData("void M() { lock (gate) { Action callback = () => value++; } }")]
    [InlineData("[RequiresLock(nameof(gate))] void Increment() { value++; } void M() { Increment(); }")]
    [InlineData("void M() { Monitor.Exit(gate); }")]
    [InlineData("void M() { Monitor.Enter(gate); }")]
    [InlineData("void M() { Monitor.Enter(gate); Throwing(); Monitor.Exit(gate); }")]
    [InlineData("[RequiresLock(nameof(gate))] void M() { Monitor.Exit(gate); }")]
    [InlineData("async Task M() { Monitor.Enter(gate); try { await Task.Yield(); value++; } finally { Monitor.Exit(gate); } }")]
    [InlineData("IEnumerable<int> M() { lock (gate) { yield return value; } }")]
    [InlineData("void M() { var taken = Monitor.TryEnter(gate); if (taken) Monitor.Exit(gate); if (taken) value++; }")]
    public async Task MissingCapabilitiesAndEscapesAreDiagnosed(string method)
    {
        var diagnostics = await Analyze(Imports + "class C { " + Fields + method + " }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0049");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0046");
    }

    [Theory]
    [InlineData("void M() { lock (gate) value++; }", false)]
    [InlineData("void M() { using (gate.EnterScope()) value++; }", false)]
    [InlineData("void M() { gate.Enter(); try { value++; } finally { gate.Exit(); } }", false)]
    [InlineData("void M() { lock ((object)gate) value++; }", true)]
    [InlineData("void M() { Monitor.Enter(gate); try { value++; } finally { Monitor.Exit(gate); } }", true)]
    [InlineData("void M() { var alias = gate; lock (alias) value++; }", false)]
    public async Task ThreadingLockIsDistinctFromMonitorOnTheSameObject(string method, bool invalid)
    {
        var diagnostics = await Analyze(Imports + "class C { readonly System.Threading.Lock gate = new(); [GuardedBy(nameof(gate))] int value; " + method + " }");
        Assert.True(invalid == diagnostics.Any(diagnostic => diagnostic.Id == "SS0049"), string.Join(Environment.NewLine, diagnostics));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0046");
    }

    [Theory]
    [InlineData("int M() { gate.EnterReadLock(); try { return value; } finally { gate.ExitReadLock(); } }", false)]
    [InlineData("void M() { gate.EnterWriteLock(); try { value++; } finally { gate.ExitWriteLock(); } }", false)]
    [InlineData("int M() { gate.EnterUpgradeableReadLock(); try { return value; } finally { gate.ExitUpgradeableReadLock(); } }", false)]
    [InlineData("void M() { gate.EnterUpgradeableReadLock(); try { gate.EnterWriteLock(); try { value++; } finally { gate.ExitWriteLock(); } } finally { gate.ExitUpgradeableReadLock(); } }", false)]
    [InlineData("void M() { gate.EnterReadLock(); try { value++; } finally { gate.ExitReadLock(); } }", true)]
    [InlineData("void M() { gate.EnterUpgradeableReadLock(); try { value++; } finally { gate.ExitUpgradeableReadLock(); } }", true)]
    [InlineData("void M() { lock (gate) value++; }", true)]
    [InlineData("void M() { if (gate.TryEnterWriteLock(1)) { try { value++; } finally { gate.ExitWriteLock(); } } }", false)]
    [InlineData("int M() { if (gate.TryEnterReadLock(1)) { try { return value; } finally { gate.ExitReadLock(); } } return 0; }", false)]
    [InlineData("void M() { gate.EnterReadLock(); try { gate.EnterWriteLock(); try { value++; } finally { gate.ExitWriteLock(); } } finally { gate.ExitReadLock(); } }", true)]
    [InlineData("[RequiresLock(nameof(gate), LockAccess.Read)] int Read() => value; int M() { gate.EnterReadLock(); try { return Read(); } finally { gate.ExitReadLock(); } }", false)]
    [InlineData("[RequiresLock(nameof(gate))] void Write() { value++; } void M() { gate.EnterReadLock(); try { Write(); } finally { gate.ExitReadLock(); } }", true)]
    public async Task ReaderWriterModesAndUpgradeRulesAreChecked(string method, bool invalid)
    {
        var diagnostics = await Analyze(Imports + "class C { readonly ReaderWriterLockSlim gate = new(); [GuardedBy(nameof(gate))] int value; " + method + " }");
        Assert.True(invalid == diagnostics.Any(diagnostic => diagnostic.Id == "SS0049"), string.Join(Environment.NewLine, diagnostics));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0046");
    }

    [Fact]
    public async Task StaticGuardsAndParameterRootsAreResolvedBySymbol()
    {
        var source = Imports + "class C { static readonly object gate = new(); [GuardedBy(nameof(gate))] static int value; " +
            "[RequiresLock(nameof(gate))] static void Increment() { value++; } " +
            "[RequiresLock(nameof(sync))] static void Call(object sync) { Monitor.Pulse(sync); } " +
            "static void M() { lock (gate) { Increment(); Call(gate); } } }";
        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task ReturnGuardContractRetainsTheCallerReceiver()
    {
        var source = Imports + "class C { " + Fields +
            "[return: GuardedBy(nameof(gate))] List<int> Read() { lock (gate) return items; } " +
            "void Good(C target) { var alias = target.Read(); lock (target.gate) alias.Add(1); } " +
            "void Bad(C target) { var alias = target.Read(); lock (gate) alias.Add(1); } }";
        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0049" && diagnostic.Location.SourceSpan.Start > source.IndexOf("void Bad", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Location.SourceSpan.Start < source.IndexOf("void Bad", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("object gate = new(); [GuardedBy(nameof(gate))] int value;")]
    [InlineData("readonly object gate = new(); [GuardedBy(\"missing\")] int value;")]
    [InlineData("readonly object gate = new(); [GuardedBy(\"gate.ToString\")] int value;")]
    [InlineData("readonly int gate = 1; [GuardedBy(nameof(gate))] int value;")]
    [InlineData("[RequiresLock(\"\")] void M() {}")]
    [InlineData("[RequiresLock(\"this\")] static void M() {}")]
    [InlineData("[RequiresLock(nameof(gate))] static void M(ref object gate) {}")]
    public async Task InvalidGuardDeclarationsAreReportedWithoutAUse(string members)
    {
        var diagnostics = await Analyze(Imports + "class C { " + members + " }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0049");
    }

    [Fact]
    public async Task OverridesCannotStrengthenLockPreconditions()
    {
        var diagnostics = await Analyze(Imports + "class Base { public virtual void M(object gate) {} } " +
            "class Derived : Base { [RequiresLock(nameof(gate))] public override void M(object gate) {} }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0049" && diagnostic.GetMessage().Contains("strengthen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InheritedPreconditionsRenameParametersPositionally()
    {
        var diagnostics = await Analyze(Imports + "interface I { [RequiresLock(nameof(gate))] void M(object gate); } " +
            "class C : I { public void M(object sync) { Monitor.Pulse(sync); } " +
            "void Caller(object gate) { lock (gate) M(gate); } }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DiagnosticContainsComputedHeldStateAndContractEvidence()
    {
        var source = Imports + "class C { " + Fields + "void M() { lock (other) value++; } }";
        var diagnostic = (await Analyze(source)).First(value => value.Id == "SS0049");
        Assert.Equal("gate", diagnostic.Properties["SafeSharp.SynchronizationRequired"]);
        Assert.Contains("this.other [Monitor: 1..1]", diagnostic.Properties["SafeSharp.SynchronizationHeld"]);
        Assert.Equal("Monitor", diagnostic.Properties["SafeSharp.SynchronizationProtocol"]);
        Assert.NotEmpty(diagnostic.AdditionalLocations);
        Assert.Equal("value", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public async Task ExhaustedBudgetCannotPublishAPartialSynchronizationProof()
    {
        var compilation = AnalyzerTestHost.CreateCompilation(Imports + "class C { " + Fields + "void M() { lock (gate) value++; } }");
        var diagnostics = await compilation.WithAnalyzers([new ConcurrencyContractAnalyzer()], new AnalyzerOptions([], new OneStepOptions())).GetAnalyzerDiagnosticsAsync();
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0046");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "AD0001" or "SS0049");
    }


    [Theory]
    [InlineData("void M() { var saved = current; lock (saved.gate) saved.value++; }", false)]
    [InlineData("void M() { var saved = current; current = new C(); lock (saved.gate) saved.value++; }", false)]
    [InlineData("void M() { lock (current.gate) current.value++; }", true)]
    [InlineData("void M() { lock (current.gate) { current = new C(); current.value++; } }", true)]
    [InlineData("void M() { var saved = current; lock (saved.gate) current.value++; }", true)]
    [InlineData("void M() { object selected = gate; ref object alias = ref selected; alias = other; lock (selected) value++; }", true)]
    [InlineData("void M() { object selected = other; ref object alias = ref selected; alias = gate; lock (selected) value++; }", false)]
    [InlineData("void M() { object selected = gate; ref object alias = ref selected; selected = other; lock (alias) value++; }", true)]
    [InlineData("void M() { object a = gate, b = other; ref object selected = ref a; ref object old = ref selected; selected = ref b; lock (old) value++; }", false)]
    [InlineData("void M(bool choose) { object a = gate, b = other; ref object selected = ref (choose ? ref a : ref b); lock (selected) value++; }", true)]
    [InlineData("void M() { ref int alias = ref value; lock (gate) alias++; }", true)]
    [InlineData("void M() { ref int alias = ref value; alias++; }", true)]
    public async Task MutableReceiverSnapshotsAndRefStorageCannotRetainStaleProofs(string method, bool invalid)
    {
        var diagnostics = await Analyze(Imports + "class C { " + Fields + "C current = null!; " + method + " }");
        Assert.True(invalid == diagnostics.Any(diagnostic => diagnostic.Id == "SS0049"), string.Join(Environment.NewLine, diagnostics));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0046");
    }

    [Theory]
    [InlineData("void M() { if (Monitor.IsEntered(gate)) value++; }", false)]
    [InlineData("void M() { if (!Monitor.IsEntered(gate)) return; value++; }", false)]
    [InlineData("void M() { if (Monitor.IsEntered(gate)) Monitor.Exit(gate); }", true)]
    [InlineData("void M() { var taken = Monitor.TryEnter(gate); if (taken == true) { try { value++; } finally { Monitor.Exit(gate); } } }", false)]
    [InlineData("void M() { var taken = Monitor.TryEnter(gate); var copy = taken; if (!copy) return; try { value++; } finally { Monitor.Exit(gate); } }", false)]
    [InlineData("void M() { Monitor.Enter(gate); try { Monitor.Enter(gate); try { value++; } finally { Monitor.Exit(gate); } value++; } finally { Monitor.Exit(gate); } }", false)]
    [InlineData("void M() { lock (gate) { Monitor.Exit(gate); value++; } }", true)]
    [InlineData("void M() { lock (gate) { Monitor.Enter(gate); value++; } }", true)]
    [InlineData("void M() { object selected = gate; Action change = () => selected = other; lock (selected) { change(); Monitor.Pulse(selected); } }", true)]
    [InlineData("void M() { object selected = gate; Action change = () => selected = other; var frozen = selected; lock (frozen) { change(); Monitor.Pulse(frozen); } }", false)]
    [InlineData("[RequiresLock(nameof(sync))] static void Required(object sync) { Monitor.Pulse(sync); } void M() { Action<object> callback = Required; callback(gate); }", true)]
    [InlineData("[RequiresLock(nameof(sync))] delegate void Protected(object sync); [RequiresLock(nameof(sync))] static void Required(object sync) { Monitor.Pulse(sync); } void M() { Protected callback = Required; lock (gate) callback(gate); }", false)]
    public async Task ObservationsReentrancyCallbacksAndSavedConditionsAreChecked(string method, bool invalid)
    {
        var diagnostics = await Analyze(Imports + "class C { " + Fields + method + " }");
        Assert.True(invalid == diagnostics.Any(diagnostic => diagnostic.Id == "SS0049"), string.Join(Environment.NewLine, diagnostics));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0046");
    }

    [Theory]
    [InlineData("void M() { lock (gate) { value++; } value++; }", true)]
    [InlineData("void M() { lock (gate) { lock (gate) value++; value++; } }", false)]
    [InlineData("void M() { lock (gate) { try { Throwing(); } catch { value++; } finally { value++; } } }", false)]
    [InlineData("void M() { try { lock (gate) { Throwing(); } } catch { value++; } }", true)]
    [InlineData("void M() { try { lock (gate) { Throwing(); } } catch when (value > 0) { } }", true)]
    [InlineData("int M() { lock (gate) return value; }", false)]
    [InlineData("void M() { for (int i = 0; i < 2; i++) { lock (gate) { value++; if (i > 0) break; } } }", false)]
    [InlineData("void M() { var scope = gate.EnterScope(); try { value++; } finally { scope.Dispose(); scope.Dispose(); } }", false)]
    [InlineData("void M() { var scope = gate.EnterScope(); var copy = scope; scope.Dispose(); copy.Dispose(); }", true)]
    [InlineData("void M() { if (gate.IsHeldByCurrentThread) value++; }", false)]
    [InlineData("void M() { lock (gate) { gate.Exit(); value++; } }", true)]
    public async Task ThreadingLockCleanupAndScopeValueSemanticsAreExplicit(string method, bool invalid)
    {
        var diagnostics = await Analyze(Imports + "class C { readonly System.Threading.Lock gate = new(); [GuardedBy(nameof(gate))] int value; static void Throwing() {} " + method + " }");
        Assert.True(invalid == diagnostics.Any(diagnostic => diagnostic.Id == "SS0049"), string.Join(Environment.NewLine, diagnostics));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0046");
    }

    [Fact]
    public async Task ASourceRefReturnPreservesTheActualGuardCell()
    {
        var diagnostics = await Analyze(Imports + "class C { " + Fields +
            "static ref object Identity(ref object cell) => ref cell; void M() { object selected = other; ref object alias = ref Identity(ref selected); alias = gate; lock (selected) value++; } }");
        Assert.Empty(diagnostics);
    }


    [Theory]
    [InlineData("[RequiresLock(nameof(sync))] public C(object sync) {} static void M(object gate) { lock (gate) _ = new C(gate); }", false)]
    [InlineData("[RequiresLock(nameof(sync))] public C(object sync) {} static void M(object gate) { _ = new C(gate); }", true)]
    [InlineData("static readonly object gate = new(); [RequiresLock(nameof(gate))] public C() {} static void M() { lock (gate) _ = new C(); }", false)]
    [InlineData("static readonly object gate = new(); [RequiresLock(nameof(gate))] public C() {} static void M() { _ = new C(); }", true)]
    [InlineData("[RequiresLock(\"this\")] public C() {} static void M() { _ = new C(); }", true)]
    [InlineData("readonly object gate = new(); [GuardedBy(nameof(gate))] int value; static void M(C instance) { _ = instance.value; }", true)]
    public async Task ConstructorsValidateCallerLockPreconditions(string members, bool invalid)
    {
        var diagnostics = await Analyze(Imports + "class C { " + members + " }");
        Assert.True(invalid == diagnostics.Any(diagnostic => diagnostic.Id == "SS0049"), string.Join(Environment.NewLine, diagnostics));
    }

    [Theory]
    [InlineData("object selected = gate; lock (selected) { try { _ = new D(ref selected, other); } catch { Monitor.Pulse(selected); } }", true)]
    [InlineData("object selected = gate; var frozen = selected; lock (frozen) { try { _ = new D(ref selected, other); } catch { Monitor.Pulse(frozen); } }", false)]
    [InlineData("object a = gate, b = other; ref object selected = ref a; try { _ = new D(ref selected, (selected = ref b)); } catch { } lock (a) value++;", true)]
    public async Task ConstructorRefEffectsUseEvaluatedStorageOnNormalAndExceptionalPaths(string body, bool invalid)
    {
        var diagnostics = await Analyze(Imports + "class D { public D(ref object selected, object replacement) { selected = replacement; throw new Exception(); } } " +
            "class C { " + Fields + "void M() { " + body + " } }");
        Assert.True(invalid == diagnostics.Any(diagnostic => diagnostic.Id == "SS0049"), string.Join(Environment.NewLine, diagnostics));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0046");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InheritedGuardedPropertiesRetainTheirBaseRequirement(bool guarded)
    {
        var diagnostics = await Analyze(Imports + "class B { [GuardedBy(\"this\")] public virtual int Value => 0; } " +
            "class D : B { public override int Value => 0; static int M(D target) { " + (guarded ? "lock (target) return target.Value;" : "return target.Value;") + " } }");
        Assert.True(guarded != diagnostics.Any(diagnostic => diagnostic.Id == "SS0049"), string.Join(Environment.NewLine, diagnostics));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplementedGuardedPropertiesRetainTheirInterfaceRequirement(bool guarded)
    {
        var diagnostics = await Analyze(Imports + "interface I { [GuardedBy(\"this\")] int Value { get; } } " +
            "class D : I { public int Value => 0; static int M(D target) { " + (guarded ? "lock (target) return target.Value;" : "return target.Value;") + " } }");
        Assert.True(guarded != diagnostics.Any(diagnostic => diagnostic.Id == "SS0049"), string.Join(Environment.NewLine, diagnostics));
    }

    [Fact]
    public async Task BorrowedNoEscapeArgumentsRequireOnlyAReadCapability()
    {
        var diagnostics = await Analyze(Imports + "class C { readonly ReaderWriterLockSlim gate = new(); [GuardedBy(nameof(gate))] readonly List<int> items = new(); " +
            "static void Read([Borrowed, DoesNotEscape] List<int> value) {} void M() { gate.EnterReadLock(); try { Read(items); } finally { gate.ExitReadLock(); } } }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CopyingAnImmutableValueThroughARefAliasEndsItsGuardRequirement()
    {
        var diagnostics = await Analyze(Imports + "class C { " + Fields +
            "void M() { int copy; lock (gate) { ref int alias = ref value; copy = alias; } Console.WriteLine(copy); } }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task FilterObservationDoesNotInventALeakForAnUnacquiredLexicalGuard()
    {
        var diagnostics = await Analyze(Imports + "class C { readonly System.Threading.Lock gate = new(); [GuardedBy(nameof(gate))] int value; static void Throwing() {} " +
            "void M() { try { lock (gate) { Throwing(); } } catch when (gate.IsHeldByCurrentThread && value > 0) { } } }");
        Assert.Empty(diagnostics);
    }


    [Fact]
    public async Task GuardedPropertyAccessorsAssumeTheCallerCapability()
    {
        var diagnostics = await Analyze(Imports + "class C { readonly object gate = new(); [GuardedBy(nameof(gate))] int backing; " +
            "[GuardedBy(nameof(gate))] int Value { get => backing; set => backing = value; } void M() { lock (gate) Value = Value + 1; } }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ASetterCannotReuseAPreconditionReleasedByItsValueExpression()
    {
        var diagnostics = await Analyze(Imports + "class C { readonly object gate = new(); [GuardedBy(nameof(gate))] int Value { get; set; } " +
            "void M() { lock (gate) Value = (Monitor.IsEntered(gate) ? Exit() : 0); } int Exit() { Monitor.Exit(gate); return 0; } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0049");
    }

    [Theory]
    [InlineData("class B { public virtual int Value => 0; } class D : B { [GuardedBy(\"this\")] public override int Value => 0; }")]
    [InlineData("interface I { int Value { get; } } class D : I { [GuardedBy(\"this\")] public int Value => 0; }")]
    [InlineData("class B { public virtual List<int> Get() => new(); } class D : B { [return: GuardedBy(\"this\")] public override List<int> Get() => new(); }")]
    [InlineData("[RequiresLock(\"\")] delegate void Callback(object gate);")]
    [InlineData("[RequiresLock(\"missing\")] delegate void Callback(object gate);")]
    public async Task IncompatibleOrMalformedContractsAreDiagnosedWithoutCallSites(string declarations)
    {
        var diagnostics = await Analyze(Imports + declarations);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0049");
    }


    [Theory]
    [InlineData("public static class Monitor { public static void Enter(object gate) {} public static void Exit(object gate) {} }", "object", "Monitor.Enter(gate); try { value++; } finally { Monitor.Exit(gate); }")]
    [InlineData("public sealed class Lock { public void Enter() {} public void Exit() {} }", "Lock", "gate.Enter(); try { value++; } finally { gate.Exit(); }")]
    [InlineData("public sealed class ReaderWriterLockSlim { public void EnterWriteLock() {} public void ExitWriteLock() {} }", "ReaderWriterLockSlim", "gate.EnterWriteLock(); try { value++; } finally { gate.ExitWriteLock(); }")]
    public async Task SourceLookalikesCannotGrantFrameworkCapabilities(string lookalike, string type, string body)
    {
        var diagnostics = await Analyze(Imports + "namespace System.Threading { " + lookalike + " } class C { readonly " + type +
            " gate = new(); [GuardedBy(nameof(gate))] int value; void M() { " + body + " } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0049" && diagnostic.Properties.TryGetValue("SafeSharp.SynchronizationRequired", out var required) && required == "gate");
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
