using Microsoft.CodeAnalysis;

namespace SafeSharp.Analyzers.Semantics;

/// <summary>Explicit concurrency contracts and bounded structural contract analysis.</summary>
public static class ConcurrencyDiagnostics
{
    public static readonly DiagnosticDescriptor MissingSynchronizationGuard = Create("SS0049", "Synchronization guard is not established",
        "Access to '{0}' requires guard '{1}': {2}",
        "Guarded storage and declared lock preconditions require a matching capability on every incoming path. Monitor, System.Threading.Lock and reader/writer guards have distinct protocols.");

    public static readonly DiagnosticDescriptor InvalidConcurrencyContract = Create("SS0044", "Concurrency contract is not structurally satisfied",
        "Type '{0}' does not satisfy its {1} contract: {2}",
        "Send permits declared transfer; Sync permits declared sharing. Composition and generic requirements must hold for every instance field. These contracts do not prove arbitrary synchronization.");

    public static readonly DiagnosticDescriptor UnsafeConcurrencyCapture = Create("SS0045", "Captured value does not satisfy the concurrency boundary",
        "Captured value '{0}' requires {1} at this boundary: {2}",
        "A shared callback requires Sync state. An explicitly declared ownership-transfer callback requires Send state and the caller must also honor its ownership-transfer contract.");

    public static readonly DiagnosticDescriptor IncompleteContractAnalysis = Create("SS0046", "Contract analysis reached its budget",
        "Contract analysis for '{0}' exceeded its {1} budget; the required safety property is not established",
        "Structural composition, recursive cleanup summaries, and borrowed escape provenance have finite exploration limits. A budget result must not be treated as a proof of safety.", DiagnosticSeverity.Info);

    private static DiagnosticDescriptor Create(string id, string title, string message, string description,
        DiagnosticSeverity severity = DiagnosticSeverity.Warning) => new(id, title, message, "SafeSharp.Ownership", severity,
        isEnabledByDefault: true, description: description,
        helpLinkUri: "https://github.com/wieslawsoltes/SafeSharp/blob/main/docs/diagnostics.md#" + id.ToLowerInvariant());
}
