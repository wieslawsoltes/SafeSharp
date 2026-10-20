using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Tests.Semantics;

public sealed class BorrowedEscapeTests
{
    [Fact]
    public Task Local_alias_reaches_field() => Escape("class C { object? field; void M([Borrowed] object value) { object alias = value; field = [|alias|]; } }");

    [Fact]
    public Task Parameter_reassignment_kills_old_origin() => Clean("class C { object? field; void M([Borrowed] object value) { value = new object(); field = value; } }");

    [Fact]
    public Task Alias_reassignment_kills_old_origin() => Clean("class C { object? field; void M([Borrowed] object value) { object alias = value; alias = new object(); field = alias; } }");

    [Fact]
    public Task Null_reassignment_kills_old_origin() => Clean("class C { object? field; void M([Borrowed] object value) { object? alias = value; alias = null; field = alias; } }");

    [Fact]
    public Task Property_retains_aliased_origin() => Escape("class C { object? Value { get; set; } void M([BorrowedMut] object value) { var alias = value; Value = [|alias|]; } }");

    [Fact]
    public Task Indexer_retains_aliased_origin() => Escape("class C { object? this[int i] { get => null; set {} } void M([Borrowed] object value) { var alias = value; this[0] = [|alias|]; } }");

    [Fact]
    public Task External_array_retains_origin() => Escape("class C { void M([Borrowed] object value, object[] destination) { destination[0] = [|value|]; } }");

    [Fact]
    public Task Ref_output_retains_origin() => Escape("class C { void M([Borrowed] object value, ref object destination) { destination = [|value|]; } }");

    [Fact]
    public Task Out_output_retains_origin() => Escape("class C { void M([Borrowed] object value, out object destination) { destination = [|value|]; } }");

    [Fact]
    public Task Ref_local_to_field_retains_origin() => Escape("class C { object field = new(); void M([Borrowed] object value) { ref object target = ref field; target = [|value|]; } }");

    [Fact]
    public Task Ref_local_to_local_keeps_local_storage() => Clean("class C { void M([Borrowed] object value) { object cell = new(); ref object alias = ref cell; alias = value; } }");

    [Fact]
    public Task Ref_local_write_changes_subsequent_value_origin() => Escape("class C { object? field; void M([Borrowed] object value) { object cell = new(); ref object alias = ref cell; alias = value; field = [|cell|]; } }");

    [Fact]
    public Task Ref_reassignment_changes_storage_target() => Clean("class C { object field = new(); void M([Borrowed] object value) { object cell = new(); scoped ref object target = ref field; target = ref cell; target = value; } }");

    [Fact]
    public Task Local_array_tracks_contents_until_it_escapes() => Escape("class C { object[]? field; void M([Borrowed] object value) { var local = new object[1]; local[0] = value; field = [|local|]; } }");

    [Fact]
    public Task Local_array_does_not_itself_escape() => Clean("class C { void M([Borrowed] object value) { var local = new object[1]; local[0] = value; System.Console.WriteLine(local[0]); } }");

    [Fact]
    public Task Container_alias_preserves_shared_contents() => Escape("class Box { public object? Value; } class C { Box? field; void M([Borrowed] object value) { var local = new Box(); var alias = local; alias.Value = value; field = [|local|]; } }");

    [Fact]
    public Task Object_initializer_preserves_contents() => Escape("class Box { public object? Value; } class C { Box? field; void M([Borrowed] object value) { var local = new Box { Value = value }; field = [|local|]; } }");

    [Fact]
    public Task Anonymous_object_preserves_contents() => Escape("class C { object? field; void M([Borrowed] object value) { var local = new { Value = value }; field = [|local|]; } }");

    [Fact]
    public Task Nested_tuple_and_array_preserve_contents() => Escape("class C { object? field; void M([Borrowed] object value) { var local = (new[] { value }, 1); field = [|local|]; } }");

    [Fact]
    public Task Source_identity_summary_preserves_alias() => Escape("class C { object? field; static object Identity(object source) { object local = source; return local; } void M([Borrowed] object value) { var alias = Identity(value); field = [|alias|]; } }");

    [Fact]
    public Task Known_fresh_source_summary_clears_origin() => Clean("class C { object? field; static object Clone(object source) { return new object(); } void M([Borrowed] object value) { var alias = Clone(value); field = alias; } }");

