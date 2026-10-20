using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Tests.Flow;

/// <summary>Exercises source summaries at caller boundaries, separately from direct producer lowering.</summary>
public sealed class AsyncOriginSummaryTests
{
    private const string Prelude = """
        using System;
        using System.Threading.Tasks;
        using SafeSharp;
        sealed class Resource : IDisposable { public void Dispose() { } public void Read() { } }
        sealed class DerivedTask : Task<Resource> { public DerivedTask(Resource input) : base(() => input) { } }
        class Example {
            static T Identity<T>(T value) => value;
            static Task<Resource> First(Task<Resource> value, Resource ignored) => value;
            static void Replace(ref Task<Resource> value, Task<Resource> other) => value = other;
            static Task<Resource> Owned() => Task.FromResult(new Resource());
            static Task<Task<Resource>> Nested(Resource input) => Task.FromResult(Task.FromResult(input));
            static T AllocateCarrier<T>(Resource input) => (T)(object)new DerivedTask(input);
        """;

    [Theory]
    [InlineData("return Task.FromResult(new Resource()).Result;")]
    [InlineData("return Task.FromResult(new Resource()).GetAwaiter().GetResult();")]
    [InlineData("return Task.FromResult(new Resource()).ConfigureAwait(false).GetAwaiter().GetResult();")]
    [InlineData("return new ValueTask<Resource>(new Resource()).Result;")]
    [InlineData("return ValueTask.FromResult(new Resource()).Result;")]
    [InlineData("return new ValueTask<Resource>(Task.FromResult(new Resource())).Result;")]
    [InlineData("return new ValueTask<Resource>(new Resource()).AsTask().Result;")]
    [InlineData("return new ValueTask<Resource>(new Resource()).Preserve().Result;")]
    [InlineData("return Identity(Task.FromResult(new Resource())).Result;")]
    [InlineData("return new ValueTask<Resource>(Identity(Owned())).ConfigureAwait(false).GetAwaiter().GetResult();")]
    [InlineData("return Task.FromResult(Task.FromResult(new Resource())).Result.Result;")]
    [InlineData("var value = new Resource(); var pending = Task.FromResult(value); value = input; return pending.Result;")]
    [InlineData("var value = new Resource(); ref Resource alias = ref value; var pending = Task.FromResult(alias); alias = input; return pending.Result;")]
    [InlineData("var value = new Resource(); return First(Task.FromResult(value), value = input).Result;")]
    [InlineData("Task<Resource> pending; if (choose) pending = Task.FromResult(new Resource()); else pending = Task.FromResult(new Resource()); return pending.Result;")]
    [InlineData("var pending = Task.FromResult(new Resource()); while (count-- > 0) pending = Task.FromResult(new Resource()); return pending.Result;")]
    [InlineData("var pending = opaque; do { pending = Task.FromResult(new Resource()); } while (count-- > 0); return pending.Result;")]
    public async Task ProvenPayloadOriginsEstablishCallerOwnership(string body)
    {
        var source = SynchronousFactory(body);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var call = source.LastIndexOf("Factory(input, opaque, choose, count)", StringComparison.Ordinal);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }

    [Theory]
    [InlineData("return opaque.Result;")]
    [InlineData("return Identity(opaque).Result;")]
    [InlineData("return new Task<Resource>(() => input).Result;")]
    [InlineData("return new Task<Resource>(() => new Resource()).Result;")]
    [InlineData("return new DerivedTask(input).Result;")]
    [InlineData("return AllocateCarrier<Task<Resource>>(input).Result;")]
    [InlineData("return new ValueTask<Resource>(opaque).Result;")]
    [InlineData("return Task.FromResult(opaque).Result.Result;")]
    [InlineData("return Nested(input).Result.Result;")]
    [InlineData("var pending = Task.FromResult(new Resource()); Replace(ref pending, opaque); return pending.Result;")]
    [InlineData("var pending = Task.FromResult(new Resource()); ref Task<Resource> alias = ref pending; alias = opaque; return pending.Result;")]
    [InlineData("var pending = Task.FromResult(new Resource()); if (choose) pending = opaque; return pending.Result;")]
    [InlineData("var pending = Task.FromResult(new Resource()); while (count-- > 0) pending = Task.FromResult(input); return pending.Result;")]
    [InlineData("var pending = opaque; while (count-- > 0) pending = Task.FromResult(new Resource()); return pending.Result;")]
    [InlineData("var value = input; var pending = Task.FromResult(value); value = new Resource(); return pending.Result;")]
    public async Task UnknownCarriersAndMixedPayloadsCannotInventOwnedResults(string body)
    {
        var source = SynchronousFactory(body);
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var call = source.LastIndexOf("Factory(input, opaque, choose, count)", StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }

    [Theory]
    [InlineData("return Task.FromResult(input).Result;")]
    [InlineData("return Identity(Task.FromResult(input)).Result;")]
    [InlineData("return new ValueTask<Resource>(Task.FromResult(input)).Result;")]
    [InlineData("return Task.FromResult(Task.FromResult(input)).Result.Result;")]
    [InlineData("var value = input; ref Resource alias = ref value; var pending = Task.FromResult(alias); alias = new Resource(); return pending.Result;")]
    [InlineData("var pending = Task.FromResult(input); if (choose) pending = Task.FromResult(input); return pending.Result;")]
    [InlineData("var pending = Task.FromResult(input); while (count-- > 0) pending = Identity(pending); return pending.Result;")]
    public async Task ExtractedPayloadsNameTheirEvaluatedInput(string body)
    {
        var source = Prelude + "Resource Factory(Resource input, bool choose, int count) { " + body +
            " } void Caller(Resource input, bool choose, int count) { var result = Factory(input, choose, count); result.Dispose(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var disposal = source.LastIndexOf("result.Dispose()", StringComparison.Ordinal);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0009" && diagnostic.Location.SourceSpan.Start == disposal);
    }

