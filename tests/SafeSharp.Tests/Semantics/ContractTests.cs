using SafeSharp;

namespace SafeSharp.Tests.Semantics;

public sealed class ContractTests
{
    [Fact]
    public void Helpers_preserve_runtime_identity()
    {
        var value = new object();
        Assert.Same(value, Ownership.Move(value));
        Assert.Same(value, Ownership.Borrow(value));
        Assert.Same(value, Ownership.BorrowMut(value));
        Assert.Null(Ownership.Move<object?>(null));
    }

    [Fact]
    public void Contracts_retain_declared_metadata()
    {
        var borrowed = new ReturnsBorrowedAttribute("owner", mutable: true);
        Assert.Equal("owner", borrowed.ParameterName);
        Assert.True(borrowed.Mutable);
        Assert.False(new ReturnsBorrowedAttribute("this").Mutable);
        Assert.Throws<ArgumentNullException>(() => new ReturnsBorrowedAttribute(null!));
        Assert.Equal(ServiceLifetime.Scoped, new ServiceLifetimeAttribute(ServiceLifetime.Scoped).Lifetime);
    }

    public static TheoryData<string, string> InvalidContracts => new()
    {
        { "using SafeSharp; class C { void M([Borrowed, Consumes] object [|value|]) {} }", "SS0023" },
        { "using SafeSharp; class C { void M([Borrowed, BorrowedMut] object [|value|]) {} }", "SS0023" },
        { "using SafeSharp; class C { [[|ReturnsBorrowed(\"missing\")|]] object M(object value) => value; }", "SS0023" },
        { "using SafeSharp; class C { [[|ReturnsBorrowed(\"this\")|]] static object M(object value) => value; }", "SS0023" },
        { "using SafeSharp; class C { [ReturnsOwned] void [|M|]() {} }", "SS0023" },
        { "using SafeSharp; class C { [Mutates] static void [|M|]() {} }", "SS0023" },
        { "using SafeSharp; class C { [MustUse] void [|M|]() {} void Use() { M(); } }", "SS0023" },
        { "using SafeSharp; [[|ServiceLifetime((ServiceLifetime)17)|]] class C {}", "SS0023" },
        { "using SafeSharp; [ThreadAffine, ThreadSafe] class [|C|] {}", "SS0023" },
        { "using SafeSharp; class C { [[|ReturnsBorrowed(\"value\", true)|]] object M([Borrowed] object value) => value; }", "SS0023" },
        { "using SafeSharp; class C { [[|ReturnsBorrowed(\"value\", true)|]] [return: Borrowed] object M(object value) => value; }", "SS0023" },
    };

    [Theory]
    [MemberData(nameof(InvalidContracts))]
    public Task Invalid_contracts_report_precisely(string source, string id) => SemanticsAssert.DiagnosticAsync(source, id);

    [Fact]
    public Task Owned_and_consumes_are_compatible() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; class C { void M([Owned, Consumes] object value) {} }");

    [Fact]
    public Task Instance_receiver_is_valid_return_lifetime() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; class C { [ReturnsBorrowed(\"this\")] object M() => this; }");

    [Fact]
    public Task Return_and_parameter_contracts_are_distinct() => SemanticsAssert.NoDiagnosticAsync("using SafeSharp; class C { [ReturnsOwned] [return: Owned] object M([Borrowed] object value) => new object(); }");

    [Fact]
    public Task Shadow_attributes_are_not_trusted() => SemanticsAssert.NoDiagnosticAsync("using Other; class C { void M([Borrowed, Consumes] object value) {} } namespace Other { class BorrowedAttribute : System.Attribute {} class ConsumesAttribute : System.Attribute {} }");

    [Fact]
    public Task Qualified_attribute_alias_is_recognized() => SemanticsAssert.DiagnosticAsync("using B = SafeSharp.BorrowedAttribute; using Cn = SafeSharp.ConsumesAttribute; class C { void M([B, Cn] object [|value|]) {} }", "SS0023");

    [Fact]
    public Task Owner_struct_remains_copyable() => SemanticsAssert.DiagnosticAsync("using SafeSharp; [Owned] readonly struct [|Owner|] {}", "SS0022");

    [Fact]
    public Task Ref_struct_is_not_a_move_only_type() => SemanticsAssert.DiagnosticAsync("using SafeSharp; [Owned] ref struct [|Owner|] {}", "SS0022");

    [Fact]
    public Task Unannotated_value_type_is_not_an_owner() => SemanticsAssert.NoDiagnosticAsync("readonly struct Value { public int Id { get; } }");
}
