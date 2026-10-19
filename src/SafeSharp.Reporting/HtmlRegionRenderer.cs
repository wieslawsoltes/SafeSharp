using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace SafeSharp.Reporting;

/// <summary>Self-contained SVG over declared edges; cycles affect layout, never the reported topology.</summary>
internal static class HtmlRegionRenderer
{
    private const int NodeWidth = 220;
    private const int NodeHeight = 76;
    private const int ColumnPitch = 276;
    private const int RowPitch = 140;
    private const int Margin = 38;

    internal static void Write(TextWriter writer, RegionWitness witness, int diagnostic, CancellationToken cancellationToken)
    {
        if (!witness.CanRender)
        {
            writer.Write("<p class=\"region-omitted muted\">"); writer.Write(ReportText.Html(witness.OmissionReason));
            writer.Write(" Full diagnostic properties follow.</p>");
            return;
        }
        writer.Write("<section class=\"region-witness\"><h4>Declared lifetime regions</h4><p class=\"muted\">An arrow from a to b means the declared constraint a : b: a outlives b. Only declared edges are drawn; proof metadata does not add edges.</p>");
        writer.Write("<p class=\"region-legend\"><span class=\"region-source-key\">Source region</span><span class=\"region-required-key\">Required region</span><span>Other declared region</span></p>");
        if (!witness.OtherOrigins.IsEmpty)
        {
            writer.Write("<p class=\"muted\">Other reported origins, not declared vertices: <code>");
            writer.Write(ReportText.Html(string.Join(", ", witness.OtherOrigins))); writer.Write("</code>.</p>");
        }
        var positions = Layout(witness);
        var width = positions.Values.Max(position => position.X) + NodeWidth + Margin + 70;
        var height = positions.Values.Max(position => position.Y) + NodeHeight + Margin;
        var prefix = "region-" + ReportText.Number(diagnostic);
        writer.Write("<div class=\"region-scroll\"><svg class=\"region-graph\" xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-labelledby=\"");
        writer.Write(prefix); writer.Write("-title "); writer.Write(prefix); writer.Write("-description\" viewBox=\"0 0 ");
        Point(writer, width, height, ' '); writer.Write("\" width=\""); writer.Write(ReportText.Number(width));
        writer.Write("\" height=\""); writer.Write(ReportText.Number(height)); writer.Write("\"><title id=\"");
        writer.Write(prefix); writer.Write("-title\">Declared lifetime region graph</title><desc id=\"");
        writer.Write(prefix); writer.Write("-description\">Arrows show only recorded outlives constraints. Node labels identify source and required regions. Complete names and edges appear in the following tables.</desc><defs><marker id=\"");
        writer.Write(prefix); writer.Write("-arrow\" markerWidth=\"8\" markerHeight=\"8\" refX=\"7\" refY=\"4\" orient=\"auto\"><path d=\"M0,0 L8,4 L0,8 Z\" fill=\"#91a6bf\"/></marker></defs>");
        var edgeIndex = 0;
        foreach (var edge in witness.Edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = positions[edge.Longer];
            var end = positions[edge.Shorter];
            writer.Write("<path class=\"region-edge\" data-longer=\""); writer.Write(ReportText.Html(edge.Longer));
            writer.Write("\" data-shorter=\""); writer.Write(ReportText.Html(edge.Shorter));
            writer.Write("\" fill=\"none\" stroke=\"#91a6bf\" stroke-width=\"1.6\" marker-end=\"url(#");
            writer.Write(prefix); writer.Write("-arrow)\" d=\"");
            if (start.Y == end.Y && start.X != end.X)
            {
                var forward = start.X < end.X;
                var sx = start.X + (forward ? NodeWidth : 0);
                var ex = end.X + (forward ? -3 : NodeWidth + 3);
                var y = start.Y + (forward ? 24 : NodeHeight - 24);
                var bend = y + (forward ? -44 : 44);
                writer.Write('M'); Point(writer, sx, y); writer.Write(" C"); Point(writer, sx, bend);
                writer.Write(' '); Point(writer, ex, bend); writer.Write(' '); Point(writer, ex, y);
            }
            else if (end.Y > start.Y && end.Y - start.Y == RowPitch)
            {
                var sx = start.X + NodeWidth / 2;
                var sy = start.Y + NodeHeight;
                var ex = end.X + NodeWidth / 2;
                var ey = end.Y - 3;
                writer.Write('M'); Point(writer, sx, sy); writer.Write(" C"); Point(writer, sx, (sy + ey) / 2);
                writer.Write(' '); Point(writer, ex, (sy + ey) / 2); writer.Write(' '); Point(writer, ex, ey);
            }
            else
            {
                var sx = start.X + NodeWidth;
                var sy = start.Y + NodeHeight * 2 / 3;
                var ex = end.X + NodeWidth + 3;
                var ey = end.Y + NodeHeight / 3;
                var lane = width - 16 - edgeIndex % 6 * 6;
                writer.Write('M'); Point(writer, sx, sy); writer.Write(" C"); Point(writer, lane, sy + 32);
                writer.Write(' '); Point(writer, lane, ey - 32); writer.Write(' '); Point(writer, ex, ey);
            }
            writer.Write("\"><title>"); writer.Write(ReportText.Html(edge.Longer + " : " + edge.Shorter));
            writer.Write(" (declared outlives)</title></path>");
            edgeIndex++;
        }
        foreach (var name in witness.Regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = positions[name];
            writer.Write("<g class=\"region-node\" data-region=\""); writer.Write(ReportText.Html(name));
            writer.Write("\" transform=\"translate("); Point(writer, position.X, position.Y);
            writer.Write(")\"><rect width=\"220\" height=\"76\" rx=\"10\" stroke-width=\"2\" fill=\"");
            writer.Write(witness.Fill(name)); writer.Write("\" stroke=\""); writer.Write(witness.Color(name));
            writer.Write("\"/><text x=\"14\" y=\"29\" fill=\"#e5eefb\" font-size=\"14\" font-family=\"monospace\">");
            writer.Write(ReportText.Html(Shorten(name))); writer.Write("</text><text x=\"14\" y=\"54\" fill=\"");
            writer.Write(witness.Color(name)); writer.Write("\" font-size=\"11\">"); writer.Write(witness.Role(name));
            writer.Write("</text><title>"); writer.Write(ReportText.Html(witness.Label(name))); writer.Write("</title></g>");
        }
        writer.Write("</svg></div><div class=\"table-scroll\"><table><caption>Declared regions and diagnostic roles</caption><thead><tr><th scope=\"col\">Region</th><th scope=\"col\">Diagnostic role</th></tr></thead><tbody>");
        foreach (var region in witness.Regions) Row(writer, region, witness.Role(region));
        writer.Write("</tbody></table></div>");
        if (witness.Edges.IsEmpty) writer.Write("<p class=\"muted\">No outlives constraints were declared. Disconnected vertices do not imply a relationship.</p>");
        else
        {
            writer.Write("<div class=\"table-scroll\"><table><caption>Declared outlives constraints</caption><thead><tr><th scope=\"col\">Longer region</th><th scope=\"col\">Shorter region</th></tr></thead><tbody>");
            foreach (var edge in witness.Edges) Row(writer, edge.Longer, edge.Shorter);
            writer.Write("</tbody></table></div>");
        }
        writer.Write("</section>");
    }

