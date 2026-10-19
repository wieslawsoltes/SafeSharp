using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using SafeSharp.Core;

namespace SafeSharp.Reporting;

/// <summary>The presentation severity of a diagnostic, independent of a compiler host.</summary>
public enum ReportSeverity
{
    Note,
    Warning,
    Error,
}

/// <summary>A compiler-independent diagnostic with precise source coordinates and evidence.</summary>
public sealed class ReportDiagnostic
{
    public ReportDiagnostic(
        string id,
        string message,
        SourceSite site,
        ReportSeverity severity = ReportSeverity.Warning,
        string? title = null,
        string? method = null,
        string? resourceId = null,
        IssueCertainty? certainty = null,
        IEnumerable<SourceSite>? relatedLocations = null,
        IEnumerable<AnalysisEvidence>? evidence = null,
        IEnumerable<KeyValuePair<string, string?>>? properties = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Site = site ?? throw new ArgumentNullException(nameof(site));
        Severity = severity;
        Title = title ?? id;
        Method = method;
        ResourceId = resourceId;
        Certainty = certainty;
        RelatedLocations = relatedLocations?.ToImmutableArray() ?? ImmutableArray<SourceSite>.Empty;
        Evidence = evidence?.ToImmutableArray() ?? ImmutableArray<AnalysisEvidence>.Empty;
        Properties = properties?.ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            ?? ImmutableDictionary<string, string?>.Empty;
    }

    public string Id { get; }
    public string Title { get; }
    public string Message { get; }
    public SourceSite Site { get; }
    public ReportSeverity Severity { get; }
    public string? Method { get; }
    public string? ResourceId { get; }
    public IssueCertainty? Certainty { get; }
    public ImmutableArray<SourceSite> RelatedLocations { get; }
    public ImmutableArray<AnalysisEvidence> Evidence { get; }
    public ImmutableDictionary<string, string?> Properties { get; }

    /// <summary>Preserves an engine issue, including its actual abstract-analysis provenance.</summary>
    public static ReportDiagnostic FromIssue(AnalysisIssue issue, string? method = null)
    {
        if (issue is null) throw new ArgumentNullException(nameof(issue));
        return new ReportDiagnostic(issue.Id, issue.Message, issue.Site, method: method,
            resourceId: issue.ResourceId, certainty: issue.Certainty,
            relatedLocations: issue.RelatedLocations, evidence: issue.Evidence);
    }
}

/// <summary>
/// An immutable report. When supplied, diagnostics are the caller's authoritative list, allowing a
/// compiler host to preserve configured severity, suppressions, and supplementary semantic rules.
/// Raw engine issues and all computed states remain available in <see cref="Results"/>.
/// </summary>
public sealed class AnalysisReport
{
    public const string SchemaVersion = "1.0";

    public AnalysisReport(
        IEnumerable<AnalysisResult> results,
        IEnumerable<ReportDiagnostic>? diagnostics = null,
        string title = "SafeSharp ownership analysis",
        string? sourceRoot = null,
        string? toolVersion = null,
        bool analysisComplete = true)
    {
        if (results is null) throw new ArgumentNullException(nameof(results));
        var materializedResults = results.ToImmutableArray();
        if (materializedResults.Any(result => result is null))
            throw new ArgumentException("Results cannot contain null.", nameof(results));
        Results = materializedResults.OrderBy(result => result.Graph.Site.Path, StringComparer.Ordinal)
            .ThenBy(result => result.Graph.Site.Start)
            .ThenBy(result => result.Graph.Name, StringComparer.Ordinal).ToImmutableArray();
        var materializedDiagnostics = (diagnostics ?? Results.SelectMany(result =>
                result.Issues.Select(issue => ReportDiagnostic.FromIssue(issue, result.Graph.Name)))).ToImmutableArray();
        if (materializedDiagnostics.Any(diagnostic => diagnostic is null))
            throw new ArgumentException("Diagnostics cannot contain null.", nameof(diagnostics));
        Diagnostics = materializedDiagnostics
            .OrderBy(diagnostic => diagnostic.Site.Path, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Site.Start)
            .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Severity)
            .ToImmutableArray();
        Title = title ?? throw new ArgumentNullException(nameof(title));
        if (sourceRoot is not null && (!Uri.TryCreate(ReportText.SourceUri(sourceRoot), UriKind.Absolute, out var rootUri) || !rootUri.IsFile))
            throw new ArgumentException("The source root must be an absolute filesystem path or file URI.", nameof(sourceRoot));
        SourceRoot = sourceRoot;
        ToolVersion = toolVersion ?? typeof(AnalysisReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "unknown";
        AnalysisComplete = analysisComplete && Results.All(result => !result.BudgetExceeded);
    }

    public ImmutableArray<AnalysisResult> Results { get; }
    public ImmutableArray<ReportDiagnostic> Diagnostics { get; }
    public string Title { get; }
    public string? SourceRoot { get; }
    public string ToolVersion { get; }
    public bool AnalysisComplete { get; }
}
