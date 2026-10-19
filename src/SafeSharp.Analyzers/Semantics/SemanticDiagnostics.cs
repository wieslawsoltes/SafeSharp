using Microsoft.CodeAnalysis;

namespace SafeSharp.Analyzers.Semantics;

/// <summary>Diagnostics for contracts, aggregate ownership, task captures, and service dependency lifetimes.</summary>
public static class SemanticDiagnostics
{
    public static readonly DiagnosticDescriptor MissingDisposalContract = Create("SS0020", "An aggregate owner must provide disposal", "Type '{0}' owns disposable field '{1}' but implements neither IDisposable nor IAsyncDisposable", "A type owning a disposable field must expose an explicit disposal contract.");
    public static readonly DiagnosticDescriptor OwnedFieldNotDisposed = Create("SS0021", "Owned field has no reachable release", "Owned field '{0}' has no recognized release reachable from a disposal implementation", "Release an owned field from Dispose/DisposeAsync or a directly called helper. This rule proves presence of a release, not completeness of all exceptional paths.");
    public static readonly DiagnosticDescriptor CopyableOwner = Create("SS0022", "Value-type ownership can be duplicated", "Ownership-bearing struct '{0}' can be copied without a move; use a reference owner or an explicitly reviewed value-type ownership design", "C# structs remain copyable, including readonly and ref structs. An ownership annotation cannot change their language-level copy semantics.");
    public static readonly DiagnosticDescriptor InvalidContract = Create("SS0023", "Ownership contract is inconsistent", "Invalid SafeSharp contract on '{0}': {1}", "Ownership, shared loans, and exclusive loans are different contracts. A returned loan must name an existing source whose lifetime can bound the result.");
    public static readonly DiagnosticDescriptor IgnoredMustUse = Create("SS0024", "Required result is ignored", "The result of '{0}' must be consumed or explicitly discarded", "Assign, return, await, or otherwise consume a MustUse result. An explicit discard acknowledges intentional non-use.");
    public static readonly DiagnosticDescriptor ThreadAffineTaskCapture = Create("SS0025", "Thread-affine value crosses a Task.Run boundary", "Thread-affine value '{0}' is accessed by Task.Run", "Task.Run schedules work on the thread pool. Marshal access to the owning context or transfer a thread-safe snapshot.");
    public static readonly DiagnosticDescriptor CaptiveScopedService = Create("SS0026", "Singleton may capture a scoped dependency", "Singleton '{0}' may retain a scoped service through: {1}", "A singleton must not retain a scoped dependency, including through transient intermediates. Create and dispose an explicit scope for bounded work.", compilationEnd: true);
    public static readonly DiagnosticDescriptor StoredBorrowedParameter = Create("SS0027", "Borrowed argument escapes into persistent storage", "Borrowed parameter '{0}' is retained in '{1}'", "A borrowed parameter cannot be retained in a field or property without a lifetime relationship that C# can express and SafeSharp can verify.");
    public static readonly DiagnosticDescriptor EscapingUsingCapture = Create("SS0028", "Captured resource may outlive its using scope", "Escaping delegate or task captures using resource '{0}'", "Complete background work before the using scope ends, or transfer ownership to a longer-lived owner.");
    public static readonly DiagnosticDescriptor IncompleteServiceAnalysis = new("SS0029", "Service graph analysis reached its budget", "Service dependency analysis for '{0}' exceeded its exploration budget; lifetime safety is not established", "SafeSharp.Ownership", DiagnosticSeverity.Info, isEnabledByDefault: true,
        description: "Recursive open-generic registrations can generate an unbounded dependency graph. SafeSharp bounds exploration and explicitly reports incompleteness.",
        helpLinkUri: "https://github.com/wieslawsoltes/SafeSharp/blob/main/docs/diagnostics.md#ss0029", customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    private static DiagnosticDescriptor Create(string id, string title, string message, string description, bool compilationEnd = false) =>
        new(id, title, message, "SafeSharp.Ownership", DiagnosticSeverity.Warning, isEnabledByDefault: true,
            description: description, helpLinkUri: "https://github.com/wieslawsoltes/SafeSharp/blob/main/docs/diagnostics.md#" + id.ToLowerInvariant(),
            customTags: compilationEnd ? new[] { WellKnownDiagnosticTags.CompilationEnd } : System.Array.Empty<string>());
}
