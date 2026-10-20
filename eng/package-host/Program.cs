using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SafeSharp.Core;
using SafeSharp.Core.Regions;
using SafeSharp.Reporting;
using ReportDiagnostic = SafeSharp.Reporting.ReportDiagnostic;

if (args.Length != 2)
    throw new ArgumentException("Expected the packaged analyzer and code-fix assembly paths.");

Require(typeof(Compilation).Assembly.GetName().Version is { Major: 4, Minor: 14 },
    "The package compatibility host must use Roslyn 4.14.");

// These references are resolved by NuGet from the isolated local package feed.
// An actual ownership result exercises Core's immutable-collection dependency and
// Reporting's package dependency on the exact matching Core release.
var site = new SourceSite("PackageHost.cs", 0, 1);
var graph = new AnalysisGraph("Packaged core", [new FlowBlock(0,
    [new FlowInstruction(InstructionKind.Allocate, site, target: "owner", resourceId: "resource"),
     new FlowInstruction(InstructionKind.Dispose, site, source: "owner"),
     new FlowInstruction(InstructionKind.Read, site, source: "owner")], [])], 0, site);
var analysis = new OwnershipAnalysisEngine().Analyze(graph, new AnalysisOptions(captureSnapshots: true));
Require(analysis.Issues.Any(issue => issue.Id == "SS0001"), "Packaged Core did not analyze the ownership graph.");
using (var report = JsonDocument.Parse(AnalysisReportRenderer.Render(new AnalysisReport([analysis]), ReportFormat.Json)))
    Require(report.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item => item.GetProperty("id").GetString() == "SS0001"),
        "Packaged Reporting did not serialize the Core diagnostic.");

var universal = new UniversalRegionSolver().Solve(new UniversalRegionProblem(
    new RegionProblem(["a", "b"]), ["input"],
    [new RegionConstraint("b", "input", site, "input shortening"),
     new RegionConstraint("input", "a", site, "promised output")]));
Require(universal.IsComplete && !universal.IsCompatible && universal.Failures.Length == 1 &&
    universal.Failures[0].Longer == "b" && universal.Failures[0].Shorter == "a" &&
    universal.Failures[0].Proof.Steps.Length == 2,
    "Packaged universal solver did not retain the shortest unpromised relation proof.");

var higherRanked = new HigherRankedRegionSolver().Solve(new HigherRankedRegionProblem(
    [new RegionBinder("root"), new RegionBinder("inner", "root")],
    [new RegionVariable("a", "root", RegionVariableKind.Universal),
     new RegionVariable("input", "root", RegionVariableKind.Inference),
     new RegionVariable("fresh", "inner", RegionVariableKind.Placeholder)],
    requirements:
    [new RegionConstraint("a", "input", site, "input shortening"),
     new RegionConstraint("input", "fresh", site, "unnameable nested output")]));
Require(higherRanked.IsComplete && !higherRanked.IsCompatible && !higherRanked.Failures.IsEmpty &&
    !higherRanked.Promotions.IsEmpty && higherRanked.Promotions.All(promotion => promotion.Proof.Steps.Length > 0),
    "Packaged higher-ranked solver lost the nested placeholder leak or its original visibility evidence.");

var loader = new PackageAssemblyLoader();
var reference = new AnalyzerFileReference(Path.GetFullPath(args[0]), loader);
var loadFailures = new List<string>();
reference.AnalyzerLoadFailed += (_, failure) => loadFailures.Add(failure.Message);
var analyzers = reference.GetAnalyzers(LanguageNames.CSharp);
Require(loadFailures.Count == 0 && !analyzers.IsEmpty,
    "Packaged analyzers did not load on Roslyn 4.14: " + string.Join("; ", loadFailures));
var declaredRules = analyzers.SelectMany(analyzer => analyzer.SupportedDiagnostics).Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
Require(new[] { "SS0001", "SS0017", "SS0021", "SS0024", "SS0041", "SS0044", "SS0045", "SS0047", "SS0048", "SS0049" }.All(declaredRules.Contains),
    "Packaged analyzer discovery lost an analyzer family.");

var codeFixAssembly = loader.LoadFromPath(Path.GetFullPath(args[1]));
using var composition = new ContainerConfiguration().WithAssembly(codeFixAssembly).CreateContainer();
var provider = composition.GetExports<CodeFixProvider>().Single(candidate => candidate.FixableDiagnosticIds.Contains("SS0024"));
var export = provider.GetType().GetCustomAttribute<ExportCodeFixProviderAttribute>();
Require(export?.Languages.Contains(LanguageNames.CSharp) == true && provider.GetFixAllProvider() is not null,
    "The packaged provider did not expose its C# export or Fix All support.");

