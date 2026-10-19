using System.Reflection;
using System.Text;
using SafeSharp.Reporting;

namespace SafeSharp.Tool;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, @event) => { @event.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var options = ToolOptions.Parse(args);
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+')[0] ?? "unknown";
            if (options.Help) { Console.Out.WriteLine(ToolOptions.Usage); return 0; }
            if (options.Version) { Console.Out.WriteLine(version); return 0; }
            if (options.InputKind != InputKind.Source) WorkspaceBootstrap.Register();
            var report = await AnalysisRunner.RunAsync(options, version, Console.Error, cancellation.Token).ConfigureAwait(false);
            await WriteReportAsync(report, options, cancellation.Token).ConfigureAwait(false);
            Console.Error.WriteLine($"SafeSharp: {report.Results.Length} method graphs, {report.Diagnostics.Length} diagnostics; analysis {(report.AnalysisComplete ? "completed" : "incomplete")}.");
            if (!report.AnalysisComplete) return 2;
            return options.FailureThreshold switch
            {
                FailureThreshold.Warning when report.Diagnostics.Any(diagnostic => diagnostic.Severity >= ReportSeverity.Warning) => 1,
                FailureThreshold.Error when report.Diagnostics.Any(diagnostic => diagnostic.Severity == ReportSeverity.Error) => 1,
                _ => 0,
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("SafeSharp: canceled.");
            return 130;
        }
        catch (Exception exception) when (exception is ToolException or IOException or UnauthorizedAccessException or
                                        ArgumentException or InvalidOperationException or BadImageFormatException)
        {
            Console.Error.WriteLine("SafeSharp: " + exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SafeSharp: host failure ({exception.GetType().Name}): {exception.Message}");
            return 2;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static async Task WriteReportAsync(AnalysisReport report, ToolOptions options, CancellationToken cancellationToken)
    {
        var renderer = AnalysisReportRenderer.CreateWriter(options.Format);
        if (options.Output is null or "-")
        {
            renderer.Write(report, Console.Out, cancellationToken);
            return;
        }
        var directory = Path.GetDirectoryName(options.Output)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".safesharp-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 65536, options: FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 65536))
            {
                renderer.Write(report, writer, cancellationToken);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, options.Output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
