using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SafeSharp.Core;

namespace SafeSharp.Reporting;

/// <summary>
/// SARIF 2.1.0 emission with UTF-16 coordinates. Evidence is explicitly identified as abstract
/// provenance: a join-state explanation is not represented as a proven executable witness.
/// </summary>
public sealed class SarifReportWriter : IAnalysisReportWriter
{
    public void Write(AnalysisReport report, TextWriter writer, CancellationToken cancellationToken = default)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        cancellationToken.ThrowIfCancellationRequested();
        var rules = report.Diagnostics.GroupBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        var indices = rules.Select((rule, index) => new { rule.Key, Index = index })
            .ToDictionary(rule => rule.Key, rule => rule.Index, StringComparer.Ordinal);
        var json = new JsonSink(writer);
        json.StartObject();
        json.Property("$schema", "https://docs.oasis-open.org/sarif/sarif/v2.1.0/os/schemas/sarif-schema-2.1.0.json");
        json.Property("version", "2.1.0");
        json.Name("runs"); json.StartArray(); json.StartObject();
        json.Name("tool"); json.StartObject();
        json.Name("driver"); json.StartObject();
        json.Property("name", "SafeSharp");
        json.Property("version", report.ToolVersion);
        json.Property("informationUri", "https://github.com/wieslawsoltes/SafeSharp");
        json.Name("rules"); json.StartArray();
        foreach (var rule in rules)
        {
            var diagnostic = rule.First();
            json.StartObject();
            json.Property("id", diagnostic.Id);
            json.Property("name", diagnostic.Id);
            json.Name("shortDescription"); WriteMessage(json, diagnostic.Title);
            json.Property("helpUri", "https://github.com/wieslawsoltes/SafeSharp/blob/main/docs/diagnostics.md#" +
                Uri.EscapeDataString(diagnostic.Id.ToLowerInvariant()));
            json.Name("defaultConfiguration"); json.StartObject();
            json.Property("level", "warning"); json.EndObject();
            json.Name("properties"); json.StartObject();
            json.Name("tags"); json.StartArray(); json.String("correctness"); json.String("ownership"); json.EndArray();
            json.EndObject();
            json.EndObject();
        }
        json.EndArray(); json.EndObject(); json.EndObject();
        json.Property("columnKind", "utf16CodeUnits");
        if (!string.IsNullOrEmpty(report.SourceRoot))
        {
            json.Name("originalUriBaseIds"); json.StartObject();
            json.Name("%SRCROOT%"); json.StartObject();
            json.Property("uri", ReportText.SourceUri(report.SourceRoot!.TrimEnd('/', '\\') + "/"));
            json.EndObject(); json.EndObject();
        }
        json.Name("invocations"); json.StartArray(); json.StartObject();
        json.Property("executionSuccessful", report.AnalysisComplete);
        json.EndObject(); json.EndArray();
        json.Name("results"); json.StartArray();
        foreach (var diagnostic in report.Diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteResult(json, diagnostic, indices[diagnostic.Id], report.SourceRoot);
        }
        json.EndArray();
        json.Name("properties"); json.StartObject();
        json.Property("reportSchemaVersion", AnalysisReport.SchemaVersion);
        json.Property("analysisComplete", report.AnalysisComplete);
        json.Property("methodsAnalyzed", report.Results.Length);
        json.Property("evidenceSemantics", "abstractProvenance");
        json.EndObject();
        json.EndObject(); json.EndArray(); json.EndObject();
        writer.Write('\n');
    }

    private static void WriteResult(JsonSink json, ReportDiagnostic diagnostic, int ruleIndex, string? sourceRoot)
    {
        json.StartObject();
        json.Property("ruleId", diagnostic.Id);
        json.Property("ruleIndex", ruleIndex);
        json.Property("level", diagnostic.Severity.ToString().ToLowerInvariant());
        json.Name("message"); WriteMessage(json, diagnostic.Message);
        json.Name("locations"); json.StartArray();
        WriteLocation(json, diagnostic.Site, diagnostic.Method, null, sourceRoot);
        json.EndArray();
        if (!diagnostic.RelatedLocations.IsEmpty)
        {
            json.Name("relatedLocations"); json.StartArray();
            for (var index = 0; index < diagnostic.RelatedLocations.Length; index++)
            {
                var site = diagnostic.RelatedLocations[index];
                var message = diagnostic.Evidence.FirstOrDefault(evidence => evidence.Site.Equals(site))?.Message
                    ?? "Related ownership event";
                WriteLocation(json, site, diagnostic.Method, message, sourceRoot, index + 1);
            }
            json.EndArray();
        }
        if (!diagnostic.Evidence.IsEmpty)
        {
            json.Name("codeFlows"); json.StartArray(); json.StartObject();
            json.Name("message"); WriteMessage(json,
                "Abstract ownership provenance. Joined states can combine paths; these events are not a proven execution trace.");
            json.Name("threadFlows"); json.StartArray(); json.StartObject();
            json.Name("locations"); json.StartArray();
            foreach (var evidence in diagnostic.Evidence)
            {
                json.StartObject();
                json.Name("location"); WriteLocation(json, evidence.Site, diagnostic.Method, evidence.Message, sourceRoot);
                json.Name("kinds"); json.StartArray(); json.String(evidence.Kind); json.EndArray();
                json.Name("properties"); json.StartObject();
                json.Property("evidenceSemantics", "abstractProvenance");
                json.EndObject(); json.EndObject();
            }
            json.EndArray(); json.EndObject(); json.EndArray(); json.EndObject(); json.EndArray();
        }
        json.Name("partialFingerprints"); json.StartObject();
        json.Property("safesharp/v1", Fingerprint(diagnostic));
        json.EndObject();
        json.Name("properties"); json.StartObject();
        json.Property("certainty", diagnostic.Certainty?.ToString());
        json.Property("resourceId", diagnostic.ResourceId);
        json.Property("rawSourcePath", diagnostic.Site.Path);
        json.Name("diagnosticProperties"); json.StartObject();
        foreach (var property in diagnostic.Properties.OrderBy(property => property.Key, StringComparer.Ordinal))
            json.Property(property.Key, property.Value);
        json.EndObject();
        json.EndObject();
        json.EndObject();
    }

    private static void WriteLocation(JsonSink json, SourceSite site, string? method, string? message,
        string? sourceRoot, int? relatedId = null)
    {
        json.StartObject();
        if (relatedId.HasValue) json.Property("id", relatedId.Value);
        if (!string.IsNullOrEmpty(site.Path))
        {
            json.Name("physicalLocation"); json.StartObject();
            json.Name("artifactLocation"); json.StartObject();
            var uri = ReportText.SourceUri(site.Path);
            json.Property("uri", uri);
            if (sourceRoot is not null && !Uri.IsWellFormedUriString(uri, UriKind.Absolute))
                json.Property("uriBaseId", "%SRCROOT%");
            json.EndObject();
            json.Name("region"); json.StartObject();
            json.Property("startLine", site.Line + 1);
            json.Property("startColumn", site.Column + 1);
            // SourceSite stores exact UTF-16 offsets. No end line is guessed for multiline spans.
            json.Property("charOffset", site.Start);
            json.Property("charLength", site.Length);
            json.EndObject(); json.EndObject();
        }
        if (!string.IsNullOrEmpty(method))
        {
            json.Name("logicalLocations"); json.StartArray(); json.StartObject();
            json.Property("fullyQualifiedName", method);
            json.Property("kind", "function");
            json.EndObject(); json.EndArray();
        }
        if (message is not null) { json.Name("message"); WriteMessage(json, message); }
        if (string.IsNullOrEmpty(site.Path) && string.IsNullOrEmpty(method) && message is null)
        {
            json.Name("message"); WriteMessage(json, "Source location unavailable");
        }
        json.EndObject();
    }

    private static void WriteMessage(JsonSink json, string message)
    {
        json.StartObject(); json.Property("text", message); json.EndObject();
    }

    private static string Fingerprint(ReportDiagnostic diagnostic)
    {
        var value = diagnostic.Id + "\n" + ReportText.SourceUri(diagnostic.Site.Path) + "\n" +
            ReportText.Number(diagnostic.Site.Start) + ":" + ReportText.Number(diagnostic.Site.Length) + "\n" +
            diagnostic.ResourceId + "\n" + diagnostic.Message;
        using var algorithm = SHA256.Create();
        return string.Concat(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(@byte => @byte.ToString("x2")));
    }
}
