using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace SafeSharp.Reporting;

/// <summary>Mermaid flow, dependency and declared region graphs with generated identifiers and escaped labels.</summary>
public sealed class MermaidReportWriter : IAnalysisReportWriter
{
    public void Write(AnalysisReport report, TextWriter writer, CancellationToken cancellationToken = default)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        cancellationToken.ThrowIfCancellationRequested();
        writer.Write("flowchart TD\n");
        for (var method = 0; method < report.Results.Length; method++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = report.Results[method];
            writer.Write("  subgraph m"); writer.Write(ReportText.Number(method)); writer.Write("[\"");
            writer.Write(ReportText.Mermaid(result.Graph.Name)); writer.Write("\"]\n");
            foreach (var block in result.Graph.Blocks.OrderBy(block => block.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write("    "); writer.Write(ReportText.BlockId(method, block.Id)); writer.Write("[\"B");
                writer.Write(ReportText.Number(block.Id));
                if (block.Id == result.Graph.EntryId) writer.Write(" · entry");
                foreach (var instruction in block.Instructions)
                {
                    writer.Write(" | "); writer.Write(ReportText.Mermaid(ReportText.Instruction(instruction)));
                }
                writer.Write("\"]\n");
            }
            foreach (var block in result.Graph.Blocks.OrderBy(block => block.Id))
            foreach (var successor in block.Successors.OrderBy(id => id))
            {
                writer.Write("    "); writer.Write(ReportText.BlockId(method, block.Id)); writer.Write(" --> ");
                writer.Write(ReportText.BlockId(method, successor)); writer.Write('\n');
            }
            writer.Write("  end\n");
        }
        for (var diagnosticIndex = 0; diagnosticIndex < report.Diagnostics.Length; diagnosticIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = report.Diagnostics[diagnosticIndex];
            if (RegionWitness.FromDiagnostic(diagnostic) is not { } witness) continue;
            var prefix = "r" + ReportText.Number(diagnosticIndex);
            writer.Write("  subgraph "); writer.Write(prefix); writer.Write("[\"");
            writer.Write(ReportText.Mermaid(diagnostic.Id + (witness.CanRender
                ? " · declared lifetime regions; arrows mean outlives"
                : " · lifetime diagnostic metadata"))); writer.Write("\"]\n");
            var identifiers = witness.Regions.Select((name, index) => (name, id: prefix + "_n" + ReportText.Number(index)))
                .ToDictionary(pair => pair.name, pair => pair.id, StringComparer.Ordinal);
            foreach (var region in witness.Regions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write("    "); writer.Write(identifiers[region]); writer.Write("[\"");
                writer.Write(ReportText.Mermaid(witness.Label(region).Replace("\n", " · "))); writer.Write("\"]\n    style ");
                writer.Write(identifiers[region]); writer.Write(" fill:"); writer.Write(witness.Fill(region));
                writer.Write(",stroke:"); writer.Write(witness.Color(region)); writer.Write(",color:#e5eefb,stroke-width:2px\n");
            }
            foreach (var edge in witness.Edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write("    "); writer.Write(identifiers[edge.Longer]); writer.Write(" -->|outlives| ");
                writer.Write(identifiers[edge.Shorter]); writer.Write('\n');
            }
            writer.Write("    "); writer.Write(prefix); writer.Write("_metadata[\"");
            writer.Write(ReportText.Mermaid(witness.MetadataLabel(diagnostic))); writer.Write("\"]\n  end\n");
        }
        for (var diagnosticIndex = 0; diagnosticIndex < report.Diagnostics.Length; diagnosticIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = report.Diagnostics[diagnosticIndex];
            var witness = DependencyWitness.FromDiagnostic(diagnostic);
            if (witness is null) continue;
            var prefix = "d" + ReportText.Number(diagnosticIndex);
            writer.Write("  subgraph "); writer.Write(prefix); writer.Write("[\"");
            writer.Write(ReportText.Mermaid(diagnostic.Id + " · dependency witness")); writer.Write("\"]\n");
            for (var step = 0; step < witness.Steps.Length; step++)
            {
                writer.Write("    "); writer.Write(prefix); writer.Write("_p"); writer.Write(ReportText.Number(step));
                writer.Write("[\""); writer.Write(ReportText.Mermaid(witness.Steps[step].Label.Replace("\n", " · "))); writer.Write("\"]\n");
                if (step == 0) continue;
                writer.Write("    "); writer.Write(prefix); writer.Write("_p"); writer.Write(ReportText.Number(step - 1));
                writer.Write(" --> "); writer.Write(prefix); writer.Write("_p"); writer.Write(ReportText.Number(step)); writer.Write('\n');
            }
            writer.Write("  end\n");
        }
    }
}
