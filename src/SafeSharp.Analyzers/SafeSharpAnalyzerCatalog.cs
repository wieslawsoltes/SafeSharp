using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Diagnostics;
using SafeSharp.Analyzers.Semantics;

namespace SafeSharp.Analyzers;

/// <summary>Creates independent compiler analyzers for embedding in custom Roslyn hosts.</summary>
public static class SafeSharpAnalyzerCatalog
{
    public static ImmutableArray<DiagnosticAnalyzer> Create() => ImmutableArray.Create<DiagnosticAnalyzer>(
        new OwnershipAnalyzer(), new SafeSharpSemanticAnalyzer(), new BoundarySafetyAnalyzer());
}
