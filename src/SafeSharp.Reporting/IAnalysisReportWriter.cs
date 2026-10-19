using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace SafeSharp.Reporting;

/// <summary>A streaming renderer. Implementations never analyze source or access its files.</summary>
public interface IAnalysisReportWriter
{
    void Write(AnalysisReport report, TextWriter writer, CancellationToken cancellationToken = default);
}

public enum ReportFormat
{
    Json,
    Sarif,
    Dot,
    Mermaid,
    Html,
}

/// <summary>Convenience factory for the independently reusable report writers.</summary>
public static class AnalysisReportRenderer
{
    public static IAnalysisReportWriter CreateWriter(ReportFormat format) => format switch
    {
        ReportFormat.Json => new JsonReportWriter(),
        ReportFormat.Sarif => new SarifReportWriter(),
        ReportFormat.Dot => new DotReportWriter(),
        ReportFormat.Mermaid => new MermaidReportWriter(),
        ReportFormat.Html => new HtmlReportWriter(),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string Render(AnalysisReport report, ReportFormat format,
        CancellationToken cancellationToken = default)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        CreateWriter(format).Write(report, writer, cancellationToken);
        return writer.ToString();
    }
}
