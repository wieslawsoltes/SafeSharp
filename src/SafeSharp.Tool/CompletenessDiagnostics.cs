using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using DiagnosticSetting = Microsoft.CodeAnalysis.ReportDiagnostic;

namespace SafeSharp.Tool;

/// <summary>
/// Separates evidence of an incomplete analysis from user-configured diagnostic visibility.
/// Only SafeSharp's budget rules are forced on; analyzer options and generated-code
/// classification are preserved. The analyzer catalog executes once per compilation.
/// </summary>
internal static class CompletenessDiagnostics
{
    private static readonly string[] BudgetIds = ["SS0015", "SS0029", "SS0043", "SS0046"];

    public static bool IsBudgetDiagnostic(string id) => id is "SS0015" or "SS0029" or "SS0043" or "SS0046";

    public static Compilation EnableBudgetDiagnostics(Compilation compilation)
    {
        var options = compilation.Options.WithSpecificDiagnosticOptions(
            compilation.Options.SpecificDiagnosticOptions.SetItems(
                BudgetIds.Select(id => new KeyValuePair<string, DiagnosticSetting>(
                    id, DiagnosticSetting.Warn))))
            .WithSyntaxTreeOptionsProvider(new ExecutionOptionsProvider(compilation.Options.SyntaxTreeOptionsProvider));

        // /warn:0 is applied before diagnostic-specific severity. Allow level-one
        // markers internally; the original warning level filters the visible report.
        if (options is CSharpCompilationOptions { WarningLevel: < 1 } csharp)
            options = csharp.WithWarningLevel(1);

        return compilation.WithOptions(options);
    }