    [Fact]
    public Task Explicit_borrowed_summary_preserves_origin() => Escape("class C { object? field; [ReturnsBorrowed(\"source\")] static object Identity(object source) => source; void M([Borrowed] object value) { var alias = Identity(value); field = [|alias|]; } }");

    [Fact]
    public Task Inherited_interface_parameter_contract_is_positional() => Escape("interface I { void M([Borrowed] object original); } class C : I { object? field; public void M(object renamed) { var alias = renamed; field = [|alias|]; } }");

    [Fact]
    public Task Inherited_override_noescape_contract_is_positional() => Escape("abstract class B { public abstract void M([DoesNotEscape] object original); } class C : B { object? field; public override void M(object renamed) { field = [|renamed|]; } }");

    [Fact]
    public Task Inherited_borrowed_result_source_maps_renamed_parameter() => Escape("interface I { [ReturnsBorrowed(\"original\")] object Select(object original); } sealed class Selector : I { public object Select(object renamed) => renamed; } class C { object? field; void M([Borrowed] object value, Selector selector) { var alias = selector.Select(value); field = [|alias|]; } }");

    [Fact]
    public Task Extension_receiver_borrowed_summary_preserves_origin() => Escape("static class Extensions { [ReturnsBorrowed(\"source\")] public static object View(this object source) => source; } class C { object? field; void M([Borrowed] object value) { var alias = value.View(); field = [|alias|]; } }");

    [Fact]
    public Task Borrowed_field_read_preserves_receiver_origin() => Escape("class Box { public object Value = new(); } class C { object? field; void M([Borrowed] Box value) { var alias = value.Value; field = [|alias|]; } }");

    [Fact]
    public Task Fresh_property_getter_clears_receiver_origin() => Clean("sealed class Box { public object Value => new object(); } class C { object? field; void M([Borrowed] Box value) { field = value.Value; } }");

    [Fact]
    public Task Primitive_property_read_does_not_retain_receiver() => Clean("class C { int field; void M([Borrowed] string value) { field = value.Length; } }");

    [Fact]
    public Task Passing_argument_to_opaque_call_is_not_an_inferred_escape() => Clean("interface I { void Observe(object value); } class C { void M([DoesNotEscape] object value, I observer) { observer.Observe(value); } }");

    [Fact]
    public Task Opaque_call_result_is_not_an_inferred_borrow() => Clean("interface I { object Clone(object value); } class C { object? field; void M([Borrowed] object value, I factory) { field = factory.Clone(value); } }");

    [Fact]
    public Task Nameof_does_not_capture_parameter() => Clean("class C { System.Func<string> M([Borrowed] object value) { return () => nameof(value); } }");

    [Fact]
    public Task Borrowed_bound_method_group_cannot_be_returned_as_unbounded_delegate() => Escape("class C { System.Func<string?> M([Borrowed] object value) { return [|value|].ToString; } }");

    [Fact]
    public Task Event_subscription_retains_borrowed_delegate() => Escape("class C { event System.Action? Changed; void M([Borrowed] System.Action callback) { var alias = callback; Changed += [|alias|]; } }");

    [Fact]
    public Task Event_unsubscription_does_not_retain_argument() => Clean("class C { event System.Action? Changed; void M([Borrowed] System.Action callback) { Changed -= callback; } }");

    [Fact]
    public Task Conditional_expression_merges_origins() => Escape("class C { object? field; void M([Borrowed] object value, bool condition) { object alias = condition ? value : new object(); field = [|alias|]; } }");

    [Fact]
    public Task Pattern_binding_preserves_origin() => Escape("class C { object? field; void M([Borrowed] object value) { if (value is string alias) field = [|alias|]; } }");

    [Fact]
    public Task Explicit_async_return_contract_preserves_payload_loan() => Escape("class C { object? field; [ReturnsBorrowed(\"input\")] static System.Threading.Tasks.Task<object> Wrap(object input) => System.Threading.Tasks.Task.FromResult(input); async System.Threading.Tasks.Task M([Borrowed] object value) { var alias = await Wrap(value); field = [|alias|]; } }");

    [Fact]
    public Task Task_constructor_capture_is_not_a_result_identity() => Clean("class C { object? field; async System.Threading.Tasks.Task M([Borrowed] object value) { var task = new System.Threading.Tasks.Task<object>(() => { System.Console.WriteLine(value); return new object(); }); task.Start(); field = await task; } }");

