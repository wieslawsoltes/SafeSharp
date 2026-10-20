using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace SafeSharp.Analyzers;

public static class OwnershipDiagnostics
{
    public const string Category = "Ownership";
    private static DiagnosticDescriptor Rule(string id, string title) => new(id, title, "{0}", Category,
        DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: title + ". SafeSharp checks explicit ownership effects over reachable C# control-flow paths. Related locations identify acquisition and conflicting effects.",
        helpLinkUri: "https://github.com/wieslawsoltes/SafeSharp/blob/main/docs/diagnostics.md#" + id.ToLowerInvariant());

    public static readonly DiagnosticDescriptor UseAfterDispose = Rule("SS0001", "Use after disposal");
    public static readonly DiagnosticDescriptor DoubleDispose = Rule("SS0002", "Repeated disposal");
    public static readonly DiagnosticDescriptor ResourceLeak = Rule("SS0003", "Owned resource is not released");
    public static readonly DiagnosticDescriptor UseAfterMove = Rule("SS0004", "Use after ownership transfer");
    public static readonly DiagnosticDescriptor MoveWhileBorrowed = Rule("SS0005", "Move conflicts with an active borrow");
    public static readonly DiagnosticDescriptor BorrowConflict = Rule("SS0006", "Conflicting borrows");
    public static readonly DiagnosticDescriptor MutationWhileBorrowed = Rule("SS0007", "Mutation conflicts with an active borrow");
    public static readonly DiagnosticDescriptor BorrowEscape = Rule("SS0008", "Borrow escapes its owner");
    public static readonly DiagnosticDescriptor DisposeBorrowed = Rule("SS0009", "Borrowed resource must not be disposed");
    public static readonly DiagnosticDescriptor BorrowAcrossSuspend = Rule("SS0010", "Borrow crosses suspension");
    public static readonly DiagnosticDescriptor OverwriteOwner = Rule("SS0011", "Owned resource is overwritten");
    public static readonly DiagnosticDescriptor ScopeLifetime = Rule("SS0012", "Service outlives its scope");
    public static readonly DiagnosticDescriptor ReadWhileExclusive = Rule("SS0013", "Read conflicts with an exclusive borrow");
    public static readonly DiagnosticDescriptor DisposeWhileBorrowed = Rule("SS0014", "Disposal conflicts with an active borrow");
    public static readonly DiagnosticDescriptor BudgetExceeded = Rule("SS0015", "Ownership analysis budget exhausted");
    public static readonly DiagnosticDescriptor NoSuspend = Rule("SS0016", "Resource must not cross suspension");
    public static readonly DiagnosticDescriptor UnobservedAsyncDisposal = Rule("SS0017", "Asynchronous disposal completion is not observed");
    public static readonly DiagnosticDescriptor InvalidValueTaskConsumption = Rule("SS0048", "ValueTask is consumed more than once");

    public static readonly ImmutableArray<DiagnosticDescriptor> All = ImmutableArray.Create(
        UseAfterDispose, DoubleDispose, ResourceLeak, UseAfterMove, MoveWhileBorrowed, BorrowConflict, MutationWhileBorrowed, BorrowEscape, DisposeBorrowed, BorrowAcrossSuspend, OverwriteOwner, ScopeLifetime, ReadWhileExclusive, DisposeWhileBorrowed, BudgetExceeded, NoSuspend, UnobservedAsyncDisposal, InvalidValueTaskConsumption);

    public static DiagnosticDescriptor Get(string id) => All.First(descriptor => descriptor.Id == id);
}
