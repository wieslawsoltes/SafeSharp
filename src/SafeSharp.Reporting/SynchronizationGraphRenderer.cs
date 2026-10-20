using System;
using System.IO;
using System.Threading;

namespace SafeSharp.Reporting;

/// <summary>Formats recorded guard requirements and capability membership without inferring equality or scheduling edges.</summary>
internal static class SynchronizationGraphRenderer
{
    private const string Meaning = "Dashed: required guard. Solid: recorded capability intervals. Names are labels, not an inferred identity match. This is not a thread schedule or a race-freedom proof.";

    internal static void WriteHtml(TextWriter writer, SynchronizationWitness witness, int diagnostic, CancellationToken cancellationToken)
    {
        if (!witness.CanRender)
        {
            writer.Write("<p class=\"region-omitted muted\">"); writer.Write(ReportText.Html(witness.OmissionReason));
            writer.Write(" Full diagnostic properties follow.</p>");
            return;
        }
        var prefix = "sync-" + ReportText.Number(diagnostic);
        var height = Math.Max(290, 164 + witness.Held.Length * 112);
        writer.Write("<section class=\"synchronization-witness region-witness\"><h4>Synchronization requirement and observed capabilities</h4><p class=\"muted\">");
        writer.Write(Meaning); writer.Write(" A zero minimum does not establish that the guard is held on every incoming path; many is an abstract upper bound.</p>");
        writer.Write("<div class=\"region-scroll\"><svg class=\"synchronization-graph\" xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-labelledby=\"");
        writer.Write(prefix); writer.Write("-title "); writer.Write(prefix); writer.Write("-description\" width=\"770\" height=\"");
        writer.Write(ReportText.Number(height)); writer.Write("\" viewBox=\"0 0 770 "); writer.Write(ReportText.Number(height));
        writer.Write("\"><title id=\""); writer.Write(prefix); writer.Write("-title\">Required guard and recorded capability intervals</title><desc id=\"");
        writer.Write(prefix); writer.Write("-description\">"); writer.Write(Meaning); writer.Write("</desc><defs><marker id=\"");
        writer.Write(prefix); writer.Write("-arrow\" markerWidth=\"8\" markerHeight=\"8\" refX=\"7\" refY=\"4\" orient=\"auto\"><path d=\"M0,0 L8,4 L0,8 Z\" fill=\"#91a6bf\"/></marker></defs>");
        HtmlEdge(writer, prefix, "requirement", -1, "M170,110 L170,158", dashed: true);
        for (var index = 0; index < witness.Held.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var y = 164 + index * 112 + 43;
            var path = "M545,110 L545,132 L340,132 L340," + ReportText.Number(y) + " L364," + ReportText.Number(y);
            HtmlEdge(writer, prefix, "observed", index, path, dashed: false);
        }
        HtmlNode(writer, "access", -1, "Guard obligation", witness.Access, 30, 30, 280, "#49362b", "#ffb578");
        HtmlNode(writer, "required", -1, witness.RequiredIdentity ?? witness.Required,
            witness.Protocol + " · " + witness.AccessMode, 30, 164, 280, "#49362b", "#ffb578", witness.RequiredLabel);
        HtmlNode(writer, "observed-state", -1, "Recorded capability state",
            witness.Held.IsEmpty ? "No capability records" : ReportText.Number(witness.Held.Length) + " recorded intervals",
            370, 30, 350, "#152237", "#91a6bf");
        for (var index = 0; index < witness.Held.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var held = witness.Held[index];
            HtmlNode(writer, "held", index, held.Name, held.Protocol + " · acquisitions " + held.Interval,
                370, 164 + index * 112, 350, "#173c44", held.Minimum > 0 ? "#79d8cd" : "#91a6bf", held.Label);
        }
        writer.Write("</svg></div><div class=\"table-scroll\"><table><caption>Exact synchronization requirement and recorded intervals</caption><thead><tr><th scope=\"col\">Role</th><th scope=\"col\">Guard</th><th scope=\"col\">Protocol / mode</th><th scope=\"col\">Access / acquisition interval</th></tr></thead><tbody>");
        Row(writer, "Required", witness.Required + (witness.RequiredIdentity is null ? "" : " (identity: " + witness.RequiredIdentity + ")"), witness.Protocol, witness.AccessMode + " (access: " + witness.Access + ")");
        for (var index = 0; index < witness.Held.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var held = witness.Held[index];
            Row(writer, "Observed " + ReportText.Number(index), held.Name, held.Protocol, held.Interval);
        }
        writer.Write("</tbody></table></div></section>");
    }

    internal static void WriteDot(TextWriter writer, SynchronizationWitness witness, ReportDiagnostic diagnostic, int index, CancellationToken cancellationToken)
    {
        var prefix = "s" + ReportText.Number(index);
        writer.Write("  subgraph cluster_"); writer.Write(prefix); writer.Write(" {\n    label=\"");
        writer.Write(ReportText.Dot(diagnostic.Id + " · synchronization evidence\n" + Meaning)); writer.Write("\";\n");
        if (witness.CanRender)
        {
            DotNode(writer, prefix + "_access", "Guard obligation\n" + witness.Access);
            DotNode(writer, prefix + "_required", witness.RequiredLabel);
            DotNode(writer, prefix + "_state", "Recorded capability state\n" + (witness.Held.IsEmpty ? "No capability records" : ReportText.Number(witness.Held.Length) + " recorded intervals"));
            writer.Write("    "); writer.Write(prefix); writer.Write("_access -> "); writer.Write(prefix);
            writer.Write("_required [label=\"requires\", style=dashed, color=\"#c9a5ff\"];\n");
            for (var record = 0; record < witness.Held.Length; record++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = prefix + "_held" + ReportText.Number(record);
                DotNode(writer, id, witness.Held[record].Label);
                writer.Write("    "); writer.Write(prefix); writer.Write("_state -> "); writer.Write(id);
                writer.Write(" [label=\"recorded interval\"];\n");
            }
        }
        writer.Write("    "); writer.Write(prefix); writer.Write("_metadata [shape=note, label=\"");
        writer.Write(ReportText.Dot(witness.MetadataLabel(diagnostic))); writer.Write("\"];\n  }\n");
    }

