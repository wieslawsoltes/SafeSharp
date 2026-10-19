using System;
using System.Collections.Immutable;

namespace SafeSharp.Reporting;

/// <summary>A presentation of the actual path recorded by the service analyzer, not a reconstructed container.</summary>
internal sealed class DependencyWitness
{
    private DependencyWitness(ImmutableArray<DependencyStep> steps) => Steps = steps;
    public ImmutableArray<DependencyStep> Steps { get; }

    public static DependencyWitness? FromDiagnostic(ReportDiagnostic diagnostic)
    {
        if (!diagnostic.Properties.TryGetValue("SafeSharp.DependencyPath", out var path) || string.IsNullOrEmpty(path)) return null;
        var implementations = Split(path!);
        if (implementations.Length < 2) return null;
        diagnostic.Properties.TryGetValue("SafeSharp.DependencyServices", out var servicePath);
        diagnostic.Properties.TryGetValue("SafeSharp.DependencyLifetimes", out var lifetimePath);
        var services = Split(servicePath);
        var lifetimes = Split(lifetimePath);
        var steps = ImmutableArray.CreateBuilder<DependencyStep>();
        for (var index = 0; index < implementations.Length; index++)
            steps.Add(new DependencyStep(implementations[index], services.Length == implementations.Length ? services[index] : null,
                lifetimes.Length == implementations.Length ? lifetimes[index] : null));
        return new DependencyWitness(steps.ToImmutable());
    }

    private static string[] Split(string? value) => value is null ? Array.Empty<string>() : value.Split(new[] { " -> " }, StringSplitOptions.None);
}

internal sealed class DependencyStep
{
    public DependencyStep(string implementation, string? service, string? lifetime)
    {
        Implementation = implementation;
        Service = service;
        Lifetime = lifetime;
    }

    public string Implementation { get; }
    public string? Service { get; }
    public string? Lifetime { get; }
    public string Label => Implementation + (Service is null || Service == Implementation ? "" : "\nservice " + Service) +
        (Lifetime is null ? "" : "\n" + Lifetime);
}