    [Fact]
    public Task String_concatenation_does_not_retain_source_identity() => Clean("class C { string? field; void M([Borrowed] string value) { field = value + \"suffix\"; string alias = value; alias += \"suffix\"; field = alias; } }");

    [Theory]
    [InlineData("Identity(value)")]
    [InlineData("Ownership.Borrow(value)")]
    [InlineData("Ownership.BorrowMut(value)")]
    public Task Inferred_or_generic_task_identity_is_a_carrier_identity(string expression) => Clean("class C { object? field; static System.Threading.Tasks.Task<object> Identity(System.Threading.Tasks.Task<object> task) => task; async System.Threading.Tasks.Task M([Borrowed] System.Threading.Tasks.Task<object> value) { field = await " + expression + "; } }");

    [Fact]
    public Task Nameof_and_interpolation_do_not_retain_noescape_parameter() => Clean("class C { string? field; void M([DoesNotEscape] object value) { field = nameof(value); field = $\"{value}\"; } }");

    [Fact]
    public Task Owned_output_uses_earlier_ref_target_before_later_argument_rebinding() => Escape("class C { object? field; static void Fill([Owned] out object output, object ignored) { output = new object(); } void M([Borrowed] object value) { object first = value; object second = value; scoped ref object alias = ref first; Fill(out alias, alias = ref second); field = [|second|]; } }");

    [Fact]
    public async Task Diagnostic_contains_parameter_destination_and_possible_certainty()
    {
        const string source = "using SafeSharp; class C { object? field; void M([Borrowed] object value) { var alias = value; field = alias; } }";
        var diagnostic = Assert.Single(await SemanticsAssert.AnalyzeAsync(source));
        Assert.Equal("SS0027", diagnostic.Id);
        Assert.Equal("value", diagnostic.Properties["SafeSharp.BorrowedSource"]);
        Assert.Equal("field", diagnostic.Properties["SafeSharp.EscapeDestination"]);
        Assert.Equal("Possible", diagnostic.Properties["SafeSharp.Certainty"]);
        Assert.Equal("false", diagnostic.Properties["SafeSharp.DoesNotEscape"]);
        Assert.Contains(diagnostic.AdditionalLocations, location => source.Substring(location.SourceSpan.Start, location.SourceSpan.Length) == "value");
        Assert.Contains(diagnostic.AdditionalLocations, location => source.Substring(location.SourceSpan.Start, location.SourceSpan.Length) == "field");
    }

    [Fact]
    public async Task Tiny_budget_reports_incomplete_instead_of_partial_proof()
    {
        var compilation = AnalyzerTestHost.CreateCompilation("using SafeSharp; class C { object? field; void M([Borrowed] object value) { var alias = value; field = alias; } }");
        var diagnostics = await compilation.WithAnalyzers([new SafeSharpSemanticAnalyzer()], new AnalyzerOptions([], new SmallBudgetOptions()))
            .GetAnalyzerDiagnosticsAsync();
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SS0046", diagnostic.Id);
        Assert.Contains("borrowed escape provenance", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Concurrent_analysis_has_deterministic_diagnostics()
    {
        const string source = "using SafeSharp; class C { object? field; void M([Borrowed] object first, [BorrowedMut] object second, bool flag) { object local = first; while (flag) { field = local; local = second; } } }";
        var runs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SemanticsAssert.AnalyzeAsync(source)));
        var expected = runs[0].Select(Identity).ToArray();
        Assert.NotEmpty(expected);
        Assert.All(runs, diagnostics => Assert.Equal(expected, diagnostics.Select(Identity)));
        static string Identity(Diagnostic diagnostic) => diagnostic.Id + ":" + diagnostic.Location.SourceSpan + ":" + diagnostic.GetMessage();
    }

    [Fact]
    public async Task Canceled_analysis_does_not_report_partial_results()
    {
        var compilation = AnalyzerTestHost.CreateCompilation("using SafeSharp; class C { object? field; void M([Borrowed] object value) { object alias = value; field = alias; } }");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compilation.WithAnalyzers([new SafeSharpSemanticAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(cancellation.Token));
    }

    private static Task Escape(string source) => SemanticsAssert.DiagnosticAsync("using SafeSharp; " + source, "SS0027");
    private static Task Clean(string source) => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; " + source);

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
