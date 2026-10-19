using SafeSharp.Analyzers;

namespace SafeSharp.Tests.Flow;

public sealed class MethodSummaryTests
{
    private const string Prelude = """
        using System;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } public void Read() { } }
        """;
    private const string OperatorPrelude = """
        using System;
        using SafeSharp;
        sealed class Resource : IDisposable {
            public static Resource Replacement = null!;
            public void Dispose() { }
            public void Read() { }
            public static Resource operator +(Resource left, Resource right) => right;
            public static Resource operator ++(Resource value) => Replacement;
            public static Resource operator --(Resource value) => Replacement;
        }
        """;

    [Theory]
    [InlineData("Resource Factory() { var value = new Resource(); return value; }", "var value = Factory();", "SS0003")]
    [InlineData("Resource Factory(bool condition = true) { Resource value; if (condition) value = new Resource(); else value = new Resource(); return value; }", "var value = Factory();", "SS0003")]
    [InlineData("Resource Inner() => new Resource(); Resource Factory() => Inner();", "var value = Factory();", "SS0003")]
    [InlineData("Resource Factory() { var first = new Resource(); var second = first; return second; }", "var value = Factory();", "SS0003")]
    [InlineData("void Create(out Resource value) { value = new Resource(); }", "Create(out var value);", "SS0003")]
    [InlineData("void Create(out Resource value) { value = new Resource(); }", "Create(out var value); value.Dispose(); value.Read();", "SS0001")]
    [InlineData("void Create(out Resource value) { value = new Resource(); }", "var value = new Resource(); Create(out value); value.Dispose();", "SS0011")]
    [InlineData("Resource Forward(Resource input) { var alias = input; return alias; }", "using var value = new Resource(); var alias = Forward(value); value.Dispose(); alias.Read();", "SS0001")]
    [InlineData("void Create([Owned] out Resource value) { value = default!; }", "Create(out var value);", "SS0003")]
    public async Task AppliesInferredOriginsToCaller(string members, string body, string id)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "class Example { " + members + " void M() { " + body + " } }");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == id);
    }

    [Theory]
    [InlineData("Resource Factory() { var value = new Resource(); return value; }", "using var value = Factory(); value.Read();")]
    [InlineData("Resource Factory(bool condition = true) { Resource value; if (condition) value = new Resource(); else value = new Resource(); return value; }", "using var value = Factory();")]
    [InlineData("void Create(out Resource value) { value = new Resource(); }", "Create(out var value); value.Dispose();")]
    [InlineData("void Create(out Resource value) { value = new Resource(); }", "Resource value; Create(out value); value.Dispose();")]
    [InlineData("void Create(out Resource value) { value = new Resource(); }", "var value = new Resource(); value.Dispose(); Create(out value); value.Dispose();")]
    [InlineData("Resource Forward(Resource input) { var alias = input; return alias; }", "using var value = new Resource(); var alias = Forward(value); alias.Read();")]
    [InlineData("Resource Forward(Resource input, bool condition = true) { Resource value; if (condition) value = input; else value = input; return value; }", "using var value = new Resource(); Forward(value).Read();")]
    [InlineData("Resource Recursive() => Recursive();", "Recursive();")]
    public async Task AcceptsTransferredResultsAndObservedOutputs(string members, string body)
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "class Example { " + members + " void M() { " + body + " } }");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task DoesNotInferOwnershipFromAnOverridableFactory()
    {
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(Prelude + "class Example { public virtual Resource Create() => new Resource(); void M() { var value = Create(); } }");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003");
    }

    [Theory]
    [InlineData("Resource Factory(Resource external) { var value = new Resource(); Replace(ref value, external); return value; } void Replace(ref Resource value, Resource external) { value = external; }")]
    [InlineData("Resource Factory(Resource external) { var value = new Resource(); ref Resource alias = ref value; alias = external; return value; }")]
    [InlineData("Resource Factory(Resource external) { var value = new Resource(); _ = new Replacer(ref value, external); return value; } sealed class Replacer { public Replacer(ref Resource value, Resource external) { value = external; } }")]
    [InlineData("Resource Factory(Resource external) { var value = new Resource(); _ = new Box { Value = Replace(ref value, external) }; return value; } Resource Replace(ref Resource value, Resource external) { value = external; return value; } sealed class Box { public Resource? Value { get; set; } }")]
    [InlineData("Resource Factory(Resource external) { try { return new Resource(); } catch { return external; } }")]
    [InlineData("Resource Factory(Resource external) { return System.DateTime.UtcNow.Ticks > 0 ? new Resource() : external; }")]
    public async Task OpaqueEffectsCannotEstablishACallerOwnershipPostcondition(string members)
    {
        var source = Prelude + "class Example { " + members + " void M(Resource external) { var value = Factory(external); } }";
        var callStart = source.LastIndexOf("Factory(external)", StringComparison.Ordinal);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == callStart);
    }

    [Fact]
    public async Task CapturedOuterParametersDoNotAliasLocalFunctionInputOrdinals()
    {
        var source = Prelude + """
            class Example {
                void M(Resource captured) {
                    Resource Local(Resource input) => captured;
                    var owner = new Resource(); var result = Local(owner);
                    owner.Dispose(); result.Read();
                }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0001" &&
            diagnostic.Location.SourceSpan.Start == source.IndexOf("result.Read()", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("var value = new Resource(); value += external; return value;")]
    [InlineData("var value = new Resource(); value++; return value;")]
    [InlineData("var value = new Resource(); --value; return value;")]
    [InlineData("var value = new Resource(); (value, _) = (external, 0); return value;")]
    [InlineData("var value = new Resource(); (_, (value, _)) = (true, (external, false)); return value;")]
    [InlineData("var value = new Resource(); if (condition) value += external; return value;")]
    public async Task ReplacedSlotsDoNotKeepAStaleOwnedReturnPostcondition(string body)
    {
        var source = OperatorPrelude + "class Example { Resource Factory(Resource external, bool condition) { " + body +
            " } void M(Resource external, bool condition) { Resource.Replacement = external; var result = Factory(external, condition); } }";
        var call = source.LastIndexOf("Factory(external, condition)", StringComparison.Ordinal);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }

    [Theory]
    [InlineData("value = new Resource(); value += external;")]
    [InlineData("value = new Resource(); value++;")]
    [InlineData("value = new Resource(); (_, (value, _)) = (true, (external, false));")]
    public async Task ReplacedOutSlotsDoNotKeepAStaleOwnedOutputPostcondition(string body)
    {
        var source = OperatorPrelude + "class Example { void Create(Resource external, out Resource value) { " + body +
            " } void M(Resource external) { Resource.Replacement = external; Create(external, out var result); } }";
        var output = source.LastIndexOf("out var result", StringComparison.Ordinal);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == output);
    }

    [Theory]
    [InlineData("input += external; return input;")]
    [InlineData("++input; return input;")]
    [InlineData("(input, _) = (external, 0); return input;")]
    public async Task ReplacedInputsDoNotKeepAStaleBorrowedSourcePostcondition(string body)
    {
        var source = OperatorPrelude + "class Example { Resource Forward(Resource input, Resource external) { " + body +
            " } void M(Resource external) { Resource.Replacement = external; var owner = new Resource(); var result = Forward(owner, external); owner.Dispose(); result.Read(); } }";
        var read = source.LastIndexOf("result.Read()", StringComparison.Ordinal);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == read);
    }

    [Theory]
    [InlineData("var result = new Resource(); var other = external; other += external; return result;")]
    [InlineData("var result = new Resource(); var other = external; (other, _) = (external, 0); return result;")]
    [InlineData("var result = new Resource(); return result++;")]
    public async Task IndependentAndPostfixOriginalOwnersKeepTheirProvenPostcondition(string body)
    {
        var source = OperatorPrelude + "class Example { Resource Factory(Resource external) { " + body +
            " } void M(Resource external) { Resource.Replacement = external; var result = Factory(external); } }";
        var call = source.LastIndexOf("Factory(external)", StringComparison.Ordinal);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }

    [Fact]
    public async Task AssigningThroughARefReturnEvaluatesTheTargetCallBeforeRetainingOrigins()
    {
        var source = Prelude + """
            class Example {
                static ref Resource Slot(ref Resource value) => ref value;
                Resource Factory(Resource external) {
                    var value = new Resource(); Slot(ref value) = external; return value;
                }
                void M(Resource external) { var result = Factory(external); }
            }
            """;
        var call = source.LastIndexOf("Factory(external)", StringComparison.Ordinal);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }

    [Theory]
    [InlineData("public Resource Value { get; set; } = null!;")]
    [InlineData("[ReturnsOwned] public Resource Value { get => new Resource(); set { } }")]
    public async Task PropertyAssignmentEvaluatesReceiverEffectsBeforeRetainingOrigins(string property)
    {
        var source = Prelude + "class Example { sealed class Box { " + property +
            " } Box Replace(ref Resource value, Resource external) { value = external; return new Box(); } " +
            "Resource Factory(Resource external) { var value = new Resource(); Replace(ref value, external).Value = external; return value; } " +
            "void M(Resource external) { var result = Factory(external); } }";
        var call = source.LastIndexOf("Factory(external)", StringComparison.Ordinal);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }
}
