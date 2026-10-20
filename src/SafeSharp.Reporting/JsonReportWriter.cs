using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SafeSharp.Core;

namespace SafeSharp.Reporting;

/// <summary>Writes the deterministic, versioned report interchange format, including actual states.</summary>
public sealed class JsonReportWriter : IAnalysisReportWriter
{
    public void Write(AnalysisReport report, TextWriter writer, CancellationToken cancellationToken = default)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        cancellationToken.ThrowIfCancellationRequested();
        var json = new JsonSink(writer);
        json.StartObject();
        json.Property("schemaVersion", AnalysisReport.SchemaVersion);
        json.Property("tool", "SafeSharp");
        json.Property("toolVersion", report.ToolVersion);
        json.Property("title", report.Title);
        json.Property("sourceRoot", report.SourceRoot);
        json.Property("columnKind", "utf16CodeUnits");
        json.Property("coordinateBase", 0);
        json.Property("analysisComplete", report.AnalysisComplete);
        json.Name("diagnostics");
        json.StartArray();
        foreach (var diagnostic in report.Diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteDiagnostic(json, diagnostic);
        }
        json.EndArray();
        json.Name("methods");
        json.StartArray();
        foreach (var result in report.Results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteResult(json, result, cancellationToken);
        }
        json.EndArray();
        json.EndObject();
        writer.Write('\n');
    }

    private static void WriteResult(JsonSink json, AnalysisResult result, CancellationToken cancellationToken)
    {
        json.StartObject();
        json.Property("name", result.Graph.Name);
        json.Name("source"); WriteSite(json, result.Graph.Site);
        json.Property("entryBlock", result.Graph.EntryId);
        json.Property("iterations", result.Iterations);
        json.Property("budgetExceeded", result.BudgetExceeded);
        json.Name("issues"); json.StartArray();
        foreach (var issue in result.Issues.OrderBy(issue => issue.Site.Path, StringComparer.Ordinal)
                     .ThenBy(issue => issue.Site.Start).ThenBy(issue => issue.Id, StringComparer.Ordinal)
                     .ThenBy(issue => issue.Message, StringComparer.Ordinal))
            WriteDiagnostic(json, ReportDiagnostic.FromIssue(issue, result.Graph.Name));
        json.EndArray();
        json.Name("blocks"); json.StartArray();
        var snapshots = result.Blocks.ToDictionary(block => block.Id);
        foreach (var block in result.Graph.Blocks.OrderBy(block => block.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            json.StartObject();
            json.Property("id", block.Id);
            json.Name("successors"); json.StartArray();
            foreach (var successor in block.Successors.Distinct().OrderBy(id => id)) json.Number(successor);
            json.EndArray();
            json.Name("instructions"); json.StartArray();
            foreach (var instruction in block.Instructions) WriteInstruction(json, instruction);
            json.EndArray();
            json.Property("hasSnapshots", snapshots.ContainsKey(block.Id));
            if (snapshots.TryGetValue(block.Id, out var snapshot))
            {
                json.Name("liveIn"); WriteStrings(json, snapshot.LiveIn);
                json.Name("liveOut"); WriteStrings(json, snapshot.LiveOut);
                json.Name("entryState"); WriteState(json, snapshot.Entry);
                json.Name("exitState"); WriteState(json, snapshot.Exit);
                json.Name("steps"); json.StartArray();
                foreach (var step in snapshot.Steps.OrderBy(step => step.Index))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    json.StartObject();
                    json.Property("index", step.Index);
                    json.Name("instruction"); WriteInstruction(json, step.Instruction);
                    json.Name("liveBefore"); WriteStrings(json, step.LiveBefore);
                    json.Name("liveAfter"); WriteStrings(json, step.LiveAfter);
                    json.Name("before"); WriteState(json, step.Before);
                    json.Name("after"); WriteState(json, step.After);
                    json.EndObject();
                }
                json.EndArray();
            }
            json.EndObject();
        }
        json.EndArray();
        json.EndObject();
    }

    internal static void WriteDiagnostic(JsonSink json, ReportDiagnostic diagnostic)
    {
        json.StartObject();
        json.Property("id", diagnostic.Id);
        json.Property("title", diagnostic.Title);
        json.Property("message", diagnostic.Message);
        json.Property("severity", diagnostic.Severity.ToString().ToLowerInvariant());
        json.Property("certainty", diagnostic.Certainty?.ToString());
        json.Property("method", diagnostic.Method);
        json.Property("resourceId", diagnostic.ResourceId);
        json.Name("properties"); json.StartObject();
        foreach (var property in diagnostic.Properties.OrderBy(property => property.Key, StringComparer.Ordinal))
            json.Property(property.Key, property.Value);
        json.EndObject();
        json.Name("source"); WriteSite(json, diagnostic.Site);
        json.Name("relatedLocations"); json.StartArray();
        foreach (var site in diagnostic.RelatedLocations) WriteSite(json, site);
        json.EndArray();
        json.Name("evidence"); json.StartArray();
        foreach (var evidence in diagnostic.Evidence)
        {
            json.StartObject();
            json.Property("kind", evidence.Kind);
            json.Property("message", evidence.Message);
            json.Name("source"); WriteSite(json, evidence.Site);
            json.EndObject();
        }
        json.EndArray();
        json.EndObject();
    }

    private static void WriteInstruction(JsonSink json, FlowInstruction instruction)
    {
        json.StartObject();
        json.Property("kind", instruction.Kind.ToString());
        json.Property("target", instruction.Target);
        json.Property("sourcePlace", instruction.Source);
        json.Property("resourceId", instruction.ResourceId);
        json.Property("owner", instruction.Owner);
        json.Property("ownership", instruction.Ownership.ToString());
        json.Property("flags", instruction.Flags.ToString());
        json.Property("description", instruction.Description);
        json.Property("asyncResultDepth", instruction.AsyncResultDepth);
        json.Property("asyncComposition", instruction.AsyncComposition.ToString());
        json.Property("projection", instruction.Projection);
        json.Name("source"); WriteSite(json, instruction.Site);
        json.EndObject();
    }

    private static void WriteState(JsonSink json, StateSnapshot state)
    {
        json.StartObject();
        json.Property("isReachable", state.IsReachable);
        json.Name("places"); json.StartArray();
        foreach (var place in state.Places.OrderBy(place => place.Place, StringComparer.Ordinal))
        {
            json.StartObject();
            json.Property("place", place.Place);
            json.Property("mayBeNull", place.MayBeNull);
            json.Name("references"); json.StartArray();
            foreach (var reference in place.References.OrderBy(reference => reference.ResourceId, StringComparer.Ordinal)
                         .ThenBy(reference => reference.Projection, StringComparer.Ordinal)
                         .ThenBy(reference => reference.LoanId, StringComparer.Ordinal))
            {
                json.StartObject();
                json.Property("resourceId", reference.ResourceId);
                json.Property("availability", reference.Availability.ToString());
                json.Property("loanId", reference.LoanId);
                json.Property("projection", reference.Projection);
                json.Property("mayBeAbsent", reference.MayBeAbsent);
                json.Property("coversResource", reference.CoversResource);
                json.Name("movedProjections"); WriteStrings(json, reference.MovedProjections);
                json.EndObject();
            }
            json.EndArray();
            json.EndObject();
        }
        json.EndArray();
        json.Name("referenceStorage"); json.StartArray();
        foreach (var binding in state.ReferenceStorage)
        {
            json.StartObject();
            json.Property("reference", binding.Reference);
            json.Name("targets"); WriteStrings(json, binding.Targets);
            json.EndObject();
        }
        json.EndArray();
        json.Name("resources"); json.StartArray();
        foreach (var resource in state.Resources.OrderBy(resource => resource.Id, StringComparer.Ordinal))
        {
            json.StartObject();
            json.Property("id", resource.Id);
            json.Property("kind", resource.Kind.ToString());
            json.Property("ownership", resource.Ownership.ToString());
            json.Property("status", resource.Status.ToString());
            json.Name("origin"); WriteSite(json, resource.Origin);
            json.Name("ownerResourceIds"); WriteStrings(json, resource.OwnerResourceIds);
            json.Name("disposedResourceIds"); WriteStrings(json, resource.DisposedResourceIds);
            if (resource.AsyncComposition != AsyncCompositionKind.None)
            {
                json.Property("asyncComposition", resource.AsyncComposition.ToString());
                json.Name("dependencyResourceIds"); WriteStrings(json, resource.DependencyResourceIds);
            }
            if (resource.Kind == ResourceKind.Aggregate)
            {
                json.Property("summarizesMultipleAggregates", resource.SummarizesMultipleAggregates);
                json.Name("elements"); json.StartArray();
                foreach (var element in resource.Elements)
                {
                    json.StartObject(); json.Property("index", element.Index);
                    json.Name("resourceIds"); WriteStrings(json, element.ResourceIds);
                    json.Name("valueTaskIds"); WriteStrings(json, element.ValueTaskIds);
                    json.EndObject();
                }
                json.EndArray();
            }
            if (resource.Kind == ResourceKind.AsyncFactoryCarrier || resource.ResultState != AsyncResultState.None)
            {
                json.Property("resultState", resource.ResultState.ToString());
                json.Property("asyncResultDepth", resource.AsyncResultDepth);
                json.Property("producedResourceId", resource.ProducedResourceId);
                json.Name("producedResourceIds"); WriteStrings(json, resource.ProducedResourceIds);
            }
            json.EndObject();
        }
        json.EndArray();
        json.Name("loans"); json.StartArray();
        foreach (var loan in state.Loans.OrderBy(loan => loan.Id, StringComparer.Ordinal))
        {
            json.StartObject();
            json.Property("id", loan.Id);
            json.Property("resourceId", loan.ResourceId);
            json.Property("holder", loan.Holder);
            json.Property("kind", loan.Kind.ToString());
            json.Property("phase", loan.Phase.ToString());
            json.Property("parentId", loan.ParentId);
            json.Property("projection", loan.Projection);
            json.Property("allowsSuspension", loan.AllowsSuspension);
            json.Property("mayBeAbsent", loan.MayBeAbsent);
            json.Name("origin"); WriteSite(json, loan.Origin);
            json.Name("activationSites"); json.StartArray();
            foreach (var site in loan.ActivationSites) WriteSite(json, site);
            json.EndArray();
            json.EndObject();
        }
        json.EndArray();
        json.Name("valueTasks"); json.StartArray();
        foreach (var valueTask in state.ValueTasks)
        {
            json.StartObject();
            json.Property("id", valueTask.Id);
            json.Property("state", valueTask.State.ToString());
            json.Name("origin"); WriteSite(json, valueTask.Origin);
            json.Name("places"); WriteStrings(json, valueTask.Places);
            json.Name("evidenceSites"); json.StartArray();
            foreach (var site in valueTask.EvidenceSites) WriteSite(json, site);
            json.EndArray();
            json.EndObject();
        }
        json.EndArray();
        json.EndObject();
    }

    internal static void WriteSite(JsonSink json, SourceSite site)
    {
        json.StartObject();
        json.Property("path", site.Path);
        json.Property("uri", ReportText.SourceUri(site.Path));
        json.Property("start", site.Start);
        json.Property("length", site.Length);
        json.Property("line", site.Line);
        json.Property("column", site.Column);
        json.EndObject();
    }

    private static void WriteStrings(JsonSink json, IEnumerable<string> values)
    {
        json.StartArray();
        foreach (var value in values.OrderBy(value => value, StringComparer.Ordinal)) json.String(value);
        json.EndArray();
    }
}
