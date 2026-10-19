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
using SafeSharp.Reporting;

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

var loader = new PackageAssemblyLoader();
var reference = new AnalyzerFileReference(Path.GetFullPath(args[0]), loader);
var loadFailures = new List<string>();
reference.AnalyzerLoadFailed += (_, failure) => loadFailures.Add(failure.Message);
var analyzers = reference.GetAnalyzers(LanguageNames.CSharp);
Require(loadFailures.Count == 0 && !analyzers.IsEmpty,
    "Packaged analyzers did not load on Roslyn 4.14: " + string.Join("; ", loadFailures));
var declaredRules = analyzers.SelectMany(analyzer => analyzer.SupportedDiagnostics).Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
Require(new[] { "SS0001", "SS0017", "SS0021", "SS0024", "SS0041", "SS0044", "SS0045" }.All(declaredRules.Contains),
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

Console.WriteLine("Package host: Roslyn 4.14 discovered every analyzer family; MEF exported the code fix; SS0024 was fixed in a live compilation; Core and Reporting packages executed.");

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