using var workspace = new AdhocWorkspace();
var frameworkAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
    ?? throw new InvalidOperationException("The host runtime did not provide its framework assembly list."))
    .Split(Path.PathSeparator).Append(typeof(SafeSharp.Ownership).Assembly.Location)
    .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path));
var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(),
    "PackagedFix", "PackagedFix", LanguageNames.CSharp,
    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
    parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp13),
    metadataReferences: frameworkAssemblies));
var document = workspace.AddDocument(project.Id, "Example.cs", SourceText.From("""
    using SafeSharp;
    public sealed class Example
    {
        [MustUse] private int Compute() => 42;
        public void Run() { Compute(); }
    }
    """));
var compilation = await document.Project.GetCompilationAsync() ?? throw new InvalidOperationException("No C# compilation was created.");
AssertCompiles(compilation);
var diagnostics = await AnalyzeAsync(compilation, analyzers);
var mustUse = diagnostics.Single(diagnostic => diagnostic.Id == "SS0024");
var actions = new List<CodeAction>();
await provider.RegisterCodeFixesAsync(new CodeFixContext(document, mustUse,
    (registered, _) => actions.Add(registered), CancellationToken.None));
var action = actions.Single();
var operations = await action.GetOperationsAsync(CancellationToken.None);
var changed = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution.GetDocument(document.Id)
    ?? throw new InvalidOperationException("The packaged code fix removed the original document.");

// Keep the actual transformed syntax tree. Re-parsing its text can hide missing
// contextual token metadata that causes binding failures inside a live IDE.
var changedCompilation = await changed.Project.GetCompilationAsync() ?? throw new InvalidOperationException("No changed compilation was created.");
AssertCompiles(changedCompilation);
Require((await AnalyzeAsync(changedCompilation, analyzers)).All(diagnostic => diagnostic.Id != "SS0024"),
    "The packaged code fix did not eliminate SS0024.");
Require((await changed.GetTextAsync()).ToString().Contains("_ = Compute();", StringComparison.Ordinal),
    "The packaged code fix did not preserve and acknowledge the computation.");

var callbackSource = CSharpSyntaxTree.ParseText("""
    using SafeSharp;
    [return: Lifetime("a"), Borrowed]
    delegate object Select([Borrowed, Lifetime("a")] object first, [Borrowed, Lifetime("b")] object second);
    static class Callbacks
    {
        [return: Lifetime("y"), Borrowed]
        static object Second([Borrowed, Lifetime("x")] object first, [Borrowed, Lifetime("y")] object second) => second;
        static Select Invalid() => Second;
        static Select InvalidLambda() => ([Borrowed] object first, [Borrowed] object second) => second;
        static Select ValidLambda() => ([Borrowed] object first, [Borrowed] object second) => first;
    }
    """, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp13), "Callbacks.cs");
var callbacks = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(callbackSource);
AssertCompiles(callbacks);
var callbackDiagnostics = (await AnalyzeAsync(callbacks, analyzers)).Where(diagnostic => diagnostic.Id == "SS0047").ToArray();
Require(callbackDiagnostics.Length == 2 && callbackDiagnostics.All(diagnostic => diagnostic.AdditionalLocations.Count > 0) &&
    callbackDiagnostics.Any(diagnostic => diagnostic.Properties.TryGetValue("SafeSharp.RegionQuantification", out var binder) &&
        binder == "one universal delegate binder"),
    "Packaged delegate method-group/lambda analysis or universal evidence failed on Roslyn 4.14.");

// Exercise the new lowering and source summaries on the minimum supported Roslyn
// host, independently of the newer compiler and Workspaces used by the SDK/CLI.
var asyncSource = CSharpSyntaxTree.ParseText("""
    using System.IO;
    using System.Threading.Tasks;
    static class AsyncFactories
    {
        private static Task<MemoryStream> Open() => Task.FromResult(new MemoryStream());
        public static async Task Safe()
        {
            var pending = Open();
            var configured = pending.ConfigureAwait(false);
            using var stream = await configured;
            _ = stream.Length;
        }
        public static void Ignored() { var pending = Open(); }
        public static async Task Repeated()
        {
            var pending = Open();
            var first = await pending;
            first.Dispose();
            var second = await pending;
            _ = second.Length;
        }
    }
    """, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp13), "AsyncFactories.cs");