    [Theory]
    [InlineData("return await Task.FromResult(new Resource());")]
    [InlineData("return await Task.FromResult(new Resource()).ConfigureAwait(false);")]
    [InlineData("return await new ValueTask<Resource>(Task.FromResult(new Resource()));")]
    [InlineData("return await await Task.FromResult(Task.FromResult(new Resource()));")]
    [InlineData("var pending = Identity(Owned()); return await pending;")]
    public async Task EachExplicitAwaitExtractsOneProvenPayloadLayer(string body)
    {
        var source = Prelude + "async Task<Resource> Factory() { " + body +
            " } async Task Caller() { var result = await Factory(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var receipt = source.LastIndexOf("await Factory()", StringComparison.Ordinal);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == receipt);
    }

    [Theory]
    [InlineData("return await opaque;")]
    [InlineData("return await Identity(opaque);")]
    [InlineData("return await new ValueTask<Resource>(opaque);")]
    [InlineData("return await new Task<Resource>(() => input);")]
    [InlineData("return await await Task.FromResult(opaque);")]
    public async Task AwaitingAnUnknownCarrierCannotEstablishAPayloadPostcondition(string body)
    {
        var source = Prelude + "async Task<Resource> Factory(Resource input, Task<Resource> opaque) { " + body +
            " } async Task Caller(Resource input, Task<Resource> opaque) { var result = await Factory(input, opaque); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var receipt = source.LastIndexOf("await Factory(input, opaque)", StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == receipt);
    }

    [Fact]
    public async Task ANestedCarrierSummaryDoesNotFlattenTheInnerResource()
    {
        var source = Prelude + "void Caller() { var owner = new Resource(); var inner = Nested(owner).Result; inner.Dispose(); owner.Read(); owner.Dispose(); } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var disposal = source.IndexOf("inner.Dispose()", StringComparison.Ordinal);
        var read = source.IndexOf("owner.Read()", StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0009" && diagnostic.Location.SourceSpan.Start == disposal);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0001" && diagnostic.Location.SourceSpan.Start == read);
    }

    [Fact]
    public async Task ALoopWhichNestsCarriersReachesAFiniteConservativeSummary()
    {
        var source = Prelude + """
            object Build(object input, int count) {
                object current = input;
                while (count-- > 0) current = Task.FromResult(current);
                return current;
            }
            void Caller(object input, int count) { _ = Build(input, count); }
        }
        """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "AD0001" or "SS0043" or "SS0003");
    }

    [Fact]
    public async Task ANonGenericTaskKeepsItsOrdinaryDisposableCarrierOwnership()
    {
        const string source = """
            using System.Threading.Tasks;
            class Example {
                Task Create() => new Task(() => { });
                void Caller() { var result = Create(); }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var call = source.LastIndexOf("Create()", StringComparison.Ordinal);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }

    [Fact]
    public async Task SourceDefinedFrameworkNamesDoNotAcquirePayloadContracts()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            sealed class Resource : IDisposable { public void Dispose() { } }
            namespace System.Threading.Tasks {
                class Task<T> { public T Result => default!; }
                static class Task { public static Task<T> FromResult<T>(T input) => new Task<T>(); }
            }
            class Example {
                Resource Factory() => Task.FromResult(new Resource()).Result;
                void Caller() { var result = Factory(); }
            }
            """;
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source);
        var call = source.LastIndexOf("Factory()", StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0003" && diagnostic.Location.SourceSpan.Start == call);
    }

    [Theory]
    [InlineData("Task<Resource> Echo(Resource input) => Task.FromResult(input);")]
    [InlineData("ValueTask<Resource> Echo(Resource input) => ValueTask.FromResult(input);")]
    [InlineData("ValueTask<Resource> Echo(Resource input) => new ValueTask<Resource>(input);")]
    [InlineData("ValueTask<Resource> Echo(Resource input) => new ValueTask<Resource>(Task.FromResult(input));")]
    [InlineData("async Task<Resource> Echo(Resource input) { await Task.Yield(); return input; }")]
    [InlineData("async ValueTask<Resource> Echo(Resource input) { await Task.Yield(); return input; }")]
    public async Task InferredPayloadSummariesPreserveBorrowedEscapeEvidence(string producer)
    {
        var source = Prelude + "Resource field = null!; " + producer +
            " async Task Caller([Borrowed] Resource input) { var pending = Echo(input); field = await pending; } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, new SafeSharpSemanticAnalyzer());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SS0027");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SS0046");
    }

    [Theory]
    [InlineData("Task<Resource> Echo(Task<Resource> input) => input;")]
    [InlineData("T Echo<T>(T input) => input;")]
    public async Task CarrierIdentitySummariesDoNotInventBorrowedEscapeEvidence(string identity)
    {
        var source = Prelude + "Resource field = null!; " + identity +
            " async Task Caller([Borrowed] Task<Resource> input) { var pending = Echo(input); field = await pending; } }";
        var diagnostics = await AnalyzerTestHost.GetDiagnosticsAsync(source, new SafeSharpSemanticAnalyzer());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "SS0027" or "SS0046");
    }

    private static string SynchronousFactory(string body) => Prelude +
        "Resource Factory(Resource input, Task<Resource> opaque, bool choose, int count) { " + body +
        " } void Caller(Resource input, Task<Resource> opaque, bool choose, int count) { var result = Factory(input, opaque, choose, count); } }";
}
