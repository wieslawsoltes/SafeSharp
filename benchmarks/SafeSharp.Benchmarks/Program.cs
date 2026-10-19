using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SafeSharp.Core;

namespace SafeSharp.Benchmarks;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = BenchmarkOptions.Parse(args);
            var cases = new List<BenchmarkCase>();
            if (options.Suite is "all" or "liveness") cases.AddRange(LivenessBenchmarks.Create());
            if (options.Suite is "all" or "regions") cases.AddRange(RegionBenchmarks.Create(options.BaselineCore));
            if (options.Filter is not null) cases.RemoveAll(benchmark => !benchmark.Name.Contains(options.Filter, StringComparison.OrdinalIgnoreCase));
            if (cases.Count == 0) throw new ArgumentException("No benchmark scenario matched the selected suite and filter.");
            var results = new List<BenchmarkResult>();
            Console.WriteLine($"SafeSharp {options.Suite}: {options.Warmup} warmups, {options.Iterations} samples, {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}");
            Console.WriteLine("Scenario | baseline ms | optimized ms | time ratio | baseline bytes | optimized bytes");
            foreach (var benchmark in cases)
            {
                benchmark.Verify();
                var measurement = Measure(benchmark, options);
                results.Add(measurement);
                Console.WriteLine($"{measurement.Name} | {Format(measurement.BaselineMilliseconds)} | {measurement.OptimizedMilliseconds:F4} | {Format(measurement.Speedup)} | {measurement.BaselineAllocatedBytes?.ToString() ?? "n/a"} | {measurement.OptimizedAllocatedBytes}");
            }
            if (options.JsonPath is not null)
            {
                var path = Path.GetFullPath(options.JsonPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var output = new
                {
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    runtime = RuntimeInformation.FrameworkDescription,
                    operatingSystem = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    logicalProcessors = Environment.ProcessorCount,
                    optimizedCoreSha256 = Hash(typeof(OwnershipAnalysisEngine).Assembly.Location),
                    baselineCoreSha256 = options.BaselineCore is null ? null : Hash(options.BaselineCore),
                    options.Suite,
                    options.Warmup,
                    options.Iterations,
                    options.Filter,
                    tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? Environment.GetEnvironmentVariable("COMPlus_TieredCompilation") ?? "runtime default",
                    notes = "Medians of alternating baseline/optimized synchronous samples; construction excluded, solve and requested outputs included. Allocated bytes are per current thread. Microbenchmarks are not whole-project build speedups.",
                    results,
                };
                File.WriteAllText(path, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("JSON: " + path);
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static string Format(double? value) => value?.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a";
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static BenchmarkResult Measure(BenchmarkCase benchmark, BenchmarkOptions options)
    {
        for (var index = 0; index < options.Warmup; index++)
        {
            if (benchmark.Baseline is not null) GC.KeepAlive(benchmark.Baseline());
            GC.KeepAlive(benchmark.Optimized());
        }
        var baseline = new List<Sample>();
        var optimized = new List<Sample>();
        for (var index = 0; index < options.Iterations; index++)
        {
            if ((index & 1) == 0)
            {
                if (benchmark.Baseline is not null) baseline.Add(Sample.Run(benchmark.Baseline));
                optimized.Add(Sample.Run(benchmark.Optimized));
            }
            else
            {
                optimized.Add(Sample.Run(benchmark.Optimized));
                if (benchmark.Baseline is not null) baseline.Add(Sample.Run(benchmark.Baseline));
            }
        }
        var beforeTime = baseline.Count == 0 ? (double?)null : Median(baseline.Select(sample => sample.Milliseconds));
        var afterTime = Median(optimized.Select(sample => sample.Milliseconds));
        return new(benchmark.Name, benchmark.Details, beforeTime, afterTime,
            beforeTime / afterTime, baseline.Count == 0 ? null : (long)Median(baseline.Select(sample => (double)sample.AllocatedBytes)),
            (long)Median(optimized.Select(sample => (double)sample.AllocatedBytes)));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        var middle = sorted.Length / 2;
        return (sorted.Length & 1) == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    private readonly record struct Sample(double Milliseconds, long AllocatedBytes)
    {
        internal static Sample Run(Func<object> operation)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            var result = operation();
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            GC.KeepAlive(result);
            return new(elapsed, allocated);
        }
    }
}

internal sealed record BenchmarkCase(string Name, string Details, Func<object>? Baseline, Func<object> Optimized, Action Verify);
internal sealed record BenchmarkResult(string Name, string Details, double? BaselineMilliseconds, double OptimizedMilliseconds,
    double? Speedup, long? BaselineAllocatedBytes, long OptimizedAllocatedBytes);

internal sealed record BenchmarkOptions(string Suite, int Iterations, int Warmup, string? BaselineCore, string? JsonPath, string? Filter)
{
    internal static BenchmarkOptions Parse(string[] args)
    {
        var suite = "all";
        var iterations = 12;
        var warmup = 4;
        string? baseline = null;
        string? json = null;
        string? filter = null;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option is "liveness" or "regions" or "all") { suite = option; continue; }
            if (++index == args.Length) throw new ArgumentException("Missing value for " + option);
            switch (option)
            {
                case "--iterations": iterations = int.Parse(args[index], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--warmup": warmup = int.Parse(args[index], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--baseline-core": baseline = Path.GetFullPath(args[index]); break;
                case "--json": json = args[index]; break;
                case "--filter": filter = args[index]; break;
                default: throw new ArgumentException("Unknown benchmark option " + option);
            }
        }
        if (iterations < 1 || warmup < 0) throw new ArgumentException("Iterations must be positive and warmup nonnegative.");
        if (baseline is not null && !File.Exists(baseline)) throw new FileNotFoundException("The baseline Core assembly does not exist.", baseline);
        return new(suite, iterations, warmup, baseline, json, filter);
    }
}
