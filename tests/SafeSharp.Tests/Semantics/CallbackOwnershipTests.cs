namespace SafeSharp.Tests.Semantics;

public sealed class CallbackOwnershipTests
{
    [Fact]
    public Task MustUse_method_result_must_be_consumed() => SemanticsAssert.DiagnosticAsync("using SafeSharp; class C { [MustUse] int Create() => 1; void M() { [|Create()|]; } }", "SS0024");

    [Fact]
    public Task MustUse_type_result_must_be_consumed() => SemanticsAssert.DiagnosticAsync("using SafeSharp; [MustUse] class Result {} class C { Result Create() => new(); void M() { [|Create()|]; } }", "SS0024");

    [Fact]
    public Task MustUse_construction_must_be_consumed() => SemanticsAssert.DiagnosticAsync("using SafeSharp; [MustUse] class Result {} class C { void M() { [|new Result()|]; } }", "SS0024");

    [Fact]
    public Task Explicit_discard_acknowledges_MustUse() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; class C { [MustUse] int Create() => 1; void M() { _ = Create(); } }");

    [Fact]
    public Task Assigned_MustUse_is_consumed() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; class C { [MustUse] int Create() => 1; int M() { var result = Create(); return result; } }");

    [Fact]
    public Task Await_consumes_non_generic_task_completion() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; using System.Threading.Tasks; class C { [MustUse] Task Create() => Task.CompletedTask; async Task M() { await Create(); } }");

    [Fact]
    public Task Value_result_after_await_can_be_ignored() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using System.Threading.Tasks; class C { [MustUse] Task<int> Create() => Task.FromResult(1); async Task M() { await [|Create()|]; } }", "SS0024");

    [Fact]
    public Task Shared_parameter_cannot_be_stored_in_field() => SemanticsAssert.DiagnosticAsync("using SafeSharp; class C { object? field; void M([Borrowed] object value) { field = [|value|]; } }", "SS0027");

    [Fact]
    public Task Mutable_parameter_cannot_be_stored_in_property() => SemanticsAssert.DiagnosticAsync("using SafeSharp; class C { object? Value { get; set; } void M([BorrowedMut] object value) { Value = [|value|]; } }", "SS0027");

    [Fact]
    public Task No_escape_parameter_cannot_be_stored() => SemanticsAssert.DiagnosticAsync("using SafeSharp; class C { object? field; void M([DoesNotEscape] object value) { field = [|value|]; } }", "SS0027");

    [Fact]
    public Task Borrowed_parameter_inside_array_is_retained() => SemanticsAssert.DiagnosticAsync("using SafeSharp; class C { object[]? field; void M([Borrowed] object value) { field = new[] { [|value|] }; } }", "SS0027");

    [Fact]
    public Task Borrow_helper_preserves_escape_origin() => SemanticsAssert.DiagnosticAsync("using SafeSharp; class C { object? field; void M([Borrowed] object value) { field = Ownership.Borrow([|value|]); } }", "SS0027");

    [Fact]
    public Task Clone_result_is_not_assumed_to_borrow() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; class C { object? field; object Clone(object value) => new object(); void M([Borrowed] object value) { field = Clone(value); } }");

    [Fact]
    public Task Ordinary_parameter_can_be_stored() => SemanticsAssert.NoDiagnosticAsync("class C { object? field; void M(object value) { field = value; } }");

    [Fact]
    public Task Task_Run_reports_captured_thread_affine_value() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using System.Threading.Tasks; [ThreadAffine] class Widget { public void Touch() {} } class C { void M(Widget widget) { _ = Task.Run(() => [|widget|].Touch()); } }", "SS0025");

    [Fact]
    public Task Task_alias_preserves_semantic_identity() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using Work = System.Threading.Tasks.Task; [ThreadAffine] class Widget { public void Touch() {} } class C { void M(Widget widget) { _ = Work.Run(() => [|widget|].Touch()); } }", "SS0025");

    [Fact]
    public Task Method_group_also_captures_receiver() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using System.Threading.Tasks; [ThreadAffine] class Widget { public void Touch() {} } class C { void M(Widget widget) { _ = Task.Run([|widget|].Touch); } }", "SS0025");

    [Fact]
    public Task Inherited_thread_affinity_is_respected() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using System.Threading.Tasks; [ThreadAffine] class Base { public void Touch() {} } class Widget : Base {} class C { void M(Widget widget) { _ = Task.Run(() => [|widget|].Touch()); } }", "SS0025");