    internal static void WriteMermaid(TextWriter writer, SynchronizationWitness witness, ReportDiagnostic diagnostic, int index, CancellationToken cancellationToken)
    {
        var prefix = "s" + ReportText.Number(index);
        writer.Write("  subgraph "); writer.Write(prefix); writer.Write("[\"");
        writer.Write(ReportText.Mermaid(diagnostic.Id + " · synchronization evidence; recorded relations, not a thread schedule")); writer.Write("\"]\n");
        if (witness.CanRender)
        {
            MermaidNode(writer, prefix + "_access", "Guard obligation · " + witness.Access);
            MermaidNode(writer, prefix + "_required", witness.RequiredLabel);
            MermaidNode(writer, prefix + "_state", "Recorded capability state · " + (witness.Held.IsEmpty ? "No capability records" : ReportText.Number(witness.Held.Length) + " recorded intervals"));
            writer.Write("    "); writer.Write(prefix); writer.Write("_access -.->|requires| "); writer.Write(prefix); writer.Write("_required\n");
            for (var record = 0; record < witness.Held.Length; record++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = prefix + "_held" + ReportText.Number(record);
                MermaidNode(writer, id, witness.Held[record].Label);
                writer.Write("    "); writer.Write(prefix); writer.Write("_state -->|recorded interval| "); writer.Write(id); writer.Write('\n');
            }
        }
        MermaidNode(writer, prefix + "_metadata", witness.MetadataLabel(diagnostic));
        writer.Write("  end\n");
    }

    private static void HtmlNode(TextWriter writer, string role, int record, string title, string subtitle,
        int x, int y, int width, string fill, string stroke, string? full = null)
    {
        writer.Write("<g class=\"synchronization-node\" data-role=\""); writer.Write(role);
        if (record >= 0) { writer.Write("\" data-record=\""); writer.Write(ReportText.Number(record)); }
        writer.Write("\" transform=\"translate("); writer.Write(ReportText.Number(x)); writer.Write(','); writer.Write(ReportText.Number(y));
        writer.Write(")\"><rect width=\""); writer.Write(ReportText.Number(width)); writer.Write("\" height=\"86\" rx=\"10\" stroke-width=\"2\" fill=\"");
        writer.Write(fill); writer.Write("\" stroke=\""); writer.Write(stroke);
        writer.Write("\"/><text x=\"14\" y=\"32\" fill=\"#e5eefb\" font-size=\"13\" font-family=\"monospace\">");
        writer.Write(ReportText.Html(Shorten(title, width == 280 ? 30 : 40)));
        writer.Write("</text><text x=\"14\" y=\"60\" fill=\""); writer.Write(stroke); writer.Write("\" font-size=\"11\">");
        writer.Write(ReportText.Html(Shorten(subtitle, width == 280 ? 40 : 52))); writer.Write("</text><title>");
        writer.Write(ReportText.Html(full ?? title + "\n" + subtitle)); writer.Write("</title></g>");
    }
    private static void HtmlEdge(TextWriter writer, string prefix, string kind, int record, string path, bool dashed)
    {
        writer.Write("<path class=\"synchronization-edge\" data-kind=\""); writer.Write(kind);
        if (record >= 0) { writer.Write("\" data-record=\""); writer.Write(ReportText.Number(record)); }
        writer.Write("\" fill=\"none\" stroke=\""); writer.Write(dashed ? "#c9a5ff" : "#79d8cd");
        if (dashed) writer.Write("\" stroke-dasharray=\"6 4");
        writer.Write("\" stroke-width=\"1.6\" marker-end=\"url(#"); writer.Write(prefix); writer.Write("-arrow)\" d=\"");
        writer.Write(path); writer.Write("\"><title>"); writer.Write(dashed ? "Declared guard requirement" : "Recorded capability interval"); writer.Write("</title></path>");
    }
    private static void Row(TextWriter writer, params string[] columns)
    {
        writer.Write("<tr>");
        foreach (var column in columns) { writer.Write("<td>"); writer.Write(ReportText.Html(column)); writer.Write("</td>"); }
        writer.Write("</tr>");
    }
    private static void DotNode(TextWriter writer, string id, string label)
    {
        writer.Write("    "); writer.Write(id); writer.Write(" [label=\""); writer.Write(ReportText.Dot(label)); writer.Write("\"];\n");
    }
    private static void MermaidNode(TextWriter writer, string id, string label)
    {
        writer.Write("    "); writer.Write(id); writer.Write("[\""); writer.Write(ReportText.Mermaid(label)); writer.Write("\"]\n");
    }
    private static string Shorten(string value, int length)
    {
        // Source operations can span lines. Only the compact node preview normalizes whitespace;
        // the full title, table and raw diagnostic properties retain the producer's text.
        value = value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        return value.Length <= length ? value : value.Substring(0,
            char.IsHighSurrogate(value[length - 1]) ? length - 1 : length) + "…";
    }
}
