using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Tests.Semantics;

public sealed class ConcurrencyContractTests
{
    private const string Imports = "using System; using System.Threading.Tasks; using SafeSharp; ";
    private const string Models = "[Send] sealed class Payload { public int Value; public void Touch() { Value++; } } " +
        "[Send, Sync] sealed class Message { public readonly string Text = string.Empty; public void Read() {} } ";
    private const string Boundaries = "static void Share([SharesAcrossThreads] Action callback) {} static void Send([TransfersToThread] Action callback) {} ";

    [Theory]
    [InlineData("[Send] sealed class C { int counter; }")]
    [InlineData("[Sync] sealed class C { readonly int counter; }")]
    [InlineData("[Send, Sync] readonly struct C { readonly decimal value; }")]
    [InlineData("[Send, Sync] sealed record C(string Name, int Count);")]
    [InlineData("[Sync] sealed class C { public string Name { get; init; } = string.Empty; }")]
    [InlineData("[Sync] sealed class C { public string Name { get; } = string.Empty; }")]
    [InlineData("[Sync] sealed class C { readonly Guid id; readonly DateTime instant; readonly DateTimeOffset offset; readonly TimeSpan delay; }")]
    [InlineData("[Sync] sealed class C { readonly int? number; readonly (int Number, string Text) pair; }")]
    [InlineData("[Sync] sealed class C<[Sync] T> { readonly T value = default!; }")]
    [InlineData("[Send] sealed class C<[Send] T> { T value = default!; }")]
    [InlineData("[Send] sealed class Value { int state; } [Send] sealed class C { [Owned] readonly Value value = new(); }")]
    [InlineData("[Send] sealed class Value { int state; } [Send] sealed class C { [field: Owned] Value Value { get; } = new(); }")]
    [InlineData("[Send, Sync] sealed class Value { readonly int state; } [Send] sealed class C { readonly Value value = new(); }")]
    [InlineData("[Send] sealed class C { [Owned] int[] items = []; }")]
    [InlineData("[Sync, ThreadSafe] sealed class C { int counter; object state = new(); }")]
    [InlineData("[Sync] interface IShared {} sealed class C : IShared { readonly int number; }")]
    [InlineData("[Send] interface ITransferable {} sealed class C : ITransferable { int number; }")]
    [InlineData("[Sync] class Base { readonly int first; } sealed class C : Base { readonly int second; }")]
    [InlineData("[Sync] sealed class C { readonly C? next; readonly int value; }")]
    [InlineData("[Send] sealed class C { [Owned] C? next; int value; }")]
    [InlineData("[Sync] interface IShared {} [Sync] sealed class C<T> where T : IShared { readonly T value = default!; }")]
    [InlineData("[ThreadSafe] class Shared {} [Sync] sealed class C<T> where T : Shared { readonly T value = default!; }")]
    public Task Valid_structural_declarations(string source) => NoDiagnostic(Imports + source);

    [Theory]
    [InlineData("[Sync] sealed class [|C|] { int counter; }")]
    [InlineData("[Sync] sealed class [|C|] { public int Count { get; set; } }")]
    [InlineData("[Sync] sealed class [|C|] { public int Count { get; private set; } }")]
    [InlineData("[Sync] sealed class [|C|] { readonly object unknown = new(); }")]
    [InlineData("[Sync] sealed class [|C|] { readonly int[] mutable = []; }")]
    [InlineData("[Send] unsafe sealed class [|C|] { int* pointer; }")]
    [InlineData("[Send] sealed class [|C|] { dynamic value = new object(); }")]
    [InlineData("[Send] sealed class [|C|] { IntPtr address; }")]
    [InlineData("[Send] sealed class [|C|]<T> { T value = default!; }")]
    [InlineData("[Sync] sealed class [|C|]<T> { readonly T value = default!; }")]
    [InlineData("[Send, ThreadAffine] sealed class [|C|] {}")]
    [InlineData("[Sync, ThreadAffine] sealed class [|C|] {}")]
    [InlineData("[ThreadAffine] sealed class Handle {} [Sync, ThreadSafe] sealed class [|C|] { readonly Handle value = new(); }")]
    [InlineData("[ThreadAffine] sealed class Handle {} class Base { readonly Handle value = new(); } class Derived : Base {} [Sync, ThreadSafe] sealed class [|C|] { readonly Derived value = new(); }")]
    [InlineData("[ThreadAffine] sealed class Handle {} [Sync, ThreadSafe] sealed class [|C|] { readonly (Handle Handle, int Number) value; }")]
    [InlineData("[ThreadAffine] struct Handle {} [Sync, ThreadSafe] sealed class [|C|] { readonly Handle? value; }")]
    [InlineData("[ThreadAffine] sealed class Handle {} class Base { readonly Handle value = new(); } [Sync, ThreadSafe] sealed class [|C|]<T> where T : Base { readonly T value = default!; }")]
    [InlineData("[ThreadAffine] sealed class Handle {} [Send] sealed class [|C|] { [Owned] Handle value = new(); }")]
    [InlineData("[Send] sealed class Value { int state; } [Send] sealed class [|C|] { Value value = new(); }")]
    [InlineData("[Sync] interface IShared {} sealed class [|C|] : IShared { int number; }")]
    [InlineData("[Sync] class Base { readonly int first; } sealed class [|C|] : Base { int second; }")]
    [InlineData("[Sync] sealed class [|C|] { readonly C? next; int state; }")]
    [InlineData("[Sync] sealed class Value { readonly int value; } [Send] sealed class [|C|] { readonly Value value = new(); }")]
    public Task Invalid_structural_declarations_have_precise_primary_span(string source) => OneDiagnostic(Imports + source, "SS0044");