    private static Dictionary<string, Position> Layout(RegionWitness witness)
    {
        var count = witness.Regions.Length;
        var indices = witness.Regions.Select((name, index) => (name, index)).ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
        var reaches = new bool[count, count];
        for (var index = 0; index < count; index++) reaches[index, index] = true;
        foreach (var edge in witness.Edges) reaches[indices[edge.Longer], indices[edge.Shorter]] = true;
        // Bounded reachability groups cycles for layout only. Rendering still enumerates Edges,
        // never this matrix; transitive and reflexive relationships are not drawn as declarations.
        for (var via = 0; via < count; via++)
        for (var from = 0; from < count; from++)
        for (var to = 0; to < count; to++) reaches[from, to] |= reaches[from, via] && reaches[via, to];
        var components = Enumerable.Range(0, count).Select(index => Enumerable.Range(0, count).First(other =>
            reaches[index, other] && reaches[other, index])).ToArray();
        var ranks = components.Distinct().ToDictionary(component => component, _ => 0);
        // The condensation graph is acyclic; at most count passes establish its longest ranks.
        for (var pass = 0; pass < count; pass++)
        {
            var changed = false;
            foreach (var edge in witness.Edges)
            {
                var longer = components[indices[edge.Longer]];
                var shorter = components[indices[edge.Shorter]];
                if (longer == shorter || ranks[shorter] > ranks[longer]) continue;
                ranks[shorter] = ranks[longer] + 1;
                changed = true;
            }
            if (!changed) break;
        }
        var positions = new Dictionary<string, Position>(StringComparer.Ordinal);
        var row = 0;
        foreach (var level in Enumerable.Range(0, count).GroupBy(index => ranks[components[index]]).OrderBy(group => group.Key))
        {
            var members = level.OrderBy(index => index).ToArray();
            for (var index = 0; index < members.Length; index++)
                positions.Add(witness.Regions[members[index]], new Position(Margin + index % 4 * ColumnPitch, Margin + (row + index / 4) * RowPitch));
            row += (members.Length + 3) / 4;
        }
        return positions;
    }

    private static void Point(TextWriter writer, int x, int y, char separator = ',')
    { writer.Write(ReportText.Number(x)); writer.Write(separator); writer.Write(ReportText.Number(y)); }
    private static void Row(TextWriter writer, string first, string second)
    {
        writer.Write("<tr><td>"); writer.Write(ReportText.Html(first)); writer.Write("</td><td>");
        writer.Write(ReportText.Html(second)); writer.Write("</td></tr>");
    }
    private static string Shorten(string name)
    {
        if (name.Length <= 22) return name;
        return name.Substring(0, char.IsHighSurrogate(name[21]) ? 21 : 22) + "…";
    }
    private readonly struct Position
    {
        internal Position(int x, int y) { X = x; Y = y; }
        internal int X { get; }
        internal int Y { get; }
    }
}
