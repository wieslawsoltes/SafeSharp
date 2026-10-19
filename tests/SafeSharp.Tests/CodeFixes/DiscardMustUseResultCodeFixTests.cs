using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SafeSharp.Analyzers.Semantics;
using SafeSharp.CodeFixes;

namespace SafeSharp.Tests.CodeFixes;

public sealed class DiscardMustUseResultCodeFixTests
{
    [Fact]
    public async Task Uses_a_discard_when_underscore_is_unbound()
    {
        var code = await FixAsync("using SafeSharp; class C { [MustUse] int Create() => 1; void M() { Create(); } }");
        Assert.Contains("_ = Create();", code);
    }

    [Fact]
    public async Task Await_evaluation_is_preserved()
    {
        var code = await FixAsync("using SafeSharp; using System.Threading.Tasks; class C { [MustUse] Task<int> Create() => Task.FromResult(1); async Task M() { await Create(); } }");
        Assert.Contains("_ = await Create();", code);
    }

    [Fact]
    public async Task Existing_underscore_local_is_not_assigned()
    {
        var code = await FixAsync("using SafeSharp; class C { [MustUse] int Create() => 1; void M() { int _ = 42; Create(); System.Console.WriteLine(_); } }");
        Assert.DoesNotContain("_ = Create();", code);
        Assert.Contains("unusedResult", code);
        Assert.Contains("int _ = 42;", code);
    }

    [Fact]
    public async Task Existing_underscore_field_is_not_assigned()
    {
        var code = await FixAsync("using SafeSharp; class C { int _; [MustUse] int Create() => 1; void M() { Create(); } }");
        Assert.DoesNotContain("_ = Create();", code);
        Assert.Contains("unusedResult", code);
    }

    [Fact]
    public async Task Existing_underscore_parameter_is_not_assigned()
    {
        var code = await FixAsync("using SafeSharp; class C { [MustUse] int Create() => 1; void M(int _) { Create(); } }");
        Assert.DoesNotContain("_ = Create();", code);
        Assert.Contains("unusedResult", code);
    }

    [Fact]
    public async Task Embedded_statement_fallback_gets_a_valid_block()
    {
        var code = await FixAsync("using SafeSharp; class C { [MustUse] int Create() => 1; void M(int _) { if (_ > 0) Create(); } }");
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var conditional = Assert.Single(root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IfStatementSyntax>());
        Assert.IsType<Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax>(conditional.Statement);
    }

    [Fact]
    public async Task Comments_are_preserved_once()
    {
        const string source = """
using SafeSharp;
class C
{
    [MustUse] int Create() => 1;
    void M()
    {
        // Before the call.
        Create() /* At the expression. */; // After the call.
    }
}
""";
        var code = await FixAsync(source);
        foreach (var comment in new[] { "// Before the call.", "/* At the expression. */", "// After the call." })
            Assert.Equal(1, code.Split(comment, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task Old_language_versions_use_a_local_instead_of_discard_syntax()
    {
        var code = await FixAsync("using SafeSharp; class C { [MustUse] int Create() => 1; void M() { Create(); } }", LanguageVersion.CSharp6);
        Assert.DoesNotContain("_ = Create();", code);
        Assert.Contains("unusedResult", code);
    }

    [Fact]
    public async Task Type_named_var_does_not_break_local_type_inference()
    {
        var code = await FixAsync("using SafeSharp; class @var {} class C { int _; [MustUse] int Create() => 1; void M() { Create(); } }");
        Assert.Contains("int unusedResult", code);
    }

    [Fact]
    public async Task Fix_All_uses_distinct_names_for_bound_underscore()
    {
        var code = await FixAsync("""
using SafeSharp;
class C
{
    [MustUse] int Create() => 1;
    void M(int _)
    {
        Create();
        Create();
    }
}
""", fixAll: true);
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var variables = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax>().Select(v => v.Identifier.ValueText).ToArray();
        Assert.Equal(2, variables.Length);
        Assert.Equal(2, variables.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Fix_All_acknowledges_all_unbound_underscore_results()
    {
        var code = await FixAsync("""
using SafeSharp;
class C
{
    [MustUse] int Create() => 1;
    void M()
    {
        Create();
        Create();
    }
}
""", fixAll: true);
        Assert.Equal(2, code.Split("_ = Create();", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Fix_provider_exposes_only_safe_supported_diagnostic()
    {
        var provider = new DiscardMustUseResultCodeFixProvider();
        Assert.Equal(new[] { "SS0024" }, provider.FixableDiagnosticIds);
        Assert.NotNull(provider.GetFixAllProvider());
    }

    private static async Task<string> FixAsync(string source, LanguageVersion version = LanguageVersion.Preview, bool fixAll = false)
    {
        using var workspace = new AdhocWorkspace();
        var references = AnalyzerTestHost.CreateCompilation(source).References;
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "CodeFixFixture", "CodeFixFixture", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: version >= LanguageVersion.CSharp8 ? NullableContextOptions.Enable : NullableContextOptions.Disable),
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(version), metadataReferences: references));
        var document = workspace.AddDocument(project.Id, "Code.cs", SourceText.From(source));
        var compilation = (await document.Project.GetCompilationAsync())!;
        AssertNoCompilerErrors(compilation);
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new SafeSharpSemanticAnalyzer())).GetAnalyzerDiagnosticsAsync();
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, diagnostic => Assert.Equal("SS0024", diagnostic.Id));
        var provider = new DiscardMustUseResultCodeFixProvider();
        CodeAction action;
        if (fixAll)
        {
            var context = new FixAllContext(document, provider, FixAllScope.Document, "SafeSharp.AcknowledgeUnusedResult", provider.FixableDiagnosticIds,
                new FixedDiagnostics(document.Id, diagnostics), CancellationToken.None);
            action = (await provider.GetFixAllProvider().GetFixAsync(context))!;
            Assert.NotNull(action);
        }
        else
        {
            var actions = new List<CodeAction>();
            var context = new CodeFixContext(document, Assert.Single(diagnostics), (registered, _) => actions.Add(registered), CancellationToken.None);
            await provider.RegisterCodeFixesAsync(context);
            action = Assert.Single(actions);
        }
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var changed = Assert.Single(operations.OfType<ApplyChangesOperation>()).ChangedSolution.GetDocument(document.Id)!;
        var changedCompilation = (await changed.Project.GetCompilationAsync())!;
        AssertNoCompilerErrors(changedCompilation);
        var remaining = await changedCompilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new SafeSharpSemanticAnalyzer())).GetAnalyzerDiagnosticsAsync();
        Assert.Empty(remaining);
        return (await changed.GetTextAsync()).ToString();
    }

    private static void AssertNoCompilerErrors(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(d => d.ToString())));
    }

    private sealed class FixedDiagnostics : FixAllContext.DiagnosticProvider
    {
        private readonly DocumentId documentId;
        private readonly ImmutableArray<Diagnostic> diagnostics;
        public FixedDiagnostics(DocumentId documentId, ImmutableArray<Diagnostic> diagnostics) { this.documentId = documentId; this.diagnostics = diagnostics; }
        public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(document.Id == documentId ? diagnostics : ImmutableArray<Diagnostic>.Empty);
        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(ImmutableArray<Diagnostic>.Empty);
        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);
    }
}
