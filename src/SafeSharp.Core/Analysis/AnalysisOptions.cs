using System;

namespace SafeSharp.Core;

/// <summary>Per-invocation limits; an exhausted budget always produces SS0015 instead of silently declaring safety.</summary>
public sealed class AnalysisOptions
{
    public AnalysisOptions(bool captureSnapshots = false, int maxIterations = 10000,
        int maxTrackedResources = 2048, bool reportPossibleIssues = true, bool checkSuspension = true)
    {
        if (maxIterations < 1) throw new ArgumentOutOfRangeException(nameof(maxIterations));
        if (maxTrackedResources < 1) throw new ArgumentOutOfRangeException(nameof(maxTrackedResources));
        CaptureSnapshots = captureSnapshots;
        MaxIterations = maxIterations;
        MaxTrackedResources = maxTrackedResources;
        ReportPossibleIssues = reportPossibleIssues;
        CheckSuspension = checkSuspension;
    }

    public bool CaptureSnapshots { get; }
    public int MaxIterations { get; }
    public int MaxTrackedResources { get; }
    public bool ReportPossibleIssues { get; }
    public bool CheckSuspension { get; }
}
