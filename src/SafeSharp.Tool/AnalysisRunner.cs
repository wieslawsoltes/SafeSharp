using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using SafeSharp.Analyzers;
using SafeSharp.Analyzers.Semantics;
using SafeSharp.Core;
using SafeSharp.Reporting;
using AnalysisResult = SafeSharp.Core.AnalysisResult;
using ReportDiagnostic = SafeSharp.Reporting.ReportDiagnostic;

namespace SafeSharp.Tool;

internal static class AnalysisRunner
{
    private static readonly ImmutableArray<DiagnosticAnalyzer> Analyzers = SafeSharpAnalyzerCatalog.Create();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<AnalysisReport> RunAsync(ToolOptions options, string version, TextWriter log,
        CancellationToken cancellationToken)
    {
        var results = new List<AnalysisResult>();
        var diagnostics = new List<ReportDiagnostic>();
        bool analysisComplete;
        if (options.InputKind == InputKind.Source)
        {
            var compilation = await CreateSourceCompilationAsync(options, cancellationToken).ConfigureAwait(false);
            analysisComplete = await AnalyzeCompilationAsync(compilation, new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
                results, diagnostics, options.Format is ReportFormat.Json or ReportFormat.Html, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (options.Restore) await RestoreAsync(options, log, cancellationToken).ConfigureAwait(false);
            analysisComplete = await AnalyzeWorkspaceAsync(options, results, diagnostics, log, cancellationToken).ConfigureAwait(false);
        }
        return new AnalysisReport(results, diagnostics, "SafeSharp ownership analysis", Directory.GetCurrentDirectory(), version,
            analysisComplete: analysisComplete);
    }

    private static async Task<CSharpCompilation> CreateSourceCompilationAsync(ToolOptions options,
        CancellationToken cancellationToken)
    {
        var references = FrameworkReferences.Resolve()
            .Concat(options.References)
            .Append(typeof(Ownership).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var trees = new List<SyntaxTree>();
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)
            .WithPreprocessorSymbols(options.Defines);
        foreach (var source in options.Inputs.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = SourceText.From(await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false));
            trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, source, cancellationToken: cancellationToken));
        }
        var hasTopLevelStatements = trees.Any(tree => tree.GetRoot(cancellationToken)
            .ChildNodes().OfType<GlobalStatementSyntax>().Any());
        return CSharpCompilation.Create("SafeSharp.SourceAnalysis", trees, references,
            new CSharpCompilationOptions(hasTopLevelStatements ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static async Task<bool> AnalyzeWorkspaceAsync(ToolOptions options, List<AnalysisResult> results,
        List<ReportDiagnostic> diagnostics, TextWriter log, CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, string> { ["Configuration"] = options.Configuration };
        if (options.Framework is not null) properties["TargetFramework"] = options.Framework;
        using var workspace = MSBuildWorkspace.Create(properties);
        var failures = new ConcurrentQueue<WorkspaceDiagnostic>();
        using var failureHandler = workspace.RegisterWorkspaceFailedHandler(args => failures.Enqueue(args.Diagnostic));
        IEnumerable<Project> projects;
        try
        {
            if (options.InputKind == InputKind.Project)
            {
                var project = await workspace.OpenProjectAsync(options.Inputs[0], cancellationToken: cancellationToken).ConfigureAwait(false);
                projects = [project];
            }
            else
            {
                var solution = await workspace.OpenSolutionAsync(options.Inputs[0], cancellationToken: cancellationToken).ConfigureAwait(false);
                projects = solution.Projects.Where(project => project.Language == LanguageNames.CSharp);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ToolException("Unable to load the MSBuild workspace: " + exception.Message, exception);
        }
        ThrowWorkspaceFailures(workspace, failures, log);
        var ordered = projects.OrderBy(project => project.FilePath, StringComparer.Ordinal)
            .ThenBy(project => project.Name, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0) throw new ToolException("The requested input contains no C# projects to analyze.");
        var analysisComplete = true;
        foreach (var project in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (project.Language != LanguageNames.CSharp)
                throw new ToolException("The selected project is not a C# project: " + project.FilePath);
            log.WriteLine("SafeSharp: analyzing " + project.Name + "...");
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ToolException("The workspace returned no compilation for " + project.Name);
            ThrowWorkspaceFailures(workspace, failures, log);
            analysisComplete &= await AnalyzeCompilationAsync(compilation, project.AnalyzerOptions, results, diagnostics,
                options.Format is ReportFormat.Json or ReportFormat.Html, cancellationToken).ConfigureAwait(false);
        }
        ThrowWorkspaceFailures(workspace, failures, log);
        return analysisComplete;
    }

    private static async Task<bool> AnalyzeCompilationAsync(Compilation compilation, AnalyzerOptions analyzerOptions,
        List<AnalysisResult> results, List<ReportDiagnostic> diagnostics, bool captureSnapshots, CancellationToken cancellationToken)
    {
        var errors = compilation.GetDiagnostics(cancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
            throw new ToolException("Compilation failed; an ownership report was not generated for invalid source.\n" +
                string.Join("\n", errors.Take(50).Select(diagnostic => diagnostic.ToString())) +
                (errors.Length > 50 ? $"\n...and {errors.Length - 50} more errors." : string.Empty));
        var analyzerFailures = new ConcurrentQueue<Exception>();
        // The callback makes host failures observable in addition to AD0001 diagnostics.
        var analyzerExecution = new CompilationWithAnalyzersOptions(analyzerOptions,
            (exception, _, _) => analyzerFailures.Enqueue(exception), concurrentAnalysis: true,
            logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true);
        // Diagnostic suppression is a presentation setting, not evidence that an analysis
        // completed. Keep budget markers enabled during the single analyzer execution and
        // restore the user's configuration separately when producing the visible report.
        var executionCompilation = CompletenessDiagnostics.EnableBudgetDiagnostics(compilation);
        var analyzerDiagnostics = await executionCompilation.WithAnalyzers(Analyzers, analyzerExecution)
            .GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        if (!analyzerFailures.IsEmpty || analyzerDiagnostics.Any(diagnostic => diagnostic.Id is "AD0001" or "AD0002"))
            throw new ToolException("An analyzer failed; analysis is incomplete. " +
                string.Join("\n", analyzerFailures.Select(exception => exception.Message)
                    .Concat(analyzerDiagnostics.Where(diagnostic => diagnostic.Id is "AD0001" or "AD0002").Select(diagnostic => diagnostic.ToString()))));
        var captured = OwnershipCompilationAnalysis.Analyze(compilation, new AnalysisOptions(captureSnapshots: captureSnapshots),
            cancellationToken, configOptions: analyzerOptions.AnalyzerConfigOptionsProvider).ToArray();
        results.AddRange(captured);
        var engineIssues = captured.SelectMany(result => result.Issues.Select(issue => (Result: result, Issue: issue)))
            .GroupBy(item => DiagnosticKey(item.Issue.Id, item.Issue.Site, item.Issue.ResourceId)).ToDictionary(group => group.Key, group => group.First());
        foreach (var diagnostic in CompletenessDiagnostics.GetEffectiveDiagnostics(analyzerDiagnostics,
                     compilation, analyzerOptions, cancellationToken)
                     .Where(diagnostic => !diagnostic.IsSuppressed && diagnostic.Severity != DiagnosticSeverity.Hidden))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var site = GetSite(diagnostic.Location);
            var related = diagnostic.AdditionalLocations.Select(GetSite).ToArray();
            var severity = diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => ReportSeverity.Error,
                DiagnosticSeverity.Warning => ReportSeverity.Warning,
                _ => ReportSeverity.Note,
            };
            diagnostic.Properties.TryGetValue("SafeSharp.ResourceId", out var resourceId);
            if (engineIssues.TryGetValue(DiagnosticKey(diagnostic.Id, site, resourceId), out var engine))
            {
                diagnostics.Add(new ReportDiagnostic(diagnostic.Id, diagnostic.GetMessage(), site, severity,
                    diagnostic.Descriptor.Title.ToString(), engine.Result.Graph.Name, engine.Issue.ResourceId,
                    engine.Issue.Certainty, related, engine.Issue.Evidence, diagnostic.Properties));
            }
            else
            {
                diagnostic.Properties.TryGetValue("SafeSharp.Certainty", out var declaredCertainty);
                IssueCertainty? certainty = declaredCertainty switch
                {
                    "Definite" => IssueCertainty.Definite,
                    "Possible" => IssueCertainty.Possible,
                    _ => null,
                };
                diagnostics.Add(new ReportDiagnostic(diagnostic.Id, diagnostic.GetMessage(), site, severity,
                    diagnostic.Descriptor.Title.ToString(), certainty: certainty, relatedLocations: related, properties: diagnostic.Properties));
            }
        }
        return !analyzerDiagnostics.Any(diagnostic => CompletenessDiagnostics.IsBudgetDiagnostic(diagnostic.Id));
    }

    private static string DiagnosticKey(string id, SourceSite site, string? resourceId) =>
        id + "\0" + site.Path + "\0" + site.Start + ":" + site.Length + "\0" + resourceId;

    private static SourceSite GetSite(Location location)
    {
        if (!location.IsInSource) return new SourceSite(string.Empty, 0, 0);
        var line = location.GetLineSpan();
        return new SourceSite(line.Path, location.SourceSpan.Start, location.SourceSpan.Length,
            line.StartLinePosition.Line, line.StartLinePosition.Character);
    }

    private static void ThrowWorkspaceFailures(MSBuildWorkspace workspace, ConcurrentQueue<WorkspaceDiagnostic> diagnostics, TextWriter log)
    {
        var errors = new List<string>();
        while (diagnostics.TryDequeue(out var diagnostic))
        {
            if (diagnostic.Kind == WorkspaceDiagnosticKind.Failure) errors.Add(diagnostic.Message);
            else log.WriteLine("SafeSharp workspace: " + diagnostic.Message);
        }
        // The workspace retains loader diagnostics even when an event is dispatched asynchronously.
        // Consult that authoritative collection before allowing a successful result.
        errors.AddRange(workspace.Diagnostics.Where(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            .Select(diagnostic => diagnostic.Message));
        if (errors.Count != 0)
            throw new ToolException("MSBuild workspace loading failed. Verify the installed SDK, project references, and package restore.\n" + string.Join("\n", errors.Distinct(StringComparer.Ordinal)));
    }

    private static async Task RestoreAsync(ToolOptions options, TextWriter log, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(FindDotNetHost())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(options.Inputs[0])!,
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(options.Inputs[0]);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("minimal");
        startInfo.ArgumentList.Add("/p:Configuration=" + options.Configuration);
        if (options.Framework is not null) startInfo.ArgumentList.Add("/p:TargetFramework=" + options.Framework);
        using var process = Process.Start(startInfo) ?? throw new ToolException("Unable to start dotnet restore.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        if (output.Length > 0) log.Write(output);
        if (error.Length > 0) log.Write(error);
        if (process.ExitCode != 0)
            throw new ToolException($"dotnet restore failed with exit code {process.ExitCode}. Ownership analysis was not started.");
    }

    private static string FindDotNetHost()
    {
        var explicitPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var runtimeRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var runtimeHost = Path.Combine(runtimeRoot, executableName);
        return File.Exists(runtimeHost) ? runtimeHost : executableName;
    }
}
