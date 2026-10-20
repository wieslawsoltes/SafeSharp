using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SafeSharp.Core;

namespace SafeSharp.Tests.Core;

public sealed class PublicApiCompatibilityTests
{
    // These declarations reproduce the 0.3.0-preview.1 CLR surface at 9b575f9.
    // Compile a consumer against that surface, then resolve it to the current Core assembly.
    // Recompiling the consumer against today's optional parameters would conceal a missing ABI.
    private const string LegacyReference = """
        [assembly: System.Reflection.AssemblyVersion("0.3.0.0")]
        namespace SafeSharp.Core
        {
            public enum InstructionKind { Nop }
            public enum ResourceOwnership { Owned, Borrowed, External, Scoped }
            [System.Flags] public enum InstructionFlags { None = 0 }
            public sealed class SourceSite
            {
                public SourceSite(string path, int start, int length, int line = 0, int column = 0) { }
            }
            public sealed class FlowInstruction
            {
                public FlowInstruction(InstructionKind kind, SourceSite site, string target = null,
                    string source = null, string resourceId = null,
                    ResourceOwnership ownership = ResourceOwnership.Owned, string owner = null,
                    InstructionFlags flags = InstructionFlags.None, string description = null) { }
            }
        }
        """;

    [Fact]
    public void PreviouslyCompiledConsumerBindsTheOriginalInstructionConstructor()
    {
        var frameworkReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => !Path.GetFileName(path).StartsWith("SafeSharp.", StringComparison.Ordinal))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToImmutableArray();
        var legacyCore = Emit("SafeSharp.Core", LegacyReference, frameworkReferences);
        var consumer = Emit("LegacySafeSharpConsumer", """
            public static class LegacyConsumer
            {
                public static object Create() => new SafeSharp.Core.FlowInstruction(
                    SafeSharp.Core.InstructionKind.Nop,
                    new SafeSharp.Core.SourceSite("legacy.cs", 3, 2), target: "legacy");
            }
            """, frameworkReferences.Add(MetadataReference.CreateFromImage(legacyCore)));
        var context = new CurrentCoreContext();
        try
        {
            using var stream = new MemoryStream(consumer);
            var assembly = context.LoadFromStream(stream);
            var created = assembly.GetType("LegacyConsumer")!.GetMethod("Create")!.Invoke(null, null);
            var instruction = Assert.IsType<FlowInstruction>(created);
            Assert.Equal("legacy", instruction.Target);
            Assert.Equal("legacy.cs", instruction.Site.Path);
            Assert.Equal(3, instruction.Site.Start);
            Assert.Equal(ResourceOwnership.Owned, instruction.Ownership);
            Assert.Equal(1, instruction.AsyncResultDepth);
            Assert.Equal(AsyncCompositionKind.None, instruction.AsyncComposition);
            Assert.Null(instruction.Projection);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void OriginalConstructorSignaturesAndAccessibilityAreRetained()
    {
        var instruction = Constructor(typeof(FlowInstruction), BindingFlags.Public,
            [typeof(InstructionKind), typeof(SourceSite), typeof(string), typeof(string), typeof(string),
                typeof(ResourceOwnership), typeof(string), typeof(InstructionFlags), typeof(string)],
            ["kind", "site", "target", "source", "resourceId", "ownership", "owner", "flags", "description"]);
        Assert.True(instruction.IsPublic);

        var resource = Constructor(typeof(ResourceSnapshot), BindingFlags.NonPublic,
            [typeof(string), typeof(ResourceOwnership), typeof(ResourceStatus), typeof(SourceSite),
                typeof(ImmutableArray<string>), typeof(ResourceKind), typeof(ImmutableArray<string>),
                typeof(AsyncResultState), typeof(ImmutableArray<string>)],
            ["id", "ownership", "status", "origin", "ownerResourceIds", "kind", "disposedResourceIds", "resultState", "producedResourceIds"]);
        Assert.True(resource.IsAssembly);
        var snapshot = Assert.IsType<ResourceSnapshot>(resource.Invoke([
            "old-owner", ResourceOwnership.Owned, ResourceStatus.Alive, new SourceSite("legacy.cs", 0, 0),
            ImmutableArray<string>.Empty, ResourceKind.Resource, default(ImmutableArray<string>),
            AsyncResultState.None, default(ImmutableArray<string>)]));
        Assert.Equal(1, snapshot.AsyncResultDepth);
        Assert.Equal(AsyncCompositionKind.None, snapshot.AsyncComposition);
        Assert.Empty(snapshot.DependencyResourceIds);
        Assert.Empty(snapshot.Elements);
        Assert.False(snapshot.SummarizesMultipleAggregates);

        var state = Constructor(typeof(StateSnapshot), BindingFlags.NonPublic,
            [typeof(ImmutableArray<PlaceSnapshot>), typeof(ImmutableArray<ResourceSnapshot>),
                typeof(ImmutableArray<LoanSnapshot>), typeof(bool)],
            ["places", "resources", "loans", "isReachable"]);
        Assert.True(state.IsAssembly);
        var stateSnapshot = Assert.IsType<StateSnapshot>(state.Invoke([
            ImmutableArray<PlaceSnapshot>.Empty, ImmutableArray.Create(snapshot), ImmutableArray<LoanSnapshot>.Empty, false]));
        Assert.False(stateSnapshot.IsReachable);
        Assert.Same(snapshot, Assert.Single(stateSnapshot.Resources));
        Assert.Empty(stateSnapshot.ValueTasks);
        Assert.Empty(stateSnapshot.ReferenceStorage);
    }

    private static ConstructorInfo Constructor(Type type, BindingFlags visibility, Type[] parameters, string[] names)
    {
        var constructor = type.GetConstructor(BindingFlags.Instance | visibility, binder: null, parameters, modifiers: null);
        Assert.NotNull(constructor);
        Assert.Equal(names, constructor.GetParameters().Select(parameter => parameter.Name));
        return constructor;
    }

    private static byte[] Emit(string name, string source, IEnumerable<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return stream.ToArray();
    }

    private sealed class CurrentCoreContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name == "SafeSharp.Core" ? typeof(FlowInstruction).Assembly : null;
    }
}
