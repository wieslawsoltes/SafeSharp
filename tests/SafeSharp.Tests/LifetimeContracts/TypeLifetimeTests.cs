using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Tests.LifetimeContracts;

public sealed class TypeLifetimeTests
{
    private const string Prelude = """
        using System;
        using System.Collections.Generic;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } }
        """;

    public static TheoryData<string, string> Accepted => new()
    {
        { "generic-covariance", """delegate void D([TypeLifetime("a", "arg0")] IEnumerable<Resource> value); class C { static void M([TypeLifetime("x", "arg0")] IEnumerable<Resource> value) {} D F() => M; }""" },
        { "generic-contravariance", """delegate void D([TypeLifetime("a", "arg0")] IComparer<Resource> value); class C { static void M([TypeLifetime("x", "arg0")] IComparer<Resource> value) {} D F() => M; }""" },
        { "generic-invariance", """delegate void D([TypeLifetime("a", "arg0")] List<Resource> value); class C { static void M([TypeLifetime("x", "arg0")] List<Resource> value) {} D F() => M; }""" },
        { "array-storage-invariance", """delegate void D([TypeLifetime("a", "element")] Resource[] value); class C { static void M([TypeLifetime("x", "element")] Resource[] value) {} D F() => M; }""" },
        { "nested-callable-alpha-renaming", """delegate void D([TypeLifetime("a", "param0"), TypeLifetime("a", "return")] Func<Resource, Resource> callback); class C { static void M([TypeLifetime("x", "param0"), TypeLifetime("x", "return")] Func<Resource, Resource> callback) {} D F() => M; }""" },
        { "contravariant-callable-weakens-callee-requirement", """delegate void D([TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("a", "return")] Func<Resource, Resource, Resource> callback); class C { static void M([TypeLifetime("x", "param0"), TypeLifetime("x", "param1"), TypeLifetime("x", "return")] Func<Resource, Resource, Resource> callback) {} D F() => M; }""" },
        { "outer-capture-remains-outer", """[return: TypeLifetime("^a", "return")] delegate Func<Resource> D([Lifetime("a")] Resource owner); class C { [return: TypeLifetime("^x", "return")] static Func<Resource> M([Lifetime("x")] Resource owner) => () => owner; D F() => M; }""" },
        { "inner-name-shadows-outer", """delegate void D([Lifetime("a")] Resource owner, [TypeLifetime("a", "param0"), TypeLifetime("a", "return")] Func<Resource, Resource> callback); class C { static void M([Lifetime("x")] Resource owner, [TypeLifetime("x", "param0"), TypeLifetime("x", "return")] Func<Resource, Resource> callback) {} D F() => M; }""" },
        { "two-nested-binders", """[return: TypeLifetime("a", "return.param0"), TypeLifetime("a", "return.return")] delegate Func<Func<Resource, Resource>> D(); class C { [return: TypeLifetime("x", "return.param0"), TypeLifetime("x", "return.return")] static Func<Func<Resource, Resource>> M() => () => value => value; D F() => M; }""" },
        { "invariant-callable-both-directions", """delegate void D([TypeLifetime("a", "arg0.param0"), TypeLifetime("a", "arg0.return")] List<Func<Resource, Resource>> callback); class C { static void M([TypeLifetime("x", "arg0.param0"), TypeLifetime("x", "arg0.return")] List<Func<Resource, Resource>> callback) {} D F() => M; }""" },
        { "implied-generic-input-bound", """delegate void D<T>([Lifetime("a")] ref T value); class C { [TypeOutlives("T", "x")] static void M<T>([Lifetime("x")] ref T value) {} D<Resource> F() => M<Resource>; }""" },
        { "explicit-generic-bound", """[TypeOutlives("T", "b")] delegate void D<T>([Lifetime("a")] T value, [Lifetime("b")] ref Resource other); class C { [TypeOutlives("T", "y")] static void M<T>([Lifetime("x")] T value, [Lifetime("y")] ref Resource other) {} D<Resource> F() => M<Resource>; }""" },
        { "borrowed-contents-imply-owner-bound", """delegate void D<T>([Lifetime("a"), TypeLifetime("b", "arg0")] ref IEnumerable<T> value); class C { [TypeOutlives("T", "x")] static void M<T>([Lifetime("x"), TypeLifetime("y", "arg0")] ref IEnumerable<T> value) {} D<Resource> F() => M<Resource>; }""" },
        { "inherited-type-paths", """delegate void D([TypeLifetime("a", "param0"), TypeLifetime("a", "return")] Func<Resource, Resource> callback); interface I { void M([TypeLifetime("x", "param0"), TypeLifetime("x", "return")] Func<Resource, Resource> callback); } class C : I { public void M(Func<Resource, Resource> callback) {} D F() => M; }""" },
        { "annotated-anonymous-function", """[return: TypeLifetime("^a", "return")] delegate Func<Resource> D([Lifetime("a")] Resource owner); class C { D F() => [return: TypeLifetime("^x", "return")] ([Lifetime("x")] Resource owner) => () => owner; }""" },
        { "generic-result-covariance", """[return: TypeLifetime("a", "arg0")] delegate IEnumerable<Resource> D([TypeLifetime("a", "")] Resource value); class C { [return: TypeLifetime("x", "arg0")] static IEnumerable<Resource> M([TypeLifetime("x", "")] Resource value) => new[] { value }; D F() => M; }""" },
        { "deep-generic-variance-composition", """delegate void D([TypeLifetime("a", "arg0.arg0")] IEnumerable<IComparer<Resource>> value); class C { static void M([TypeLifetime("x", "arg0.arg0")] IEnumerable<IComparer<Resource>> value) {} D F() => M; }""" },
        { "sibling-binders-have-independent-names", """delegate void D([TypeLifetime("a", "param0"), TypeLifetime("a", "return")] Func<Resource, Resource> first, [TypeLifetime("a", "param0"), TypeLifetime("a", "return")] Func<Resource, Resource> second); class C { static void M([TypeLifetime("x", "param0"), TypeLifetime("x", "return")] Func<Resource, Resource> first, [TypeLifetime("x", "param0"), TypeLifetime("x", "return")] Func<Resource, Resource> second) {} D F() => M; }""" },
        { "scoped-outlives-justifies-second-result", """[Outlives("b", "a", Scope = "result")] [return: TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("a", "return")] delegate Func<Resource, Resource, Resource> D(); class C { [return: TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("y", "return")] static Func<Resource, Resource, Resource> M() => (first, second) => second; D F() => M; }""" },
        { "inherited-generic-type-parameter-alpha-renaming", """interface I { [TypeOutlives("T", "a")] void M<T>([Lifetime("a")] ref T value); } class C : I { [TypeOutlives("U", "x")] public void M<U>([Lifetime("x")] ref U value) {} }""" },
        { "inherited-outer-type-position-bound-renaming", """interface I { [Outlives("a", "b")] void M([TypeLifetime("a", "arg0")] ref IEnumerable<Resource> first, [TypeLifetime("b", "arg0")] ref IEnumerable<Resource> second); } class C : I { public void M([TypeLifetime("x", "arg0")] ref IEnumerable<Resource> first, [TypeLifetime("y", "arg0")] ref IEnumerable<Resource> second) {} }""" },
        { "inherited-scoped-bound-renaming", """interface I { [Outlives("b", "a", Scope = "result")] [return: TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("a", "return")] Func<Resource, Resource, Resource> M(); } class C : I { [return: TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("x", "return")] public Func<Resource, Resource, Resource> M() => (first, second) => first; }""" },
        { "inherited-callable-input-can-weaken-requirement", """interface I { void M([TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("a", "return")] Func<Resource, Resource, Resource> callback); } class C : I { public void M([TypeLifetime("x", "param0"), TypeLifetime("x", "param1"), TypeLifetime("x", "return")] Func<Resource, Resource, Resource> callback) {} }""" },
        { "reduced-extension-normalizes-nested-parameter-path", """delegate void D([TypeLifetime("a", "param0"), TypeLifetime("a", "return")] Func<Resource, Resource> callback); static class Extensions { public static void M(this Resource owner, [TypeLifetime("x", "param0"), TypeLifetime("x", "return")] Func<Resource, Resource> callback) {} } class C { D F(Resource owner) => owner.M; }""" },
        { "inherited-concrete-bound-does-not-bind-unrelated-class-parameter", """interface I<T> { [TypeOutlives("T", "a")] void M([Lifetime("a")] ref Resource value); } class C<U> : I<int> { public void M(ref Resource value) {} }""" },
        { "inherited-constructed-bound-substitutes-containing-type", """[TypeOutlives("T", "a")] delegate void D<T>([Lifetime("a")] ref Resource value); interface I<T> { [TypeOutlives("T", "a")] void M([Lifetime("a")] ref Resource value); } class C<U> : I<List<U>> { public void M(ref Resource value) {} } class Factory { D<List<string>> F() => new C<string>().M; }""" },
        { "inherited-array-bound-substitutes-enclosing-generic-types", """[TypeOutlives("T", "a")] delegate void D<T>([Lifetime("a")] ref Resource value); interface I<T> { [TypeOutlives("T", "a")] void M([Lifetime("a")] ref Resource value); } class Outer<T> where T : notnull { public class Inner<U> : I<Dictionary<T, U[]>> { public void M(ref Resource value) {} } } class Factory { D<Dictionary<Resource, string[]>> F() => new Outer<Resource>.Inner<string>().M; }""" },
        { "inherited-bound-substitutes-nested-nominal-types", """[TypeOutlives("T", "a")] delegate void D<T>([Lifetime("a")] ref Resource value); interface I<T> { [TypeOutlives("T", "a")] void M([Lifetime("a")] ref Resource value); } class Box<T> { public class Item<U> { } } class C<T, U> : I<Box<T>.Item<U>> { public void M(ref Resource value) {} } class Factory { D<Box<Resource>.Item<string>> F() => new C<Resource, string>().M; }""" },
        { "explicit-type-bound-inside-nested-binder", """[TypeOutlives("T", "b", Scope = "result")] [return: TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("b", "return")] delegate Func<T, Resource, Resource> D<T>(); class C { [TypeOutlives("T", "y", Scope = "result")] [return: TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("y", "return")] static Func<T, Resource, Resource> M<T>() => (first, second) => second; D<Resource> F() => M<Resource>; }""" },
        { "inherited-scoped-type-bound-renames-method-parameter-and-region", """interface I { [TypeOutlives("T", "b", Scope = "result")] [return: TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("b", "return")] Func<T, Resource, Resource> M<T>(); } class C : I { [return: TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("y", "return")] public Func<U, Resource, Resource> M<U>() => (first, second) => second; }""" },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task AcceptedContractsRemainCompatible(string scenario, string source)
    {
        Assert.NotEmpty(scenario);
        Assert.Empty(await AnalyzeAsync(Prelude + source));
    }

    public static TheoryData<string, string> Rejected => new()
    {
        { "missing-type-contract", """delegate void D([TypeLifetime("a", "arg0")] IEnumerable<Resource> value); class C { static void M(IEnumerable<Resource> value) {} D F() => [|M|]; }""" },
        { "stronger-type-contract", """delegate void D([Lifetime("a")] Resource owner, IEnumerable<Resource> value); class C { static void M([Lifetime("x")] Resource owner, [TypeLifetime("y", "arg0")] IEnumerable<Resource> value) {} D F() => [|M|]; }""" },
        { "nested-callback-demands-first-origin", """delegate void D([TypeLifetime("a", "param0"), TypeLifetime("a", "param1"), TypeLifetime("a", "return")] Func<Resource, Resource, Resource> callback); class C { static void M([TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("x", "return")] Func<Resource, Resource, Resource> callback) {} D F() => [|M|]; }""" },
        { "captured-outer-lifetime-cannot-be-generalized", """[return: TypeLifetime("b", "return")] delegate Func<Resource> D([Lifetime("a")] Resource owner); class C { [return: TypeLifetime("^x", "return")] static Func<Resource> M([Lifetime("x")] Resource owner) => () => owner; D F() => [|M|]; }""" },
        { "shadowed-name-is-a-fresh-placeholder", """[return: TypeLifetime("a", "return")] delegate Func<Resource> D([Lifetime("a")] Resource owner); class C { [return: TypeLifetime("^a", "return")] static Func<Resource> M([Lifetime("a")] Resource owner) => () => owner; D F() => [|M|]; }""" },
        { "invariant-generic-inputs-cannot-collapse", """delegate void D([TypeLifetime("a", "arg0")] List<Resource> first, [TypeLifetime("b", "arg0")] List<Resource> second); class C { static void M([TypeLifetime("x", "arg0")] List<Resource> first, [TypeLifetime("x", "arg0")] List<Resource> second) {} D F() => [|M|]; }""" },
        { "covariant-result-cannot-promise-one-of-two-inputs", """[return: TypeLifetime("a", "arg0")] delegate IEnumerable<Resource> D([TypeLifetime("a", "arg0")] IEnumerable<Resource> first, [TypeLifetime("b", "arg0")] IEnumerable<Resource> second); class C { [return: TypeLifetime("x", "arg0")] static IEnumerable<Resource> M([TypeLifetime("x", "arg0")] IEnumerable<Resource> first, [TypeLifetime("x", "arg0")] IEnumerable<Resource> second) => first; D F() => [|M|]; }""" },
        { "contravariant-result-reverses-obligation", """[return: TypeLifetime("a", "arg0")] delegate IComparer<Resource> D([TypeLifetime("a", "arg0")] IComparer<Resource> first, [TypeLifetime("b", "arg0")] IComparer<Resource> second); class C { [return: TypeLifetime("x", "arg0")] static IComparer<Resource> M([TypeLifetime("x", "arg0")] IComparer<Resource> first, [TypeLifetime("x", "arg0")] IComparer<Resource> second) => first; D F() => [|M|]; }""" },
        { "array-element-storage-is-invariant", """delegate void D([TypeLifetime("a", "element")] Resource[] first, [TypeLifetime("b", "element")] Resource[] second); class C { static void M([TypeLifetime("x", "element")] Resource[] first, [TypeLifetime("x", "element")] Resource[] second) {} D F() => [|M|]; }""" },
        { "missing-type-outlives-bound", """delegate void D<T>([Lifetime("a")] T value, [Lifetime("b")] ref Resource other); class C { [TypeOutlives("T", "y")] static void M<T>([Lifetime("x")] T value, [Lifetime("y")] ref Resource other) {} D<Resource> F() => [|M<Resource>|]; }""" },
        { "captured-receiver-does-not-gain-inner-lifetime", """[return: TypeLifetime("a", "return")] delegate Func<Resource> D(); class C { Resource value = new(); [return: TypeLifetime("^this", "return")] Func<Resource> M() => () => value; D F() => [|M|]; }""" },
        { "anonymous-parameter-contract-is-not-guessed", """delegate void D([TypeLifetime("a", "param0"), TypeLifetime("a", "return")] Func<Resource, Resource> callback); class C { D F() => [|(Func<Resource, Resource> callback) => {}|]; }""" },
        { "invariant-callable-checks-the-reverse-direction", """delegate void D([TypeLifetime("a", "arg0.param0"), TypeLifetime("b", "arg0.param1"), TypeLifetime("a", "arg0.return")] List<Func<Resource, Resource, Resource>> callback); class C { static void M([TypeLifetime("x", "arg0.param0"), TypeLifetime("x", "arg0.param1"), TypeLifetime("x", "arg0.return")] List<Func<Resource, Resource, Resource>> callback) {} D F() => [|M|]; }""" },
        { "two-binders-cannot-generalize-a-root-capture", """[return: TypeLifetime("b", "return.return")] delegate Func<Func<Resource>> D([Lifetime("a")] Resource owner); class C { [return: TypeLifetime("^^x", "return.return")] static Func<Func<Resource>> M([Lifetime("x")] Resource owner) => () => () => owner; D F() => [|M|]; }""" },
        { "scoped-outlives-direction-matters", """[Outlives("a", "b", Scope = "result")] [return: TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("a", "return")] delegate Func<Resource, Resource, Resource> D(); class C { [return: TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("y", "return")] static Func<Resource, Resource, Resource> M() => (first, second) => second; D F() => [|M|]; }""" },
        { "bound-receiver-type-contract-needs-evidence", """delegate void D([Lifetime("a")] Resource value); static class Extensions { public static void M([TypeLifetime("borrow", "arg0")] this IEnumerable<Resource> owner, [Lifetime("x")] Resource value) {} } class C { D F(IEnumerable<Resource> owner) => [|owner.M|]; }""" },
        { "inherited-concrete-bound-is-not-an-unrelated-type-parameter", """[TypeOutlives("T", "a")] delegate void D<T>([Lifetime("a")] ref Resource value); interface I<T> { [TypeOutlives("T", "a")] void M([Lifetime("a")] ref Resource value); } class C<U> : I<int> { public void M(ref Resource value) {} D<U> F() => [|M|]; }""" },
        { "inherited-generic-container-bound-is-not-its-element", """[TypeOutlives("T", "a")] delegate void D<T>([Lifetime("a")] ref Resource value); interface I<T> { [TypeOutlives("T", "a")] void M([Lifetime("a")] ref Resource value); } class C<U> : I<List<U>> { public void M(ref Resource value) {} D<U> F() => [|M|]; }""" },
        { "nested-type-bound-cannot-be-invented", """[return: TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("b", "return")] delegate Func<T, Resource, Resource> D<T>(); class C { [TypeOutlives("T", "y", Scope = "result")] [return: TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("y", "return")] static Func<T, Resource, Resource> M<T>() => (first, second) => second; D<Resource> F() => [|M<Resource>|]; }""" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task InvalidCompatibilityReportsAtTheExactConversion(string scenario, string marked)
    {
        Assert.NotEmpty(scenario);
        var (source, span) = ExtractMarker(Prelude + marked);
        var diagnostics = await AnalyzeAsync(source);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("SS0047", diagnostic.Id);
        Assert.Equal(span, diagnostic.Location.SourceSpan);
    }

    public static TheoryData<string> InvalidDeclarations => new()
    {
        """delegate void D([TypeLifetime("a", "arg0")] Resource value);""",
        """delegate void D([TypeLifetime("a", "arg1")] IEnumerable<Resource> value);""",
        """delegate void D([TypeLifetime("a", "arg00")] IEnumerable<Resource> value);""",
        """delegate void D([TypeLifetime("a", "element")] IEnumerable<Resource> value);""",
        """delegate void D([TypeLifetime("a", "param0")] Resource value);""",
        """delegate void D([TypeLifetime("a", "param1")] Func<Resource, Resource> value);""",
        """delegate void D([TypeLifetime("a", "return")] Action<Resource> value);""",
        """delegate void D([TypeLifetime("a", "arg0.")] IEnumerable<Resource> value);""",
        """delegate void D([TypeLifetime("^a", "arg0")] IEnumerable<Resource> value);""",
        """delegate void D([TypeLifetime("^missing", "return")] Func<Resource> value);""",
        """delegate void D([TypeLifetime("a", "arg0"), TypeLifetime("b", "arg0")] IEnumerable<Resource> value);""",
        """[return: TypeLifetime("a", "")] delegate void D();""",
        """[TypeOutlives("Missing", "a")] delegate void D<T>([Lifetime("a")] T value);""",
        """[TypeOutlives("T", "missing")] delegate void D<T>([Lifetime("a")] T value);""",
        """[Outlives("a", "b", Scope = "missing")] delegate void D([TypeLifetime("a", "param0"), TypeLifetime("b", "return")] Func<Resource, Resource> value);""",
        """[Outlives("a", "missing", Scope = "parameter0")] delegate void D([TypeLifetime("a", "param0")] Func<Resource, Resource> value);""",
        """delegate void D([TypeLifetime(null, "arg0")] IEnumerable<Resource> value);""",
        """delegate void D([TypeLifetime("not-a-name", "arg0")] IEnumerable<Resource> value);""",
        """[TypeOutlives("T", "a", Scope = "missing")] delegate void D<T>([TypeLifetime("a", "param0")] Func<T, Resource> callback);""",
        """[TypeOutlives("T", "missing", Scope = "parameter0")] delegate void D<T>([TypeLifetime("a", "param0")] Func<T, Resource> callback);""",
        """[TypeOutlives("T", "a", Scope = "parameter1")] delegate void D<T>([Lifetime("a")] Resource owner, [TypeLifetime("b", "param0")] Func<T, Resource> callback);""",
    };

    [Theory]
    [MemberData(nameof(InvalidDeclarations))]
    public async Task MalformedAndUnboundMetadataIsRejected(string source)
    {
        var diagnostics = await AnalyzeAsync(Prelude + source);
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, diagnostic => Assert.Equal("SS0040", diagnostic.Id));
    }

    public static TheoryData<string> InvalidImplementations => new()
    {
        """interface I { [return: TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("a", "return")] Func<Resource, Resource, Resource> M(); } class C : I { [return: TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("y", "return")] public Func<Resource, Resource, Resource> [|M|]() => (first, second) => second; }""",
        """interface I { void M([TypeLifetime("a", "param0"), TypeLifetime("a", "param1"), TypeLifetime("a", "return")] Func<Resource, Resource, Resource> callback); } class C : I { public void [|M|]([TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("x", "return")] Func<Resource, Resource, Resource> callback) {} }""",
        """interface I { void M<T>([Lifetime("a")] T value, [Lifetime("b")] ref Resource other); } class C : I { [TypeOutlives("T", "y")] public void [|M|]<T>([Lifetime("x")] T value, [Lifetime("y")] ref Resource other) {} }""",
        """interface I { [return: TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("a", "return")] Func<Resource, Resource, Resource> M(); } class C : I { [Outlives("y", "x", Scope = "result")] [return: TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("x", "return")] public Func<Resource, Resource, Resource> [|M|]() => (first, second) => first; }""",
        """abstract class B { [return: TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("a", "return")] public abstract Func<Resource, Resource, Resource> M(); } class C : B { [return: TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("y", "return")] public override Func<Resource, Resource, Resource> [|M|]() => (first, second) => second; }""",
        """interface I { [return: TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("b", "return")] Func<T, Resource, Resource> M<T>(); } class C : I { [TypeOutlives("T", "y", Scope = "result")] [return: TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("y", "return")] public Func<T, Resource, Resource> [|M|]<T>() => (first, second) => second; }""",
    };

    [Theory]
    [MemberData(nameof(InvalidImplementations))]
    public async Task ImplementationsCannotStrengthenInheritedTypePromises(string marked)
    {
        var (source, span) = ExtractMarker(Prelude + marked);
        var diagnostic = Assert.Single(await AnalyzeAsync(source));
        Assert.Equal("SS0042", diagnostic.Id);
        Assert.Equal(span, diagnostic.Location.SourceSpan);
        Assert.NotEmpty(diagnostic.Properties["SafeSharp.RegionProof"]!);
    }

    [Fact]
    public async Task ExcessivelyLongPathsHaveABoundedDeclarationDiagnostic()
    {
        var path = string.Join(".", Enumerable.Repeat("arg0", 65));
        var diagnostic = Assert.Single(await AnalyzeAsync(Prelude + "delegate void D([TypeLifetime(\"a\", \"" + path + "\")] IEnumerable<Resource> value);"));
        Assert.Equal("SS0040", diagnostic.Id);
        Assert.Contains("64-segment", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepConstructedTypeSubstitutionReportsAnExplicitBudgetBoundary()
    {
        var type = string.Concat(Enumerable.Repeat("List<", 66)) + "Resource" + new string('>', 66);
        var source = Prelude + """
            interface I<T> { [TypeOutlives("T", "a")] void M([Lifetime("a")] ref Resource value); }
            """ + "class C : I<" + type + "> { public void M(ref Resource value) {} }";
        var diagnostic = Assert.Single(await AnalyzeAsync(source));
        Assert.Equal("SS0043", diagnostic.Id);
        Assert.Contains("substitution depth", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedLeakRetainsUniverseAndVisibilityEvidence()
    {
        var source = Rejected.First(item => (string)item[0] == "captured-outer-lifetime-cannot-be-generalized")[1].ToString()!;
        var (clean, _) = ExtractMarker(Prelude + source);
        var diagnostic = Assert.Single(await AnalyzeAsync(clean));
        Assert.Contains("nested", diagnostic.Properties["SafeSharp.RegionQuantification"], StringComparison.Ordinal);
        Assert.Contains("b1 <- root", diagnostic.Properties["SafeSharp.RegionUniverses"], StringComparison.Ordinal);
        Assert.Contains("cannot name", diagnostic.Properties["SafeSharp.RegionUniversePromotions"], StringComparison.Ordinal);
        Assert.Contains("->", diagnostic.Properties["SafeSharp.RegionVisibilityProofs"], StringComparison.Ordinal);
        Assert.Contains("static upper bound", diagnostic.Properties["SafeSharp.RegionRequirementReasons"], StringComparison.Ordinal);
        Assert.NotEmpty(diagnostic.AdditionalLocations);
    }

    [Fact]
    public async Task TypeFailureRetainsRequiredTypeAndBorrowedInputBounds()
    {
        var source = Rejected.First(item => (string)item[0] == "missing-type-outlives-bound")[1].ToString()!;
        var (clean, _) = ExtractMarker(Prelude + source);
        var diagnostic = Assert.Single(await AnalyzeAsync(clean));
        Assert.NotEmpty(diagnostic.Properties["SafeSharp.TypeOutlivesType"]!);
        Assert.Contains("Resource", diagnostic.Properties["SafeSharp.TypeBindings"], StringComparison.Ordinal);
        Assert.Contains("t0 :", diagnostic.Properties["SafeSharp.TypeOutlivesAssumptions"], StringComparison.Ordinal);
        Assert.Contains("t0 :", diagnostic.Properties["SafeSharp.TypeOutlivesRequirements"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedInvariantExpansionSharesTheUniverseBudget()
    {
        var source = Accepted.First(item => (string)item[0] == "invariant-callable-both-directions")[1].ToString()!;
        var diagnostics = await AnalyzeAsync(Prelude + source, new Dictionary<string, string> { ["safesharp_max_regions"] = "3" });
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0043");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0047");
    }

    [Fact]
    public async Task ConcurrentNestedChecksProduceTheSameEvidence()
    {
        var source = Rejected.First(item => (string)item[0] == "two-binders-cannot-generalize-a-root-capture")[1].ToString()!;
        var (clean, _) = ExtractMarker(Prelude + source);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AnalyzeAsync(clean)));
        static string Evidence(ImmutableArray<Diagnostic> diagnostics) => string.Join("\n", diagnostics.Select(diagnostic => diagnostic.ToString() +
            string.Join(";", diagnostic.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value))));
        Assert.All(results, result => Assert.Equal(Evidence(results[0]), Evidence(result)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedScopedContractsAreEnforcedAcrossAssemblyMetadata(bool hasRequiredBound)
    {
        var bound = hasRequiredBound ? "[Outlives(\"b\", \"a\", Scope = \"result\")]" : string.Empty;
        var library = AnalyzerTestHost.CreateCompilation("""
            using System;
            using SafeSharp;
            namespace ExternalLifetimeContracts;
            public sealed class Resource { }
            """ + bound + """
            [return: TypeLifetime("a", "param0"), TypeLifetime("b", "param1"), TypeLifetime("a", "return")]
            public delegate Func<Resource, Resource, Resource> D();
            public static class Api
            {
                [return: TypeLifetime("x", "param0"), TypeLifetime("y", "param1"), TypeLifetime("y", "return")]
                public static Func<Resource, Resource, Resource> Second() => (first, second) => second;
            }
            """).WithAssemblyName("ExternalLifetimeContracts");
        using var image = new MemoryStream();
        var emitted = library.Emit(image);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var (consumer, span) = ExtractMarker("""
            using ExternalLifetimeContracts;
            class C { D F() => [|Api.Second|]; }
            """);
        var compilation = AnalyzerTestHost.CreateCompilation(consumer).AddReferences(MetadataReference.CreateFromImage(image.ToArray()));
        var diagnostics = await AnalyzeAsync(compilation);
        if (hasRequiredBound)
            Assert.Empty(diagnostics);
        else
        {
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("SS0047", diagnostic.Id);
            Assert.Equal(span, diagnostic.Location.SourceSpan);
            Assert.NotEmpty(diagnostic.Properties["SafeSharp.RegionProof"]!);
            Assert.Contains("b1 <- root", diagnostic.Properties["SafeSharp.RegionUniverses"], StringComparison.Ordinal);
        }
    }

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, IReadOnlyDictionary<string, string>? settings = null)
        => AnalyzeAsync(AnalyzerTestHost.CreateCompilation(source), settings);

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(CSharpCompilation compilation, IReadOnlyDictionary<string, string>? settings = null)
    {
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(diagnostic => diagnostic.ToString())));
        var analyzerOptions = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, new ConfigProvider(settings));
        var options = new CompilationWithAnalyzersOptions(analyzerOptions, null, true, false, false);
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new LifetimeContractAnalyzer()), options).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
        return diagnostics.OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start).ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    private static (string Source, TextSpan Span) ExtractMarker(string marked)
    {
        var start = marked.IndexOf("[|", StringComparison.Ordinal);
        var end = marked.IndexOf("|]", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return (marked.Remove(end, 2).Remove(start, 2), new TextSpan(start, end - start - 2));
    }

    private sealed class ConfigProvider(IReadOnlyDictionary<string, string>? settings) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions values = new ConfigValues(settings ?? new Dictionary<string, string>());
        public override AnalyzerConfigOptions GlobalOptions => values;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => values;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => values;
    }
    private sealed class ConfigValues(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