    public static IEnumerable<Diagnostic> GetEffectiveDiagnostics(ImmutableArray<Diagnostic> diagnostics,
        Compilation originalCompilation, AnalyzerOptions analyzerOptions, CancellationToken cancellationToken)
    {
        var normalized = ImmutableArray.CreateBuilder<Diagnostic>(diagnostics.Length);
        var descriptors = new Dictionary<string, DiagnosticDescriptor>(StringComparer.Ordinal);
        var restoreWarningLevel = originalCompilation.Options is CSharpCompilationOptions { WarningLevel: < 1 };
        foreach (var diagnostic in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isBudget = IsBudgetDiagnostic(diagnostic.Id);
            if (!isBudget && !restoreWarningLevel)
            {
                normalized.Add(diagnostic);
                continue;
            }

            var descriptor = diagnostic.Descriptor;
            if (isBudget) descriptors.TryAdd(descriptor.Id, descriptor);
            // The execution severity is artificial. Start from the rule's default
            // before Roslyn reapplies the original options (SS0029 defaults to Info).
            // Under /warn:0 ordinary diagnostics also need resetting: a warning
            // promoted internally to Error now has warning level zero, so a second
            // filter would incorrectly retain it instead of restoring suppression.
            // This public factory retains the formatted message, source suppressions,
            // locations, tags, help link, and all diagnostic evidence properties.
            normalized.Add(Diagnostic.Create(descriptor.Id, descriptor.Category, diagnostic.GetMessage(),
                descriptor.DefaultSeverity, descriptor.DefaultSeverity, descriptor.IsEnabledByDefault,
                warningLevel: descriptor.DefaultSeverity == DiagnosticSeverity.Error ? 0 : 1,
                isSuppressed: diagnostic.IsSuppressed, title: descriptor.Title,
                description: descriptor.Description, helpLink: descriptor.HelpLinkUri,
                location: diagnostic.Location, additionalLocations: diagnostic.AdditionalLocations,
                customTags: descriptor.CustomTags, properties: diagnostic.Properties));
        }

        // GetEffectiveDiagnostics applies compiler options, pragmas, and
        // SuppressMessage attributes, but has no AnalyzerOptions parameter. Project
        // the original category/global analyzer severity for budget rules into its
        // syntax-tree options; all explicit per-ID settings retain precedence.
        // Roslyn's precedence is documented in AnalyzerOptionsExtensions:
        // https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/Portable/DiagnosticAnalyzer/AnalyzerOptionsExtensions.cs
        var presentation = originalCompilation.WithOptions(originalCompilation.Options.WithSyntaxTreeOptionsProvider(
            new PresentationOptionsProvider(originalCompilation.Options, analyzerOptions.AnalyzerConfigOptionsProvider,
                descriptors, cancellationToken)));
        foreach (var diagnostic in CompilationWithAnalyzers.GetEffectiveDiagnostics(normalized.ToImmutable(), presentation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return diagnostic;
        }
    }

    private sealed class ExecutionOptionsProvider(SyntaxTreeOptionsProvider? original) : SyntaxTreeOptionsProvider
    {
        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return original?.IsGenerated(tree, cancellationToken) ?? GeneratedKind.Unknown;
        }

        public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId,
            CancellationToken cancellationToken, out DiagnosticSetting severity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsBudgetDiagnostic(diagnosticId))
            {
                severity = DiagnosticSetting.Warn;
                return true;
            }
            severity = default;
            return original?.TryGetDiagnosticValue(tree, diagnosticId, cancellationToken, out severity) == true;
        }

        public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken,
            out DiagnosticSetting severity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsBudgetDiagnostic(diagnosticId))
            {
                severity = DiagnosticSetting.Warn;
                return true;
            }
            severity = default;
            return original?.TryGetGlobalDiagnosticValue(diagnosticId, cancellationToken, out severity) == true;
        }
    }

    private sealed class PresentationOptionsProvider(CompilationOptions originalOptions,
        AnalyzerConfigOptionsProvider configOptions, IReadOnlyDictionary<string, DiagnosticDescriptor> descriptors,
        CancellationToken analysisCancellation) : SyntaxTreeOptionsProvider
    {
        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken)
        {
            CheckCancellation(cancellationToken);
            return originalOptions.SyntaxTreeOptionsProvider?.IsGenerated(tree, analysisCancellation) ?? GeneratedKind.Unknown;
        }

        public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId,
            CancellationToken cancellationToken, out DiagnosticSetting severity)
        {
            CheckCancellation(cancellationToken);
            severity = default;
            var original = originalOptions.SyntaxTreeOptionsProvider;
            if (original?.TryGetDiagnosticValue(tree, diagnosticId, analysisCancellation, out severity) == true)
                return true;
            if (!descriptors.TryGetValue(diagnosticId, out var descriptor)
                || originalOptions.SpecificDiagnosticOptions.ContainsKey(diagnosticId)
                || original?.TryGetGlobalDiagnosticValue(diagnosticId, analysisCancellation, out _) == true)
                return false;

            var options = configOptions.GetOptions(tree);
            if (!TryGetSeverity(options, "dotnet_analyzer_diagnostic.category-" + descriptor.Category + ".severity", out severity)
                && !TryGetSeverity(options, "dotnet_analyzer_diagnostic.severity", out severity))
                return false;
            if (severity == DiagnosticSetting.Warn
                && originalOptions.GeneralDiagnosticOption == DiagnosticSetting.Error)
                severity = DiagnosticSetting.Error;
            return true;
        }

        public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken,
            out DiagnosticSetting severity)
        {
            CheckCancellation(cancellationToken);
            severity = default;
            return originalOptions.SyntaxTreeOptionsProvider?.TryGetGlobalDiagnosticValue(
                diagnosticId, analysisCancellation, out severity) == true;
        }

        private void CheckCancellation(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Roslyn's public effective-diagnostic API does not accept a token.
            analysisCancellation.ThrowIfCancellationRequested();
        }

        private static bool TryGetSeverity(AnalyzerConfigOptions options, string key,
            out DiagnosticSetting severity)
        {
            severity = default;
            if (!options.TryGetValue(key, out var value)) return false;
            switch (value.ToLowerInvariant())
            {
                case "default": severity = DiagnosticSetting.Default; return true;
                case "error": severity = DiagnosticSetting.Error; return true;
                case "warning": severity = DiagnosticSetting.Warn; return true;
                case "suggestion": severity = DiagnosticSetting.Info; return true;
                case "silent":
                case "refactoring": severity = DiagnosticSetting.Hidden; return true;
                case "none": severity = DiagnosticSetting.Suppress; return true;
                default: return false;
            }
        }
    }
}
