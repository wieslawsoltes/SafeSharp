using System;
using System.Globalization;
using System.Linq;
using System.Text;
using SafeSharp.Core;

namespace SafeSharp.Reporting;

internal static class ReportText
{
    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Html(string? value)
    {
        if (value is null) return string.Empty;
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '&': result.Append("&amp;"); break;
                case '<': result.Append("&lt;"); break;
                case '>': result.Append("&gt;"); break;
                case '"': result.Append("&quot;"); break;
                case '\'': result.Append("&#39;"); break;
                default:
                    if (character == '\0') result.Append("&#xfffd;");
                    else result.Append(character);
                    break;
            }
        }
        return result.ToString();
    }

    public static string Dot(string? value)
    {
        if (value is null) return string.Empty;
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': result.Append("\\\\"); break;
                case '"': result.Append("\\\""); break;
                case '\n': result.Append("\\l"); break;
                case '\r': break;
                default: result.Append(char.IsControl(character) ? ' ' : character); break;
            }
        }
        return result.ToString();
    }

    public static string Mermaid(string? value)
    {
        if (value is null) return string.Empty;
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character == ' ' || character == '.' || character == '_')
                result.Append(character);
            else if (character == '\r') { }
            else if (character == '\n') result.Append(' ');
            else result.Append('#').Append(((int)character).ToString(CultureInfo.InvariantCulture)).Append(';');
        }
        return result.ToString();
    }

    public static string BlockId(int method, int block) => "m" + Number(method) + "_b" +
        (block < 0 ? "n" + ((long)block * -1).ToString(CultureInfo.InvariantCulture) : Number(block));

    public static string Site(SourceSite site) => string.IsNullOrEmpty(site.Path)
        ? "(no source location)"
        : site.Path + ":" + Number(site.Line + 1) + ":" + Number(site.Column + 1);

    public static string Instruction(FlowInstruction instruction)
    {
        var parts = new[]
        {
            instruction.Target is null ? null : instruction.Target + " =",
            instruction.Kind.ToString(),
            instruction.Source,
            instruction.ResourceId is null ? null : "resource " + instruction.ResourceId,
            instruction.Owner is null ? null : "owner " + instruction.Owner,
            instruction.Description,
        };
        return string.Join(" ", parts.Where(part => !string.IsNullOrEmpty(part)));
    }

    /// <summary>
    /// Converts paths without consulting the current directory, preserving reproducibility.
    /// Windows drive and UNC paths are recognized on every host. Arbitrary URI schemes are
    /// treated as literal path components, preventing source names becoming active links.
    /// </summary>
    public static string SourceUri(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(normalized, UriKind.Absolute, out var fileUri) && fileUri.IsFile)
            return fileUri.AbsoluteUri;
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            var separator = normalized.IndexOf('/', 2);
            if (separator > 2)
            {
                var host = normalized.Substring(2, separator - 2);
                return "file://" + Uri.EscapeDataString(host) + EscapePath(normalized.Substring(separator));
            }
        }
        if (normalized.Length >= 3 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/')
            return "file:///" + char.ToUpperInvariant(normalized[0]) + ":" + EscapePath(normalized.Substring(2));
        if (normalized.StartsWith("/", StringComparison.Ordinal)) return "file://" + EscapePath(normalized);
        return EscapePath(normalized);
    }

    private static string EscapePath(string path) => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
}
