using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace SafeSharp.Reporting;

/// <summary>Emits real control-flow edges and reported dependency witnesses as Graphviz graphs.</summary>
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
        writer.Write("}\n");
    }
}
