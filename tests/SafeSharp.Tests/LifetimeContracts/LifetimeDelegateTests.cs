using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Tests.LifetimeContracts;

public sealed class LifetimeDelegateTests
{
    private const string Prelude = """
        using System;
        using SafeSharp;
        sealed class Resource : IDisposable
        {
            public void Dispose() { }
            public void Read() { }
        }
        """;

    public static TheoryData<string, string> Accepted => new()
    {
        { "alpha-renamed-method", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { [return: Lifetime("x"), Borrowed] static Resource Id([Borrowed, Lifetime("x")] Resource renamed) => renamed; Read M() => Id; }""" },
        { "inferred-source-method", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { static Resource Id([Borrowed] Resource renamed) => renamed; Read M() => Id; }""" },
        { "explicit-source-method", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { [ReturnsBorrowed("renamed")] static Resource Id([Borrowed] Resource renamed) => renamed; Read M() => Id; }""" },
        { "independent-parameters-first", """[return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { [return: Lifetime("x"), Borrowed] static Resource Id([Borrowed, Lifetime("x")] Resource first, [Borrowed, Lifetime("y")] Resource second) => first; Pick M() => Id; }""" },
        { "void-inputs-can-shorten", """delegate void Observe([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { static void Both([Borrowed, Lifetime("x")] Resource first, [Borrowed, Lifetime("x")] Resource second) {} Observe M() => Both; }""" },
        { "readonly-inputs-can-shorten", """delegate void Observe([Borrowed, Lifetime("a")] in Resource first, [Borrowed, Lifetime("b")] in Resource second); class Example { static void Both([Borrowed, Lifetime("x")] in Resource first, [Borrowed, Lifetime("x")] in Resource second) {} Observe M() => Both; }""" },
        { "promised-outlives-allows-second", """[Outlives("b", "a")] [return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { [return: Lifetime("y"), Borrowed] static Resource Second([Borrowed, Lifetime("x")] Resource first, [Borrowed, Lifetime("y")] Resource second) => second; Pick M() => Second; }""" },
        { "promised-outlives-allows-callee-requirement", """[Outlives("b", "a")] [return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { [Outlives("y", "x")] [return: Lifetime("x"), Borrowed] static Resource First([Borrowed, Lifetime("x")] Resource first, [Borrowed, Lifetime("y")] Resource second) => first; Pick M() => First; }""" },
        { "identical-ref-region", """delegate void Observe([Borrowed, Lifetime("a")] ref Resource first, [Borrowed, Lifetime("a")] ref Resource second); class Example { static void Both([Borrowed, Lifetime("x")] ref Resource first, [Borrowed, Lifetime("x")] ref Resource second) {} Observe M() => Both; }""" },
        { "bound-instance-keeps-argument", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Reader { [return: Lifetime("x"), Borrowed] public Resource Id([Borrowed, Lifetime("x")] Resource input) => input; } class Example { Read M(Reader reader) => reader.Id; }""" },
        { "reduced-extension-keeps-argument", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); static class Extensions { [return: Lifetime("x"), Borrowed] public static Resource Keep(this Resource receiver, [Borrowed, Lifetime("x")] Resource input) => input; } class Example { Read M(Resource receiver) => receiver.Keep; }""" },
        { "unbound-extension-first-input", """[return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); static class Extensions { [return: Lifetime("x"), Borrowed] public static Resource Keep([Borrowed, Lifetime("x")] this Resource input, [Borrowed, Lifetime("y")] Resource other) => input; } class Example { Pick M() => Extensions.Keep; }""" },
        { "inherited-interface-renaming", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); interface IReader { [return: Lifetime("x"), Borrowed] Resource Id([Borrowed, Lifetime("x")] Resource value); } sealed class Reader : IReader { public Resource Id(Resource renamed) => renamed; } class Example { Read M(Reader reader) => reader.Id; }""" },
        { "inherited-override-renaming", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); abstract class Base { [return: Lifetime("x"), Borrowed] public abstract Resource Id([Borrowed, Lifetime("x")] Resource value); } sealed class Reader : Base { public override Resource Id(Resource renamed) => renamed; } class Example { Read M(Reader reader) => reader.Id; }""" },
        { "shared-callback-can-use-exclusive-input", """[return: Lifetime("a"), Borrowed] delegate Resource Read([BorrowedMut, Lifetime("a")] Resource input); class Example { [return: Lifetime("x"), Borrowed] static Resource Id([Borrowed, Lifetime("x")] Resource value) => value; Read M() => Id; }""" },
        { "owned-factory-explicit", """[return: Owned] delegate Resource Factory(); class Example { [ReturnsOwned] static Resource Create() => new(); Factory M() => Create; }""" },
        { "owned-factory-inferred", """[return: Owned] delegate Resource Factory(); class Example { static Resource Create() => new(); Factory M() => Create; }""" },
        { "owned-factory-inferred-explicit-creation", """[return: Owned] delegate Resource Factory(); class Example { static Resource Create() => new Resource(); Factory M() => Create; }""" },
        { "owned-factory-explicit-lambda", """[return: Owned] delegate Resource Factory(); class Example { Factory M() => [return: Owned] () => new Resource(); }""" },
        { "consuming-parameter-preserved", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input, [Consumes] Resource owned); class Example { [return: Lifetime("x"), Borrowed] static Resource Id([Borrowed, Lifetime("x")] Resource value, [Consumes] Resource owned) { owned.Dispose(); return value; } Read M() => Id; }""" },
        { "noescape-preserved", """delegate void Observe([Borrowed, DoesNotEscape, Lifetime("a")] Resource input); class Example { static void Read([Borrowed, DoesNotEscape, Lifetime("x")] Resource value) { value.Read(); } Observe M() => Read; }""" },
        { "anonymous-expression", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M() => ([Borrowed] Resource input) => input; }""" },
        { "anonymous-exclusive-input-result", """[return: Lifetime("a"), BorrowedMut] delegate Resource Read([BorrowedMut, Lifetime("a")] Resource input); class Example { Read M() => ([BorrowedMut] Resource input) => input; }""" },
        { "anonymous-unused-shared-input", """[return: Lifetime("a"), BorrowedMut] delegate Resource Read([BorrowedMut, Lifetime("a")] Resource input, [Borrowed, Lifetime("b")] Resource other); class Example { Read M() => ([BorrowedMut] Resource input, [Borrowed] Resource other) => input; }""" },
        { "anonymous-block-alias", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M() => ([Borrowed] Resource input) => { var copy = input; return copy; }; }""" },
        { "anonymous-ref-alias", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M() => ([Borrowed] Resource input) => { ref Resource copy = ref input; return copy; }; }""" },
        { "anonymous-both-branches-first", """[return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { Pick M(bool choose) => ([Borrowed] Resource first, [Borrowed] Resource second) => { if (choose) return first; return first; }; }""" },
        { "anonymous-outlives-second", """[Outlives("b", "a")] [return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { Pick M() => ([Borrowed] Resource first, [Borrowed] Resource second) => second; }""" },
        { "anonymous-static-field-initializer", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { static Read Reader = ([Borrowed] Resource input) => input; }""" },
        { "anonymous-instance-property-initializer", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Read Reader { get; } = ([Borrowed] Resource input) => input; }""" },
        { "anonymous-in-local-function", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M() { Read Local() => ([Borrowed] Resource input) => input; return Local(); } }""" },
        { "anonymous-in-outer-lambda", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Func<Read> M() => () => ([Borrowed] Resource input) => input; }""" },
        { "anonymous-ordinary-nested-does-not-return-from-outer", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M() => ([Borrowed] Resource input) => { Func<Resource> unused = () => new Resource(); return input; }; }""" },
        { "typed-delegate-clone", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M(Read source) => new Read(source); }""" },
        { "generic-delegate-and-generic-method", """[return: Lifetime("a"), Borrowed] delegate T Read<T>([Borrowed, Lifetime("a")] T input) where T : class; class Example { [return: Lifetime("x"), Borrowed] static T Id<T>([Borrowed, Lifetime("x")] T input) where T : class => input; Read<Resource> M() => Id<Resource>; }""" },
        { "ordinary-func-skipped", """class Example { Func<Resource, Resource> M(Resource captured) => value => captured; }""" },
        { "void-delegate-no-return-promise", """delegate void Observe([Borrowed, Lifetime("a")] Resource input); class Example { Observe M() => ([Borrowed] Resource input) => input.Read(); }""" },
        { "async-anonymous-payload", """[return: Lifetime("a"), Borrowed] delegate System.Threading.Tasks.Task<Resource> Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M() => async ([Borrowed] Resource input) => { await System.Threading.Tasks.Task.Yield(); return input; }; }""" },
        { "wrapped-anonymous-payload", """[return: Lifetime("a"), Borrowed] delegate System.Threading.Tasks.Task<Resource> Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M() => ([Borrowed] Resource input) => System.Threading.Tasks.Task.FromResult(input); }""" },
    };

    public static TheoryData<string, string, string> Violations => new()
    {
        { "wrong-return-input", """[return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { [return: Lifetime("y"), Borrowed] static Resource Second([Borrowed, Lifetime("x")] Resource first, [Borrowed, Lifetime("y")] Resource second) => second; Pick M() => [|Second|]; }""", "SS0047" },
        { "same-callee-input-region-cannot-invent-output", """[return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { [return: Lifetime("x"), Borrowed] static Resource Choose([Borrowed, Lifetime("x")] Resource first, [Borrowed, Lifetime("x")] Resource second) => first; Pick M() => [|Choose|]; }""", "SS0047" },
        { "stronger-outlives-requirement", """[return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { [Outlives("y", "x")] [return: Lifetime("x"), Borrowed] static Resource First([Borrowed, Lifetime("x")] Resource first, [Borrowed, Lifetime("y")] Resource second) => first; Pick M() => [|First|]; }""", "SS0047" },
        { "reverse-outlives-does-not-help", """[Outlives("a", "b")] [return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { [return: Lifetime("y"), Borrowed] static Resource Second([Borrowed, Lifetime("x")] Resource first, [Borrowed, Lifetime("y")] Resource second) => second; Pick M() => [|Second|]; }""", "SS0047" },
        { "ref-inputs-cannot-shorten-independently", """delegate void Observe([Borrowed, Lifetime("a")] ref Resource first, [Borrowed, Lifetime("b")] ref Resource second); class Example { static void Both([Borrowed, Lifetime("x")] ref Resource first, [Borrowed, Lifetime("x")] ref Resource second) {} Observe M() => [|Both|]; }""", "SS0047" },
        { "bound-receiver-cannot-promise-input", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Reader { Resource value = new(); [return: Lifetime("this"), Borrowed] public Resource View([Borrowed, Lifetime("x")] Resource input) => value; } class Example { Read M(Reader reader) => [|reader.View|]; }""", "SS0047" },
        { "bound-extension-cannot-promise-input", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); static class Extensions { [return: Lifetime("owner"), Borrowed] public static Resource Keep([Borrowed, Lifetime("owner")] this Resource receiver, [Borrowed, Lifetime("x")] Resource input) => receiver; } class Example { Read M(Resource receiver) => [|receiver.Keep|]; }""", "SS0047" },
        { "unannotated-method-must-preserve-borrowed", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { static Resource Id(Resource input) => input; Read M() => [|Id|]; }""", "SS0047" },
        { "exclusive-input-cannot-replace-shared", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { [return: Lifetime("x"), Borrowed] static Resource Id([BorrowedMut, Lifetime("x")] Resource input) => input; Read M() => [|Id|]; }""", "SS0047" },
        { "mutable-return-cannot-replace-shared", """[return: Lifetime("a"), Borrowed] delegate Resource Read([BorrowedMut, Lifetime("a")] Resource input); class Example { [return: Lifetime("x"), BorrowedMut] static Resource Id([BorrowedMut, Lifetime("x")] Resource input) => input; Read M() => [|Id|]; }""", "SS0047" },
        { "shared-return-cannot-replace-mutable", """[return: Lifetime("a"), BorrowedMut] delegate Resource Read([BorrowedMut, Lifetime("a")] Resource input); class Example { [return: Lifetime("x"), Borrowed] static Resource Id([Borrowed, Lifetime("x")] Resource input) => input; Read M() => [|Id|]; }""", "SS0047" },
        { "owned-result-cannot-replace-borrow", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { [ReturnsOwned] static Resource Create([Borrowed] Resource input) => new(); Read M() => [|Create|]; }""", "SS0047" },
        { "borrow-cannot-replace-owned", """[return: Owned] delegate Resource Factory(); class Reader { Resource value = new(); [ReturnsBorrowed("this")] public Resource View() => value; } class Example { Factory M(Reader reader) => [|reader.View|]; }""", "SS0047" },
        { "added-consumes-contract", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input, Resource other); class Example { [return: Lifetime("x"), Borrowed] static Resource Id([Borrowed, Lifetime("x")] Resource input, [Consumes] Resource other) => input; Read M() => [|Id|]; }""", "SS0047" },
        { "removed-consumes-contract", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input, [Consumes] Resource other); class Example { [return: Lifetime("x"), Borrowed] static Resource Id([Borrowed, Lifetime("x")] Resource input, Resource other) => input; Read M() => [|Id|]; }""", "SS0047" },
        { "removed-owned-parameter-contract", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input, [Owned] Resource other); class Example { [return: Lifetime("x"), Borrowed] static Resource Id([Borrowed, Lifetime("x")] Resource input, Resource other) => input; Read M() => [|Id|]; }""", "SS0047" },
        { "noescape-contract-removed", """delegate void Observe([Borrowed, DoesNotEscape, Lifetime("a")] Resource input); class Example { static void Read([Borrowed, Lifetime("x")] Resource value) {} Observe M() => [|Read|]; }""", "SS0047" },
        { "anonymous-borrow-contract-not-inferred", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M() => [|input => input|]; }""", "SS0047" },
        { "anonymous-owned-result-needs-contract", """[return: Owned] delegate Resource Factory(); class Example { Factory M() => [|() => new Resource()|]; }""", "SS0047" },
        { "anonymous-fresh-return", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M() => ([Borrowed] Resource input) => [|new Resource()|]; }""", "SS0047" },
        { "anonymous-captured-return", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M(Resource captured) => ([Borrowed] Resource input) => [|captured|]; }""", "SS0047" },
        { "anonymous-local-fresh-return", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M() => ([Borrowed] Resource input) => { var local = new Resource(); return [|local|]; }; }""", "SS0047" },
        { "anonymous-wrong-input", """[return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { Pick M() => ([Borrowed] Resource first, [Borrowed] Resource second) => [|second|]; }""", "SS0047" },
        { "anonymous-branch-merge", """[return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { Pick M(bool choose) => ([Borrowed] Resource first, [Borrowed] Resource second) => { var result = first; if (choose) result = second; return [|result|]; }; }""", "SS0047" },
        { "anonymous-conditional-return", """[return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { Pick M(bool choose) => ([Borrowed] Resource first, [Borrowed] Resource second) => [|choose ? first : second|]; }""", "SS0047" },
        { "anonymous-wrong-one-return", """[return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { Pick M(bool choose) => ([Borrowed] Resource first, [Borrowed] Resource second) => { if (choose) return first; return [|second|]; }; }""", "SS0047" },
        { "anonymous-static-field-initializer", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { static Read Reader = ([Borrowed] Resource input) => [|new Resource()|]; }""", "SS0047" },
        { "anonymous-property-initializer", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Read Reader { get; } = ([Borrowed] Resource input) => [|new Resource()|]; }""", "SS0047" },
        { "anonymous-in-local-function", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M() { Read Local() => ([Borrowed] Resource input) => [|new Resource()|]; return Local(); } }""", "SS0047" },
        { "anonymous-in-nested-lambda", """[return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource input); class Example { Func<Read> M() => () => ([Borrowed] Resource input) => [|new Resource()|]; }""", "SS0047" },
        { "anonymous-ref-alias-rebound", """[return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { Pick M() => ([Borrowed] Resource first, [Borrowed] Resource second) => { ref Resource alias = ref first; alias = ref second; return [|alias|]; }; }""", "SS0047" },
        { "typed-delegate-clone-cannot-change-result", """[return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); [return: Lifetime("b"), Borrowed] delegate Resource Other([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second); class Example { Pick M(Other other) => new Pick([|other|]); }""", "SS0047" },
        { "generic-method-wrong-input", """[return: Lifetime("a"), Borrowed] delegate T Pick<T>([Borrowed, Lifetime("a")] T first, [Borrowed, Lifetime("b")] T second) where T : class; class Example { [return: Lifetime("y"), Borrowed] static T Second<T>([Borrowed, Lifetime("x")] T first, [Borrowed, Lifetime("y")] T second) where T : class => second; Pick<Resource> M() => [|Second<Resource>|]; }""", "SS0047" },
        { "async-lambda-wrong-payload", """[return: Lifetime("a"), Borrowed] delegate System.Threading.Tasks.Task<Resource> Read([Borrowed, Lifetime("a")] Resource input); class Example { Read M() => async ([Borrowed] Resource input) => { await System.Threading.Tasks.Task.Yield(); return [|new Resource()|]; }; }""", "SS0047" },
        { "task-carrier-is-not-payload-region", """[return: Lifetime("a"), Borrowed] delegate System.Threading.Tasks.Task<Resource> Read([Borrowed, Lifetime("a")] System.Threading.Tasks.Task<Resource> input); class Example { Read M() => async ([Borrowed] System.Threading.Tasks.Task<Resource> input) => [|await input|]; }""", "SS0047" },
        { "invalid-empty-name", """delegate void Observe([[|Lifetime("")|]] Resource input);""", "SS0040" },
        { "invalid-region-identifier", """delegate void Observe([[|Lifetime("bad name")|]] Resource input);""", "SS0040" },
        { "reserved-receiver-on-delegate", """delegate void Observe([[|Lifetime("this")|]] Resource input);""", "SS0040" },
        { "void-return-region", """[return: [|Lifetime("a")|]] delegate void Observe([Lifetime("a")] Resource input);""", "SS0040" },
        { "unknown-outlives-region", """[[|Outlives("a", "missing")|]] delegate void Observe([Lifetime("a")] Resource input);""", "SS0040" },
        { "receiver-outlives-on-delegate", """[[|Outlives("this", "a")|]] delegate void Observe([Lifetime("a")] Resource input);""", "SS0040" },
        { "owned-result-and-region", """[return: Owned, [|Lifetime("a")|]] delegate Resource Factory([Lifetime("a")] Resource input);""", "SS0040" },
        { "consuming-input-and-region", """delegate void Observe([Consumes, [|Lifetime("a")|]] Resource input);""", "SS0040" },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task AcceptsCompatibleUniversallyQuantifiedCallbacks(string name, string body)
    {
        var diagnostics = await AnalyzeAsync(Prelude + body);
        Assert.True(diagnostics.IsEmpty, name + ": " + string.Join("\n", diagnostics));
    }

    [Theory]
    [MemberData(nameof(Violations))]
    public async Task ReportsIncompatibleCallbackAtTheExactSpan(string name, string body, string id)
    {
        var (source, span) = ExtractMarker(Prelude + body);
        var diagnostics = await AnalyzeAsync(source);
        var matching = diagnostics.Where(diagnostic => diagnostic.Id == id).ToArray();
        Assert.True(matching.Length == 1, name + ": expected one " + id + ", actual: " + string.Join("\n", diagnostics));
        var diagnostic = matching[0];
        Assert.Equal(span, diagnostic.Location.SourceSpan);
        Assert.EndsWith("#" + id.ToLowerInvariant(), diagnostic.Descriptor.HelpLinkUri, StringComparison.Ordinal);
        if (id == "SS0047")
        {
            Assert.NotEmpty(diagnostic.AdditionalLocations);
            Assert.True(diagnostic.Properties.ContainsKey("SafeSharp.DelegateType"));
        }
    }

    [Fact]
    public async Task UniversalFailureIncludesBindingsRequirementsAndShortestEvidence()
    {
        const string body = """
            [return: Lifetime("a"), Borrowed]
            delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second);
            class Example
            {
                [return: Lifetime("y"), Borrowed]
                static Resource Second([Borrowed, Lifetime("x")] Resource first, [Borrowed, Lifetime("y")] Resource second) => second;
                Pick M() => Second;
            }
            """;
        var diagnostics = await AnalyzeAsync(Prelude + body);
        var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0047");
        Assert.Equal("one universal delegate binder", diagnostic.Properties["SafeSharp.RegionQuantification"]);
        Assert.Contains("delegate region 'a'", diagnostic.Properties["SafeSharp.RegionBindings"], StringComparison.Ordinal);
        Assert.Contains("callback region 'y'", diagnostic.Properties["SafeSharp.RegionBindings"], StringComparison.Ordinal);
        Assert.Contains(" : ", diagnostic.Properties["SafeSharp.RegionRequirements"], StringComparison.Ordinal);
        Assert.Contains("Input may be borrowed", diagnostic.Properties["SafeSharp.RegionRequirementReasons"], StringComparison.Ordinal);
        Assert.Contains("->", diagnostic.Properties["SafeSharp.RegionProof"], StringComparison.Ordinal);
        Assert.NotEmpty(diagnostic.Properties["SafeSharp.RequiredRegion"]!);
        Assert.NotEmpty(diagnostic.Properties["SafeSharp.SourceRegions"]!);
        Assert.True(diagnostic.AdditionalLocations.Count >= 4);
    }

    [Fact]
    public async Task BoundExtensionFailureRetainsIndependentReceiverProof()
    {
        var diagnostics = await AnalyzeAsync(Prelude + """
            [return: Lifetime("a"), Borrowed]
            delegate Resource Read([Borrowed, Lifetime("a")] Resource input);
            static class Extensions {
                [return: Lifetime("owner"), Borrowed]
                public static Resource Keep([Borrowed, Lifetime("owner")] this Resource receiver,
                    [Borrowed, Lifetime("x")] Resource input) => receiver;
            }
            class Example { Read M(Resource receiver) => receiver.Keep; }
            """);
        var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0047");
        Assert.Equal("one universal delegate binder", diagnostic.Properties["SafeSharp.RegionQuantification"]);
        Assert.Contains("captured argument 'receiver'", diagnostic.Properties["SafeSharp.RegionBindings"], StringComparison.Ordinal);
        Assert.Contains("->", diagnostic.Properties["SafeSharp.RegionProof"], StringComparison.Ordinal);
        Assert.DoesNotContain("layout", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    public static TheoryData<string, string> PayloadProducers => new()
    {
        { "System.Threading.Tasks.Task<Resource>", "static System.Threading.Tasks.Task<Resource> Wrap([Borrowed] Resource value) => System.Threading.Tasks.Task.FromResult(value);" },
        { "System.Threading.Tasks.ValueTask<Resource>", "static System.Threading.Tasks.ValueTask<Resource> Wrap([Borrowed] Resource value) => System.Threading.Tasks.ValueTask.FromResult(value);" },
        { "System.Threading.Tasks.ValueTask<Resource>", "static System.Threading.Tasks.ValueTask<Resource> Wrap([Borrowed] Resource value) => new System.Threading.Tasks.ValueTask<Resource>(value);" },
        { "System.Threading.Tasks.Task<Resource>", "static async System.Threading.Tasks.Task<Resource> Wrap([Borrowed] Resource value) { await System.Threading.Tasks.Task.Yield(); return value; }" },
        { "System.Threading.Tasks.ValueTask<Resource>", "static async System.Threading.Tasks.ValueTask<Resource> Wrap([Borrowed] Resource value) { await System.Threading.Tasks.Task.Yield(); return value; }" },
    };

    [Theory]
    [MemberData(nameof(PayloadProducers))]
    public async Task InferredPayloadProvenanceSurvivesMethodGroupsAndLambdaCalls(string resultType, string producer)
    {
        var body = "[return: Lifetime(\"a\"), Borrowed] delegate " + resultType + " Read([Borrowed, Lifetime(\"a\")] Resource input); " +
            "class Example { " + producer + " Read MethodGroup() => Wrap; Read Lambda() => ([Borrowed] Resource value) => Wrap(value); }";
        Assert.Empty(await AnalyzeAsync(Prelude + body));
    }

    [Theory]
    [InlineData("System.Threading.Tasks.Task<Resource>")]
    [InlineData("System.Threading.Tasks.ValueTask<Resource>")]
    public async Task CarrierIdentityCannotClaimItsPayloadUsesTheCarrierLifetime(string carrierType)
    {
        var body = "[return: Lifetime(\"a\"), Borrowed] delegate " + carrierType + " Read([Borrowed, Lifetime(\"a\")] " + carrierType + " input); " +
            "class Example { static " + carrierType + " Identity([Borrowed] " + carrierType + " value) => value; Read M() => [|Identity|]; }";
        var (source, span) = ExtractMarker(Prelude + body);
        var diagnostic = Assert.Single(await AnalyzeAsync(source), diagnostic => diagnostic.Id == "SS0047");
        Assert.Equal(span, diagnostic.Location.SourceSpan);
        Assert.Contains("payload", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("System.Threading.Tasks.Task<Resource>")]
    [InlineData("System.Threading.Tasks.ValueTask<Resource>")]
    public async Task AwaitingAnUnknownCarrierDoesNotInferADelegatePayloadRegion(string carrierType)
    {
        var body = "[return: Lifetime(\"a\"), Borrowed] delegate " + carrierType + " Read([Borrowed, Lifetime(\"a\")] " + carrierType + " input); " +
            "class Example { static async " + carrierType + " AwaitInput([Borrowed] " + carrierType + " value) => await value; Read M() => [|AwaitInput|]; }";
        var (source, span) = ExtractMarker(Prelude + body);
        var diagnostic = Assert.Single(await AnalyzeAsync(source), diagnostic => diagnostic.Id == "SS0047");
        Assert.Equal(span, diagnostic.Location.SourceSpan);
    }

    [Fact]
    public async Task PayloadSourceCannotPromiseAnIndependentDelegateInput()
    {
        var (source, span) = ExtractMarker(Prelude + """
            [return: Lifetime("a"), Borrowed]
            delegate System.Threading.Tasks.Task<Resource> Pick([Borrowed, Lifetime("a")] Resource first,
                [Borrowed, Lifetime("b")] Resource second);
            class Example {
                static System.Threading.Tasks.Task<Resource> Second([Borrowed] Resource first, [Borrowed] Resource second)
                    => System.Threading.Tasks.Task.FromResult(second);
                Pick M() => [|Second|];
            }
            """);
        var diagnostic = Assert.Single(await AnalyzeAsync(source), diagnostic => diagnostic.Id == "SS0047");
        Assert.Equal(span, diagnostic.Location.SourceSpan);
        Assert.Equal("one universal delegate binder", diagnostic.Properties["SafeSharp.RegionQuantification"]);
    }

    [Theory]
    [InlineData("([Borrowed] Resource value) => [|value|]", "")]
    [InlineData("([BorrowedMut] Resource value) => [|Shared(value)|]", "[ReturnsBorrowed(\"value\")] static Resource Shared([Borrowed] Resource value) => value;")]
    public async Task SharedBorrowsCannotAcquireAnExclusiveAnonymousResult(string expression, string helper)
    {
        var (source, span) = ExtractMarker(Prelude +
            "[return: Lifetime(\"a\"), BorrowedMut] delegate Resource Read([BorrowedMut, Lifetime(\"a\")] Resource input); " +
            "class Example { " + helper + " Read M() => " + expression + "; }");
        var diagnostic = Assert.Single(await AnalyzeAsync(source), diagnostic => diagnostic.Id == "SS0047");
        Assert.Equal(span, diagnostic.Location.SourceSpan);
        Assert.Contains("exclusive", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reflexive", diagnostic.Properties["SafeSharp.RegionProof"], StringComparison.Ordinal);
        Assert.NotEmpty(diagnostic.AdditionalLocations);
    }

    [Theory]
    [InlineData("safesharp_max_regions", "1")]
    [InlineData("safesharp_max_region_constraints", "1")]
    public async Task UniversalBindingBudgetsProduceExplicitIncompleteDiagnostics(string setting, string value)
    {
        var (source, span) = ExtractMarker(Prelude + """
            [return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource value);
            class Example
            {
                [return: Lifetime("x"), Borrowed] static Resource Id([Borrowed, Lifetime("x")] Resource value) => value;
                Read M() => [|Id|];
            }
            """);
        var diagnostics = await AnalyzeAsync(source, new Dictionary<string, string> { [setting] = value });
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0047");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0043" && diagnostic.Location.SourceSpan == span);
    }

    [Fact]
    public async Task AnonymousBodyBudgetDoesNotPublishPartialReturnViolations()
    {
        var locals = string.Join(" ", Enumerable.Range(0, 100).Select(index => "var alias" + index + " = value;"));
        var body = "[return: Lifetime(\"a\"), Borrowed] delegate Resource Read([Borrowed, Lifetime(\"a\")] Resource value); " +
            "class Example { Read M() => ([Borrowed] Resource value) => { " + locals + " return new Resource(); }; }";
        var diagnostics = await AnalyzeAsync(Prelude + body, new Dictionary<string, string> { ["safesharp_max_region_steps"] = "50" });
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0047");
        var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "SS0043");
        Assert.Contains("return-provenance", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryUnannotatedDelegateDoesNotUseTheLifetimeBudget() =>
        Assert.Empty(await AnalyzeAsync(Prelude + "class Example { Func<Resource, Resource> M() => value => new Resource(); }",
            new Dictionary<string, string> { ["safesharp_max_region_steps"] = "1" }));

    [Fact]
    public async Task SuppressionIsRespectedAtTheCallbackConversion() =>
        Assert.Empty(await AnalyzeAsync(Prelude + """

            #pragma warning disable SS0047
            [return: Lifetime("a"), Borrowed] delegate Resource Read([Borrowed, Lifetime("a")] Resource value);
            class Example { Read M() => ([Borrowed] Resource value) => new Resource(); }
            """));

    [Fact]
    public async Task ConcurrentConversionsProduceDeterministicEvidence()
    {
        var body = """
            [return: Lifetime("a"), Borrowed] delegate Resource Pick([Borrowed, Lifetime("a")] Resource first, [Borrowed, Lifetime("b")] Resource second);
            class Example {
                [return: Lifetime("y"), Borrowed] static Resource Second([Borrowed, Lifetime("x")] Resource first, [Borrowed, Lifetime("y")] Resource second) => second;
                Pick M() => Second;
            }
            """;
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AnalyzeAsync(Prelude + body)));
        static string Evidence(ImmutableArray<Diagnostic> diagnostics) => string.Join("\n", diagnostics.Select(diagnostic => diagnostic.ToString()
            + string.Join("|", diagnostic.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value))));
        Assert.All(results, diagnostics => Assert.Equal(Evidence(results[0]), Evidence(diagnostics)));
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, IReadOnlyDictionary<string, string>? settings = null)
    {
        var compilation = AnalyzerTestHost.CreateCompilation(source);
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(diagnostic => diagnostic.ToString())));
        var analyzerOptions = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, new ConfigProvider(settings));
        var options = new CompilationWithAnalyzersOptions(analyzerOptions, onAnalyzerException: null, concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false);
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