var asyncCompilation = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(asyncSource);
AssertCompiles(asyncCompilation);
var asyncDiagnostics = await AnalyzeAsync(asyncCompilation, analyzers);
Require(MethodDiagnostics(asyncCompilation, asyncDiagnostics, "AsyncFactories", "Open").Length == 0 &&
    MethodDiagnostics(asyncCompilation, asyncDiagnostics, "AsyncFactories", "Safe").Length == 0,
    "Packaged Roslyn 4.14 analysis rejected a transferred producer or a safely disposed stored async result.");
var ignored = MethodDiagnostics(asyncCompilation, asyncDiagnostics, "AsyncFactories", "Ignored");
Require(ignored.Length == 1 && ignored[0].Id == "SS0003" &&
    ignored[0].Properties.TryGetValue("SafeSharp.Certainty", out var productionCertainty) && productionCertainty == "Possible" &&
    ignored[0].GetMessage().Contains("factory may produce", StringComparison.Ordinal),
    "Packaged Roslyn 4.14 analysis lost the ignored carrier's possible production obligation.");
var repeated = MethodDiagnostics(asyncCompilation, asyncDiagnostics, "AsyncFactories", "Repeated");
Require(repeated.Any(diagnostic => diagnostic.Id == "SS0001" &&
        diagnostic.Properties.TryGetValue("SafeSharp.Certainty", out var certainty) && certainty == "Definite") &&
    repeated.All(diagnostic => diagnostic.Id != "SS0003"),
    "Packaged Roslyn 4.14 analysis did not retain disposal of the same output across repeated awaits.");

// Keep this fixture on C# 13/Roslyn 4.14 APIs. The contracts remain netstandard2.0;
// a plain object gate exercises Monitor without depending on System.Threading.Lock.
var boundarySource = CSharpSyntaxTree.ParseText("""
    using System;
    using System.Threading.Tasks;
    using SafeSharp;
    [return: TypeLifetime("fresh", "return")]
    delegate Func<object> UniversalFactory([Lifetime("a")] object value);
    [return: TypeLifetime("^a", "return")]
    delegate Func<object> TiedFactory([Lifetime("a")] object value);
    static class BoundaryContracts
    {
        [return: TypeLifetime("^x", "return")]
        static Func<object> Capture([Lifetime("x")] object value) => () => value;
        static UniversalFactory NestedLeak() => Capture;
        static TiedFactory SafeCapture() => Capture;
        static async Task RepeatValueTask()
        {
            var pending = new ValueTask<int>(Task.FromResult(1));
            _ = await pending;
            _ = await pending;
        }
        static async Task ReusableValueTask()
        {
            var pending = new ValueTask<int>(Task.FromResult(1));
            var task = pending.AsTask();
            _ = await task;
            _ = await task;
        }
    }
    sealed class GuardedCounter
    {
        private readonly object gate = new object();
        [GuardedBy(nameof(gate))] private int value;
        public void Safe() { lock (gate) value++; }
        public void Unsafe() { value++; }
    }
    """, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp13), "BoundaryContracts.cs");
var boundaries = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(boundarySource);
AssertCompiles(boundaries);
var boundaryDiagnostics = await AnalyzeAsync(boundaries, analyzers);
foreach (var method in new[] { "SafeCapture", "ReusableValueTask" })
    Require(MethodDiagnostics(boundaries, boundaryDiagnostics, "BoundaryContracts", method).Length == 0,
        "Packaged Roslyn 4.14 analysis rejected the valid boundary fixture " + method + ".");
Require(MethodDiagnostics(boundaries, boundaryDiagnostics, "GuardedCounter", "Safe").Length == 0,
    "Packaged Roslyn 4.14 analysis rejected access protected by its declared Monitor gate.");
var nested = MethodDiagnostics(boundaries, boundaryDiagnostics, "BoundaryContracts", "NestedLeak").Single();
Require(nested.Id == "SS0047" && nested.AdditionalLocations.Count > 0 &&
    new[] { "SafeSharp.RegionUniverses", "SafeSharp.RegionUniversePromotions", "SafeSharp.RegionVisibilityProofs" }
        .All(key => nested.Properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)),
    "Packaged Roslyn 4.14 nested capture analysis lost its universe leak evidence.");
var repeatedValueTask = MethodDiagnostics(boundaries, boundaryDiagnostics, "BoundaryContracts", "RepeatValueTask").Single();
Require(repeatedValueTask.Id == "SS0048" && repeatedValueTask.AdditionalLocations.Count > 0 &&
    repeatedValueTask.Properties.TryGetValue("SafeSharp.Certainty", out var consumptionCertainty) && consumptionCertainty == "Definite",
    "Packaged Roslyn 4.14 analysis did not retain single-consumption ValueTask identity.");
