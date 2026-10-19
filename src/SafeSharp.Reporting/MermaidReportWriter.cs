using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace SafeSharp.Reporting;

/// <summary>Mermaid CFG and dependency-witness output using generated identifiers and escaped source labels.</summary>
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