    [Theory]
    [InlineData("class Box<[Send] T> {} class C { Box<[|object|]> field = new(); }")]
    [InlineData("class Box<[Sync] T> {} class C { Box<[|int[]|]> field = new(); }")]
    [InlineData("class C { static void Accept<[Send] T>(T value) {} void M() { [|Accept(new object())|]; } }")]
    [InlineData("class C { static void Accept<[Sync] T>(T value) {} void M() { Accept<[|object|]>(new object()); } }")]
    [InlineData("[ThreadAffine] class Affine {} class C<[Send] [|T|]> where T : Affine {}")]
    [InlineData("class Base { public virtual void Accept<[Send] T>(T value) {} } class Derived : Base { public override void Accept<T>(T value) {} } class C { void M() { [|new Derived().Accept(new object())|]; } }")]
    public Task Generic_contract_requirements_are_checked(string source) => OneDiagnostic(Imports + source, "SS0044");

    [Theory]
    [InlineData("class Box<[Send] T> {} class C { Box<int> field = new(); }")]
    [InlineData("class C { static void Accept<[Sync] T>(T value) {} void M() { Accept(\"immutable\"); } }")]
    [InlineData("class C { static void Accept<[Send] T>(T value) {} void M<[Send] T>(T value) { Accept(value); } }")]
    [InlineData("class Base { public virtual void Accept<[Sync] T>(T value) {} } class Derived : Base { public override void Accept<T>(T value) { Other(value); } static void Other<[Sync] T>(T value) {} }")]
    public Task Generic_contracts_accept_valid_instantiations(string source) => NoDiagnostic(Imports + source);

    [Theory]
    [InlineData("void M() { var message = new Message(); Task.Run(() => message.Read()); }")]
    [InlineData("void M() { var message = new Message(); Share(() => message.Read()); }")]
    [InlineData("void M() { var payload = new Payload(); Send(() => payload.Touch()); }")]
    [InlineData("void M() { int number = 42; Share(() => Console.WriteLine(number)); }")]
    [InlineData("void M() { var payload = new Payload(); Send(payload.Touch); payload = new Payload(); }")]
    [InlineData("void M() { var message = new Message(); Action callback = message.Read; Share(callback); }")]
    [InlineData("void M() { var payload = new Payload(); Action callback = () => payload.Touch(); Send(callback); }")]
    [InlineData("void M() { var message = new Message(); Action first = () => message.Read(); Action second = first; Share(second); }")]
    [InlineData("void M() { var message = new Message(); void Read() => message.Read(); Share(Read); }")]
    [InlineData("void M() { var payload = new Payload(); Share(() => Console.WriteLine(nameof(payload))); }")]
    [InlineData("void M() { Share(() => { var payload = new Payload(); payload.Touch(); }); }")]
    [InlineData("void M() { Task.Run(() => { var payload = new Payload(); payload.Touch(); }); }")]
    [InlineData("void M() { object state = new(); Task.Run(() => Console.WriteLine(state)); }")]
    [InlineData("static readonly Message Shared = new(); void M() { Share(() => Shared.Read()); }")]
    [InlineData("void M([SharesAcrossThreads] Action callback) { Share(callback); }")]
    [InlineData("void M([TransfersToThread] Action callback) { Send(callback); }")]
    public Task Supported_concurrency_boundaries_accept_valid_captures(string methods) => NoDiagnostic(Imports + Models + "class C { " + Boundaries + methods + " }");

    [Theory]
    [InlineData("void M() { var payload = new Payload(); Task.Run(() => [|payload|].Touch()); }")]
    [InlineData("void M() { var payload = new Payload(); Share(() => [|payload|].Touch()); }")]
    [InlineData("void M() { var payload = new Payload(); Share([|payload|].Touch); }")]
    [InlineData("void M() { object state = new(); Send(() => Console.WriteLine([|state|])); }")]
    [InlineData("void M() { int number = 42; Share(() => [|number|]++); }")]
    [InlineData("void M() { int number = 42; Send(() => [|number|]++); }")]
    [InlineData("void M() { int number = 42; ref int alias = ref number; Share(() => Console.WriteLine([|number|])); alias++; }")]
    [InlineData("void M() { var message = new Message(); Task.Run(() => [|message|].Read()); message = new Message(); }")]
    [InlineData("void M() { var payload = new Payload(); Action callback = () => [|payload|].Touch(); Share(callback); }")]
    [InlineData("void M() { var payload = new Payload(); void Read() => [|payload|].Touch(); Share(Read); }")]
    [InlineData("void M(Action callback) { Share([|callback|]); }")]
    [InlineData("static Message Shared = new(); void M() { Share(() => [|Shared|].Read()); }")]
    public Task Unsafe_captures_report_values_and_boundary_evidence(string methods) => OneDiagnostic(Imports + Models + "class C { " + Boundaries + methods + " }", "SS0045");

