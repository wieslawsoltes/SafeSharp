using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SafeSharp.Core;

namespace SafeSharp.Reporting;

/// <summary>A deterministic SVG layout over actual CFG edges; no diagram service or JavaScript dependency.</summary>
internal static class HtmlCfgRenderer
{
    private const int NodeWidth = 320;
    private const int NodeHeight = 150;
    private const int ColumnGap = 44;
    private const int RowGap = 66;
    private const int Margin = 28;

    public static void Write(TextWriter writer, AnalysisResult result, int method, CancellationToken cancellationToken)
    {
        var blocks = result.Graph.Blocks.ToDictionary(block => block.Id);
        var levels = ComputeLevels(result.Graph);
        var positions = new Dictionary<int, Position>();
        var row = 0;
        var maxColumns = 1;
        foreach (var level in levels.GroupBy(pair => pair.Value).OrderBy(group => group.Key))
        {
            var members = level.OrderBy(pair => pair.Key).ToArray();
            for (var index = 0; index < members.Length; index++)
            {
                var column = index % 4;
                positions[members[index].Key] = new Position(Margin + column * (NodeWidth + ColumnGap),
                    Margin + (row + index / 4) * (NodeHeight + RowGap));
            }
            maxColumns = System.Math.Max(maxColumns, System.Math.Min(4, members.Length));
            row += (members.Length + 3) / 4;
        }
        var width = Margin * 2 + maxColumns * (NodeWidth + ColumnGap) + 52;
        var height = Margin * 2 + row * (NodeHeight + RowGap);
        writer.Write("<div class=\"graph-scroll\"><svg class=\"cfg\" xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-label=\"");
        writer.Write(ReportText.Html("Control-flow graph for " + result.Graph.Name));
        writer.Write("\" viewBox=\"0 0 "); writer.Write(ReportText.Number(width)); writer.Write(' ');
        writer.Write(ReportText.Number(height)); writer.Write("\" width=\""); writer.Write(ReportText.Number(width));
        writer.Write("\" height=\""); writer.Write(ReportText.Number(height)); writer.Write("\"><defs><marker id=\"arrow-");
        writer.Write(ReportText.Number(method));
        writer.Write("\" markerWidth=\"8\" markerHeight=\"8\" refX=\"7\" refY=\"4\" orient=\"auto\"><path d=\"M0,0 L8,4 L0,8 Z\" fill=\"#6f86a4\"/></marker></defs>");
        var edgeIndex = 0;
        foreach (var block in blocks.Values.OrderBy(block => block.Id))
        foreach (var successor in block.Successors.OrderBy(id => id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = positions[block.Id];
            var end = positions[successor];
            writer.Write("<path class=\"edge\" fill=\"none\" stroke=\"#6f86a4\" stroke-width=\"1.8\" marker-end=\"url(#arrow-");
            writer.Write(ReportText.Number(method)); writer.Write(")\" d=\"");
            if (end.Y > start.Y)
            {
                var sx = start.X + NodeWidth / 2;
                var sy = start.Y + NodeHeight;
                var ex = end.X + NodeWidth / 2;
                var ey = end.Y - 3;
                var middle = (sy + ey) / 2;
                writer.Write("M"); WritePoint(writer, sx, sy); writer.Write(" C"); WritePoint(writer, sx, middle);
                writer.Write(' '); WritePoint(writer, ex, middle); writer.Write(' '); WritePoint(writer, ex, ey);
            }
            else
            {
                var sx = start.X + NodeWidth;
                var sy = start.Y + NodeHeight * 2 / 3;
                var ex = end.X + NodeWidth + 3;
                var ey = end.Y + NodeHeight / 3;
                var lane = width - 20 - (edgeIndex % 5) * 8;
                writer.Write("M"); WritePoint(writer, sx, sy); writer.Write(" C"); WritePoint(writer, lane, sy + 35);
                writer.Write(' '); WritePoint(writer, lane, ey - 35); writer.Write(' '); WritePoint(writer, ex, ey);
            }
            writer.Write("\"><title>B"); writer.Write(ReportText.Number(block.Id)); writer.Write(" → B");
            writer.Write(ReportText.Number(successor)); writer.Write("</title></path>");
            edgeIndex++;
        }
        var snapshots = result.Blocks.ToDictionary(block => block.Id);
        foreach (var block in blocks.Values.OrderBy(block => block.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = positions[block.Id];
            var hasIssue = block.Instructions.Any(instruction => result.Issues.Any(issue => issue.Site.Equals(instruction.Site)));
            writer.Write("<a href=\"#"); writer.Write(ReportText.BlockId(method, block.Id));
            writer.Write("\"><g transform=\"translate("); WritePoint(writer, position.X, position.Y);
            writer.Write(")\"><rect width=\"320\" height=\"150\" rx=\"12\" fill=\"#152237\" stroke=\"");
            writer.Write(hasIssue ? "#ffb578" : "#3b526f"); writer.Write("\" stroke-width=\"1.6\"/>");
            writer.Write("<text x=\"16\" y=\"26\" fill=\"#e8f1ff\" font-size=\"14\" font-weight=\"700\">B");
            writer.Write(ReportText.Number(block.Id));
            if (block.Id == result.Graph.EntryId) writer.Write(" · ENTRY");
            if (hasIssue) writer.Write(" · DIAGNOSTIC");
            if (snapshots.TryGetValue(block.Id, out var captured) && !captured.Exit.IsReachable) writer.Write(" · PRUNED");
            writer.Write("</text>");
            var previewCount = System.Math.Min(3, block.Instructions.Length);
            for (var index = 0; index < previewCount; index++)
            {
                writer.Write("<text x=\"16\" y=\""); writer.Write(ReportText.Number(52 + index * 20));
                writer.Write("\" fill=\"#c0cfdf\" font-size=\"11\" font-family=\"monospace\">");
                writer.Write(ReportText.Html(Shorten(ReportText.Instruction(block.Instructions[index]), 44)));
                writer.Write("</text>");
            }
            writer.Write("<text x=\"16\" y=\"128\" fill=\"#91a6bf\" font-size=\"11\">");
            writer.Write(ReportText.Number(block.Instructions.Length)); writer.Write(" instructions");
            if (snapshots.TryGetValue(block.Id, out var snapshot))
            {
                writer.Write(" · "); writer.Write(ReportText.Number(snapshot.Exit.Resources.Length)); writer.Write(" resources · ");
                writer.Write(ReportText.Number(snapshot.Exit.Loans.Length)); writer.Write(" loans");
            }
            else writer.Write(" · no captured state");
            writer.Write("</text><title>");
            writer.Write(ReportText.Html(string.Join("\n", block.Instructions.Select(ReportText.Instruction))));
            writer.Write("</title></g></a>");
        }
        writer.Write("</svg></div>");
    }

    private static Dictionary<int, int> ComputeLevels(AnalysisGraph graph)
    {
        var blocks = graph.Blocks.ToDictionary(block => block.Id);
        var levels = new Dictionary<int, int> { [graph.EntryId] = 0 };
        var pending = new Queue<int>();
        pending.Enqueue(graph.EntryId);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var successor in blocks[current].Successors.OrderBy(id => id))
            {
                if (levels.ContainsKey(successor)) continue;
                levels.Add(successor, levels[current] + 1);
                pending.Enqueue(successor);
            }
        }
        var unreachableLevel = levels.Values.Max() + 1;
        foreach (var block in graph.Blocks.Where(block => !levels.ContainsKey(block.Id)).OrderBy(block => block.Id))
            levels.Add(block.Id, unreachableLevel);
        return levels;
    }

    private static void WritePoint(TextWriter writer, int x, int y)
    {
        writer.Write(ReportText.Number(x)); writer.Write(','); writer.Write(ReportText.Number(y));
    }

    private static string Shorten(string value, int length)
    {
        if (value.Length <= length) return value;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value.Substring(0, length) + "…";
    }

    private readonly struct Position
    {
        public Position(int x, int y) { X = x; Y = y; }
        public int X { get; }
        public int Y { get; }
    }
}