var unguarded = MethodDiagnostics(boundaries, boundaryDiagnostics, "GuardedCounter", "Unsafe").Single();
Require(unguarded.Id == "SS0049" && unguarded.AdditionalLocations.Count > 0 &&
    unguarded.Properties.TryGetValue("SafeSharp.SynchronizationRequired", out var requiredGuard) && requiredGuard == "gate" &&
    unguarded.Properties.TryGetValue("SafeSharp.SynchronizationProtocol", out var protocol) && protocol == "Monitor" &&
    unguarded.Properties.ContainsKey("SafeSharp.SynchronizationHeld"),
    "Packaged Roslyn 4.14 analysis lost the unheld guard's contract or protocol evidence.");
var nestedReport = new AnalysisReport([], [new ReportDiagnostic(nested.Id, nested.GetMessage(),
    new SourceSite("BoundaryContracts.cs", nested.Location.SourceSpan.Start, nested.Location.SourceSpan.Length),
    properties: nested.Properties)]);
var nestedHtml = AnalysisReportRenderer.Render(nestedReport, ReportFormat.Html);
Require(nestedHtml.Contains("data-region=\"$static\"", StringComparison.Ordinal) &&
    nestedHtml.Contains("SafeSharp.RegionVisibilityProofs", StringComparison.Ordinal) &&
    !nestedHtml.Contains("Region graph omitted", StringComparison.OrdinalIgnoreCase),
    "Packaged Reporting omitted the computed higher-ranked graph or its visibility proof.");
var guardReport = new AnalysisReport([], [new ReportDiagnostic(unguarded.Id, unguarded.GetMessage(),
    new SourceSite("BoundaryContracts.cs", unguarded.Location.SourceSpan.Start, unguarded.Location.SourceSpan.Length),
    properties: unguarded.Properties)]);
var guardHtml = AnalysisReportRenderer.Render(guardReport, ReportFormat.Html);
Require(guardHtml.Contains("class=\"synchronization-graph\"", StringComparison.Ordinal) &&
    guardHtml.Contains("No capability records", StringComparison.Ordinal) &&
    !guardHtml.Contains("Synchronization graph omitted", StringComparison.Ordinal),
    "Packaged Reporting lost the computed Monitor guard requirement or invented an observed capability.");

Console.WriteLine("Package host: Roslyn 4.14 discovered every analyzer family; checked delegates, nested lifetime leaks, stored/repeated async results, ValueTask consumption and Monitor guards; MEF fixed SS0024 in a live compilation; packaged Core ownership/universal/higher-ranked solvers and Reporting executed.");

static Diagnostic[] MethodDiagnostics(Compilation compilation, ImmutableArray<Diagnostic> diagnostics, string typeName, string methodName)
{
    var methods = compilation.GetTypeByMetadataName(typeName)?.GetMembers(methodName).OfType<IMethodSymbol>()
        .Where(method => method.Parameters.IsEmpty).ToArray() ?? [];
    Require(methods.Length == 1 && methods[0].DeclaringSyntaxReferences.Length == 1,
        "The package-host fixture must contain one exact method: " + typeName + "." + methodName + "().");
    var declaration = methods[0].DeclaringSyntaxReferences[0];
    return diagnostics.Where(diagnostic => diagnostic.Location.SourceTree == declaration.SyntaxTree &&
        declaration.Span.Contains(diagnostic.Location.SourceSpan)).ToArray();
}

static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Compilation compilation, ImmutableArray<DiagnosticAnalyzer> analyzers)
{
    var diagnostics = await compilation.WithAnalyzers(analyzers).GetAnalyzerDiagnosticsAsync();
    Require(diagnostics.All(diagnostic => diagnostic.Id is not ("AD0001" or "AD0002")),
        "A packaged analyzer failed on Roslyn 4.14: " + string.Join("; ", diagnostics));
    return diagnostics;
}

static void AssertCompiles(Compilation compilation)
{
    var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
    Require(errors.Length == 0, "The package-host document did not compile: " + string.Join("; ", errors.Select(error => error.ToString())));
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class PackageAssemblyLoader : IAnalyzerAssemblyLoader
{
    // Roslyn and the runtime-library package references are supplied by this host;
    // analyzer and code-fix assemblies are loaded directly from the NuGet assets.
    public void AddDependencyLocation(string fullPath) { }
    public Assembly LoadFromPath(string fullPath) => AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
}