    [Fact]
    public Task Sharing_contract_does_not_imply_transfer() => OneDiagnostic(Imports +
        "[Sync] sealed class Shared { readonly int value; public void Read() {} } class C { " + Boundaries +
        "void M() { var shared = new Shared(); Send(() => [|shared|].Read()); } }", "SS0045");

    [Fact]
    public Task Inherited_boundary_parameter_contracts_apply_to_implementation_calls() => OneDiagnostic(Imports +
        "interface IQueue { void Queue([SharesAcrossThreads] Action callback); } sealed class Dispatcher : IQueue { public void Queue(Action callback) {} } " + Models +
        "class C { void M() { var payload = new Payload(); new Dispatcher().Queue(() => [|payload|].Touch()); } }", "SS0045");

    [Theory]
    [InlineData("class C { void M([SharesAcrossThreads] object [|callback|]) {} }")]
    [InlineData("class C { void M([SharesAcrossThreads, TransfersToThread] Action [|callback|]) {} }")]
    public Task Invalid_boundary_contracts_are_diagnosed(string source) => OneDiagnostic(Imports + source, "SS0044");

    [Fact]
    public async Task Composition_witness_is_preserved_in_diagnostic_properties()
    {
        var diagnostic = Assert.Single(await Analyze(Imports + "[Sync] sealed class Container { readonly object state = new(); }"));
        Assert.Equal("Sync", diagnostic.Properties["SafeSharp.ConcurrencyCapability"]);
        Assert.Contains("state", diagnostic.Properties["SafeSharp.ConcurrencyPath"]);
        Assert.Contains("no explicit", diagnostic.Properties["SafeSharp.ConcurrencyReason"]);
        Assert.NotEmpty(diagnostic.AdditionalLocations);
    }

    [Fact]
    public async Task Callback_report_contains_original_capture_and_boundary_spans()
    {
        var source = Imports + Models + "class C { " + Boundaries + "void M() { var payload = new Payload(); Share(() => payload.Touch()); } }";
        var diagnostic = Assert.Single(await Analyze(source));
        Assert.Equal("payload", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        Assert.Contains(diagnostic.AdditionalLocations, location => source.Substring(location.SourceSpan.Start, location.SourceSpan.Length) == "Share(() => payload.Touch())");
    }

    [Fact]
    public async Task Small_budget_reports_incomplete_analysis_without_analyzer_exception()
    {
        var compilation = AnalyzerTestHost.CreateCompilation(Imports + "[Sync] sealed class C { readonly int value; }");
        var diagnostics = await compilation.WithAnalyzers([new ConcurrencyContractAnalyzer()], new AnalyzerOptions([], new SmallBudgetOptions())).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0046");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0044");
    }

    [Fact]
    public async Task Delegate_alias_budget_is_explicit_and_bounded()
    {
        var declarations = string.Join(" ", Enumerable.Range(1, 130).Select(index => "Action callback" + index + " = callback" + (index - 1) + ";"));
        var diagnostics = await Analyze(Imports + "class C { " + Boundaries + "void M() { Action callback0 = () => {}; " + declarations + " Share(callback130); } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0046");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
    }

    [Fact]
    public async Task Recursive_composition_does_not_cache_an_assumed_success()
    {
        var diagnostics = await Analyze(Imports + "[Sync] sealed class First { readonly Second? next; int state; } [Sync] sealed class Second { readonly First? next; }");
        Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.Id == "SS0044"));
        Assert.All(diagnostics, diagnostic => Assert.Contains("state", diagnostic.Properties["SafeSharp.ConcurrencyPath"]));
    }

    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> Analyze(string source) =>
        AnalyzerTestHost.GetDiagnosticsAsync(source, new ConcurrencyContractAnalyzer());
    private static async Task NoDiagnostic(string source) => Assert.Empty(await Analyze(source));
    private static async Task OneDiagnostic(string source, string id)
    {
        var start = source.IndexOf("[|", StringComparison.Ordinal); var end = source.IndexOf("|]", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var clean = source.Replace("[|", "", StringComparison.Ordinal).Replace("|]", "", StringComparison.Ordinal);
        var diagnostic = Assert.Single(await Analyze(clean));
        Assert.Equal(id, diagnostic.Id);
        Assert.Equal(start, diagnostic.Location.SourceSpan.Start);
        Assert.Equal(end - start - 2, diagnostic.Location.SourceSpan.Length);
        Assert.Equal("Fixture0.cs", diagnostic.Location.SourceTree!.FilePath);
    }
    private sealed class SmallBudgetOptions : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions Values = new Options();
        public override AnalyzerConfigOptions GlobalOptions => Values;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Values;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Values;
        private sealed class Options : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value) { value = "1"; return key == "safesharp_max_contract_steps"; }
        }
    }
}