    [Fact]
    public Task Thread_affine_value_created_on_worker_is_not_captured() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; using System.Threading.Tasks; [ThreadAffine] class Widget { public void Touch() {} } class C { void M() { _ = Task.Run(() => { var widget = new Widget(); widget.Touch(); }); } }");

    [Fact]
    public Task Unrelated_Task_Run_is_not_assumed_to_schedule() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; [ThreadAffine] class Widget { public void Touch() {} } static class Task { public static void Run(System.Action action) => action(); } class C { void M(Widget widget) { Task.Run(() => widget.Touch()); } }");

    [Fact]
    public Task Returned_closure_may_outlive_using_resource() => SemanticsAssert.DiagnosticAsync("using System; using System.IO; class C { Func<int> M() { using var resource = new MemoryStream(); return () => [|resource|].ReadByte(); } }", "SS0028");

    [Fact]
    public Task Stored_closure_may_outlive_using_resource() => SemanticsAssert.DiagnosticAsync("using System; using System.IO; class C { Func<int>? reader; void M() { using var resource = new MemoryStream(); reader = () => [|resource|].ReadByte(); } }", "SS0028");

    [Fact]
    public Task Fire_and_forget_task_may_outlive_using_resource() => SemanticsAssert.DiagnosticAsync("using System.IO; using System.Threading.Tasks; class C { void M() { using var resource = new MemoryStream(); _ = Task.Run(() => [|resource|].ReadByte()); } }", "SS0028");

    [Fact]
    public Task Returning_task_does_not_delay_using_disposal() => SemanticsAssert.DiagnosticAsync("using System.IO; using System.Threading.Tasks; class C { Task<int> M() { using var resource = new MemoryStream(); return Task.Run(() => [|resource|].ReadByte()); } }", "SS0028");

    [Fact]
    public Task Awaited_task_completes_inside_using_lifetime() => SemanticsAssert.NoDiagnosticAsync("using System.IO; using System.Threading.Tasks; class C { async Task M() { using var resource = new MemoryStream(); await Task.Run(() => resource.ReadByte()).ConfigureAwait(false); } }");

    [Fact]
    public Task Immediately_joined_task_local_is_safe() => SemanticsAssert.NoDiagnosticAsync("using System.IO; using System.Threading.Tasks; class C { async Task M() { using var resource = new MemoryStream(); var task = Task.Run(() => resource.ReadByte()); await task; } }");

    [Fact]
    public Task Synchronous_task_join_is_safe() => SemanticsAssert.NoDiagnosticAsync("using System.IO; using System.Threading.Tasks; class C { void M() { using var resource = new MemoryStream(); Task.Run(() => resource.ReadByte()).GetAwaiter().GetResult(); } }");

    [Fact]
    public Task Timed_wait_does_not_establish_task_completion() => SemanticsAssert.DiagnosticAsync("using System.IO; using System.Threading.Tasks; class C { void M() { using var resource = new MemoryStream(); Task.Run(() => [|resource|].ReadByte()).Wait(0); } }", "SS0028");

    [Fact]
    public Task Cancelable_wait_does_not_establish_task_completion() => SemanticsAssert.DiagnosticAsync("using System.IO; using System.Threading; using System.Threading.Tasks; class C { void M(CancellationToken token) { using var resource = new MemoryStream(); Task.Run(() => [|resource|].ReadByte()).Wait(token); } }", "SS0028");

    [Fact]
    public Task Immediate_callback_does_not_escape() => SemanticsAssert.NoDiagnosticAsync("using System; using System.IO; class C { void M() { using var resource = new MemoryStream(); Func<int> reader = () => resource.ReadByte(); reader(); } }");

    [Fact]
    public Task Using_statement_expression_tracks_existing_resource() => SemanticsAssert.DiagnosticAsync("using System; using System.IO; class C { Func<int> M() { var resource = new MemoryStream(); using (resource) { return () => [|resource|].ReadByte(); } } }", "SS0028");

    [Fact]
    public Task Resource_created_inside_task_is_not_an_outer_capture() => SemanticsAssert.NoDiagnosticAsync("using System.IO; using System.Threading.Tasks; class C { Task<int> M() => Task.Run(() => { using var resource = new MemoryStream(); return resource.ReadByte(); }); }");
}
