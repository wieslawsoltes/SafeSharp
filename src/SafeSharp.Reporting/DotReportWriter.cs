using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace SafeSharp.Reporting;

/// <summary>Emits control flow, dependency paths, and exact lifetime assumptions and requirements as Graphviz graphs.</summary>
public sealed class DotReportWriter : IAnalysisReportWriter
{
    public void Write(AnalysisReport report, TextWriter writer, CancellationToken cancellationToken = default)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        cancellationToken.ThrowIfCancellationRequested();
        writer.Write("digraph SafeSharp {\n  graph [rankdir=TB, compound=true, label=\"");
        writer.Write(ReportText.Dot(report.Title));
        writer.Write("\", labelloc=t];\n  node [shape=box, fontname=\"monospace\", fontsize=10];\n");
        for (var method = 0; method < report.Results.Length; method++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = report.Results[method];
            writer.Write("  subgraph cluster_m"); writer.Write(ReportText.Number(method));
            writer.Write(" {\n    label=\""); writer.Write(ReportText.Dot(result.Graph.Name)); writer.Write("\";\n");
            foreach (var block in result.Graph.Blocks.OrderBy(block => block.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write("    "); writer.Write(ReportText.BlockId(method, block.Id));
                writer.Write(" [label=\"B"); writer.Write(ReportText.Number(block.Id));
                if (block.Id == result.Graph.EntryId) writer.Write(" (entry)");
                writer.Write("\\l");
                for (var index = 0; index < block.Instructions.Length; index++)
                {
                    writer.Write(ReportText.Number(index)); writer.Write(": ");
                    writer.Write(ReportText.Dot(ReportText.Instruction(block.Instructions[index]))); writer.Write("\\l");
                }
                writer.Write('"');
                if (block.Instructions.Any(instruction => result.Issues.Any(issue => issue.Site.Equals(instruction.Site))))
                    writer.Write(", color=\"#d4512f\", penwidth=2");
                writer.Write("];\n");
            }
            foreach (var block in result.Graph.Blocks.OrderBy(block => block.Id))
            foreach (var successor in block.Successors.OrderBy(id => id))
            {
                writer.Write("    "); writer.Write(ReportText.BlockId(method, block.Id)); writer.Write(" -> ");
                writer.Write(ReportText.BlockId(method, successor)); writer.Write(";\n");
            }
            writer.Write("  }\n");
        }
        for (var diagnosticIndex = 0; diagnosticIndex < report.Diagnostics.Length; diagnosticIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = report.Diagnostics[diagnosticIndex];
            var witness = DependencyWitness.FromDiagnostic(diagnostic);
            if (witness is null) continue;
            var prefix = "d" + ReportText.Number(diagnosticIndex);
            writer.Write("  subgraph cluster_"); writer.Write(prefix);
            writer.Write(" {\n    label=\""); writer.Write(ReportText.Dot(diagnostic.Id + " · dependency witness")); writer.Write("\";\n");
            for (var step = 0; step < witness.Steps.Length; step++)
            {
                writer.Write("    "); writer.Write(prefix); writer.Write("_p"); writer.Write(ReportText.Number(step));
                writer.Write(" [label=\""); writer.Write(ReportText.Dot(witness.Steps[step].Label)); writer.Write("\"];");
                writer.Write('\n');
                if (step == 0) continue;
                writer.Write("    "); writer.Write(prefix); writer.Write("_p"); writer.Write(ReportText.Number(step - 1));
                writer.Write(" -> "); writer.Write(prefix); writer.Write("_p"); writer.Write(ReportText.Number(step)); writer.Write(";\n");
            }
            writer.Write("  }\n");
        }
        for (var diagnosticIndex = 0; diagnosticIndex < report.Diagnostics.Length; diagnosticIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = report.Diagnostics[diagnosticIndex];
            if (RegionWitness.FromDiagnostic(diagnostic) is not { } witness) continue;
            var prefix = "r" + ReportText.Number(diagnosticIndex);
            writer.Write("  subgraph cluster_"); writer.Write(prefix); writer.Write(" {\n    label=\"");
            writer.Write(ReportText.Dot(diagnostic.Id + (witness.CanRender
                ? witness.HasRequirements
                    ? " · lifetime region obligations\nSolid: declared assumptions. Dashed: callback requirements. Arrows: longer outlives shorter."
                    : " · declared lifetime regions\nArrows: longer outlives shorter. Labels: source / required."
                : " · lifetime diagnostic metadata"))); writer.Write("\";\n");
            var identifiers = witness.Regions.Select((name, index) => (name, id: prefix + "_n" + ReportText.Number(index)))
                .ToDictionary(pair => pair.name, pair => pair.id, StringComparer.Ordinal);
            foreach (var region in witness.Regions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write("    "); writer.Write(identifiers[region]); writer.Write(" [label=\"");
                writer.Write(ReportText.Dot(witness.Label(region))); writer.Write("\", style=filled, fontcolor=\"#e5eefb\", fillcolor=\"");
                writer.Write(witness.Fill(region)); writer.Write("\", color=\""); writer.Write(witness.Color(region));
                writer.Write("\", penwidth=2];\n");
            }
            foreach (var edge in witness.AllEdges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write("    "); writer.Write(identifiers[edge.Longer]); writer.Write(" -> "); writer.Write(identifiers[edge.Shorter]);
                writer.Write(edge.IsRequirement
                    ? " [label=\"requires outlives\", style=dashed, color=\"#c9a5ff\"];\n"
                    : " [label=\"outlives\"];\n");
            }
            writer.Write("    "); writer.Write(prefix); writer.Write("_metadata [shape=note, label=\"");
            writer.Write(ReportText.Dot(witness.MetadataLabel(diagnostic))); writer.Write("\"];\n  }\n");
        }
        for (var diagnosticIndex = 0; diagnosticIndex < report.Diagnostics.Length; diagnosticIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = report.Diagnostics[diagnosticIndex];
            if (SynchronizationWitness.FromDiagnostic(diagnostic) is { } synchronization)
                SynchronizationGraphRenderer.WriteDot(writer, synchronization, diagnostic, diagnosticIndex, cancellationToken);
        }
        writer.Write("}\n");
    }
}
