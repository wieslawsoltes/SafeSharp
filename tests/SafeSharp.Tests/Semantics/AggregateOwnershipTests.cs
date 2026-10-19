namespace SafeSharp.Tests.Semantics;

public sealed class AggregateOwnershipTests
{
    [Fact]
    public Task Owning_a_disposable_requires_disposal_contract() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using System.IO; class C { [Owned] MemoryStream [|resource|] = new(); }", "SS0020");

    [Fact]
    public Task Method_name_alone_is_not_disposal_contract() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using System.IO; class C { [Owned] MemoryStream [|resource|] = new(); public void Dispose() => resource.Dispose(); }", "SS0020");

    [Fact]
    public Task Empty_dispose_leaves_owned_member_unreleased() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using System; using System.IO; class C : IDisposable { [Owned] MemoryStream [|resource|] = new(); public void Dispose() {} }", "SS0021");

    [Fact]
    public Task Direct_release_is_recognized() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; using System; using System.IO; class C : IDisposable { [Owned] MemoryStream resource = new(); public void Dispose() => resource.Dispose(); }");

    [Fact]
    public Task Conditional_access_release_is_recognized() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; using System; using System.IO; class C : IDisposable { [Owned] MemoryStream? resource; public void Dispose() => resource?.Dispose(); }");

    [Fact]
    public Task Explicit_interface_release_is_recognized() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; using System; using System.IO; class C : IDisposable { [Owned] MemoryStream resource = new(); void IDisposable.Dispose() => ((IDisposable)resource).Dispose(); }");

    [Fact]
    public Task Release_in_reachable_helper_is_recognized() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; using System; using System.IO; class C : IDisposable { [Owned] MemoryStream resource = new(); public void Dispose() => Release(); void Release() => resource.Dispose(); }");

    [Fact]
    public Task Release_in_uninvoked_helper_is_not_proof() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using System; using System.IO; class C : IDisposable { [Owned] MemoryStream [|resource|] = new(); public void Dispose() {} void Release() => resource.Dispose(); }", "SS0021");

    [Fact]
    public Task Release_in_uninvoked_callback_is_not_proof() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using System; using System.IO; class C : IDisposable { [Owned] MemoryStream [|resource|] = new(); public void Dispose() { Action release = () => resource.Dispose(); } }", "SS0021");

    [Fact]
    public Task Invoked_local_function_releases_resource() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; using System; using System.IO; class C : IDisposable { [Owned] MemoryStream resource = new(); public void Dispose() { void Release() => resource.Dispose(); Release(); } }");

    [Fact]
    public Task Wrong_dispose_overload_is_not_proof() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using System; class R : IDisposable { public void Dispose() {} public void Dispose(int mode) {} } class C : IDisposable { [Owned] R [|resource|] = new(); public void Dispose() => resource.Dispose(1); }", "SS0021");

    [Fact]
    public Task Dispose_bool_override_is_followed() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; using System; using System.IO; class Base : IDisposable { public void Dispose() => Dispose(true); protected virtual void Dispose(bool disposing) {} } class C : Base { [Owned] MemoryStream resource = new(); protected override void Dispose(bool disposing) { if (disposing) resource.Dispose(); base.Dispose(disposing); } }");

    [Fact]
    public Task Nested_type_does_not_inherit_outer_disposal_contract() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using System; using System.IO; class Outer : IDisposable { public void Dispose() {} class Inner { [Owned] MemoryStream [|resource|] = new(); } }", "SS0020");

    [Fact]
    public Task Owned_property_requires_release() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using System; using System.IO; class C : IDisposable { [Owned] MemoryStream [|Resource|] { get; } = new(); public void Dispose() {} }", "SS0021");

    [Fact]
    public Task Owned_property_can_be_released() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; using System; using System.IO; class C : IDisposable { [Owned] MemoryStream Resource { get; } = new(); public void Dispose() => Resource.Dispose(); }");

    [Fact]
    public Task Awaited_async_release_is_recognized() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; using System; using System.Threading.Tasks; class R : IAsyncDisposable { public ValueTask DisposeAsync() => default; } class C : IAsyncDisposable { [Owned] R resource = new(); public async ValueTask DisposeAsync() => await resource.DisposeAsync().ConfigureAwait(false); }");

    [Fact]
    public Task Returned_async_release_is_recognized() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; using System; using System.Threading.Tasks; class R : IAsyncDisposable { public ValueTask DisposeAsync() => default; } class C : IAsyncDisposable { [Owned] R resource = new(); public ValueTask DisposeAsync() => resource.DisposeAsync(); }");

    [Fact]
    public Task Ignored_async_release_is_not_completion() => SemanticsAssert.DiagnosticAsync("using SafeSharp; using System; using System.Threading.Tasks; class R : IAsyncDisposable { public ValueTask DisposeAsync() => default; } class C : IAsyncDisposable { [Owned] R [|resource|] = new(); public ValueTask DisposeAsync() { resource.DisposeAsync(); return default; } }", "SS0021");

    [Fact]
    public Task Similar_attribute_from_another_namespace_is_ignored() => SemanticsAssert.NoDiagnosticAsync("using Other; class C { [Owned] System.IO.MemoryStream resource = new(); } namespace Other { class OwnedAttribute : System.Attribute {} }");
}
