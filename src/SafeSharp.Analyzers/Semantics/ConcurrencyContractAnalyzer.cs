using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SafeSharp.Analyzers.Semantics;

/// <summary>Checks declared transfer/sharing composition and visible callback captures, without claiming whole-program race freedom.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConcurrencyContractAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
        ConcurrencyDiagnostics.InvalidConcurrencyContract, ConcurrencyDiagnostics.UnsafeConcurrencyCapture,
        ConcurrencyDiagnostics.IncompleteContractAnalysis, ConcurrencyDiagnostics.MissingSynchronizationGuard);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var contracts = new ConcurrencyContracts(start.Compilation);
            var synchronization = new SynchronizationContracts(start.Compilation);
            if (synchronization.Available)
            {
                start.RegisterOperationBlockAction(block => SynchronizationAnalysis.Analyze(block, synchronization, contracts));
                start.RegisterSymbolAction(symbol => ValidateSynchronization(symbol, synchronization), SymbolKind.Field, SymbolKind.Property, SymbolKind.Method, SymbolKind.NamedType);
            }
            if (!contracts.Available) return;
            start.RegisterSymbolAction(symbol => AnalyzeType(symbol, contracts), SymbolKind.NamedType);
            start.RegisterSymbolAction(symbol => AnalyzeMethod(symbol, contracts), SymbolKind.Method);
            start.RegisterSyntaxNodeAction(syntax => AnalyzeGenericName(syntax, contracts), SyntaxKind.GenericName);
            start.RegisterOperationAction(operation => AnalyzeInvocation(operation, contracts), OperationKind.Invocation);
        });
    }

    private static void ValidateSynchronization(SymbolAnalysisContext context, SynchronizationContracts contracts)
    {
        var symbol = context.Symbol;
        if (symbol.IsImplicitlyDeclared || symbol is INamedTypeSymbol { TypeKind: not TypeKind.Delegate }) return;
        var declarationMethod = symbol as IMethodSymbol ?? (symbol as INamedTypeSymbol)?.DelegateInvokeMethod;
        var declarations = declarationMethod is { } method
            ? contracts.Declared(method).AddRange(contracts.Returns(method)) : contracts.Members(symbol);
        foreach (var contract in declarations)
            if (contract.Error is not null) context.ReportDiagnostic(Diagnostic.Create(ConcurrencyDiagnostics.MissingSynchronizationGuard,
                contract.Location, symbol.Name, contract.Text, contract.Error));
        if (symbol is IPropertySymbol property)
            foreach (var inherited in contracts.PropertyBases(property))
                foreach (var declared in contracts.Members(property).Where(contract => contract.Error is null))
                    if (!contracts.Members(inherited).Any(contract => contract.SamePath(declared)))
                        context.ReportDiagnostic(Diagnostic.Create(ConcurrencyDiagnostics.MissingSynchronizationGuard,
                            declared.Location, property.Name, declared.Text, "an implementation cannot strengthen an inherited property guard"));
        if (symbol is not IMethodSymbol implementation) return;
        foreach (var inherited in contracts.Bases(implementation))
            foreach (var declared in contracts.Returns(implementation).Where(contract => contract.Error is null))
                if (!contracts.Returns(inherited).Any(contract => contract.SamePath(declared)))
                    context.ReportDiagnostic(Diagnostic.Create(ConcurrencyDiagnostics.MissingSynchronizationGuard,
                        declared.Location, implementation.Name, declared.Text, "an implementation cannot add a guard requirement to an inherited return contract"));
        foreach (var inherited in contracts.Bases(implementation))
        foreach (var declared in contracts.Declared(implementation).Where(contract => contract.Error is null))
        {
            if (contracts.Required(inherited).Any(contract => contract.SamePath(declared) && (contract.Write || !declared.Write))) continue;
            context.ReportDiagnostic(Diagnostic.Create(ConcurrencyDiagnostics.MissingSynchronizationGuard,
                declared.Location, implementation.Name, declared.Text, "an implementation cannot strengthen an inherited lock precondition"));
        }
    }

    private static void AnalyzeType(SymbolAnalysisContext context, ConcurrencyContracts contracts)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.IsImplicitlyDeclared || type.Locations.FirstOrDefault(location => location.IsInSource) is not { SourceTree: { } tree } location) return;
        var analysis = new ConcurrencyTypeAnalysis(contracts, ConcurrencyContracts.MaximumSteps(context.Options.AnalyzerConfigOptionsProvider, tree), context.CancellationToken);
        foreach (var capability in new[] { ConcurrencyCapability.Send, ConcurrencyCapability.Sync })
        {
            if (!contracts.HasCapability(type, capability)) continue;
            var check = analysis.Check(type, capability);
            if (!check.Satisfied && !analysis.BudgetExceeded) ReportDeclaration(context.ReportDiagnostic, type.ToDisplayString(), location, capability, check);
        }
        foreach (var parameter in type.TypeParameters) ValidateParameterConstraint(parameter, contracts, analysis, context.ReportDiagnostic);
        if (analysis.BudgetExceeded) ReportBudget(context.ReportDiagnostic, location, type.Name, "concurrency composition");
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context, ConcurrencyContracts contracts)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.IsImplicitlyDeclared || method.Locations.FirstOrDefault(location => location.IsInSource) is not { SourceTree: { } tree } location) return;
        var analysis = new ConcurrencyTypeAnalysis(contracts, ConcurrencyContracts.MaximumSteps(context.Options.AnalyzerConfigOptionsProvider, tree), context.CancellationToken);
        foreach (var parameter in method.Parameters)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var transfer = contracts.HasBoundary(parameter, "TransfersToThread");
            var share = contracts.HasBoundary(parameter, "SharesAcrossThreads");
            if (!transfer && !share) continue;
            if (parameter.Type.TypeKind == TypeKind.Delegate && !(transfer && share)) continue;
            var reason = transfer && share ? "a callback parameter cannot declare both transfer and sharing boundaries" : "a callback boundary requires a delegate parameter";
            ReportDeclaration(context.ReportDiagnostic, method.ToDisplayString(), parameter.Locations.FirstOrDefault() ?? location,
                transfer ? ConcurrencyCapability.Send : ConcurrencyCapability.Sync, new ConcurrencyCheck(reason, parameter.Name, parameter));
        }
        foreach (var parameter in method.TypeParameters) ValidateParameterConstraint(parameter, contracts, analysis, context.ReportDiagnostic);
        if (analysis.BudgetExceeded) ReportBudget(context.ReportDiagnostic, location, method.Name, "generic concurrency constraint");
    }

    private static void ValidateParameterConstraint(ITypeParameterSymbol parameter, ConcurrencyContracts contracts,
        ConcurrencyTypeAnalysis analysis, Action<Diagnostic> report)
    {
        foreach (var capability in new[] { ConcurrencyCapability.Send, ConcurrencyCapability.Sync })
        {
            if (!contracts.Has(parameter, capability.ToString())) continue;
            var check = analysis.Check(parameter, capability);
            if (!check.Satisfied && !analysis.BudgetExceeded && parameter.Locations.FirstOrDefault(location => location.IsInSource) is { } location)
                ReportDeclaration(report, parameter.Name, location, capability, check);
        }
    }

    private static void AnalyzeGenericName(SyntaxNodeAnalysisContext context, ConcurrencyContracts contracts)
    {
        var syntax = (GenericNameSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(syntax, context.CancellationToken).Symbol is not INamedTypeSymbol type) return;
        CheckArguments(type.TypeParameters, type.TypeArguments, syntax.TypeArgumentList.Arguments.Select(argument => argument.GetLocation()).ToArray(),
            type.ToDisplayString(), syntax.GetLocation(), contracts, context.Options, context.CancellationToken, context.ReportDiagnostic);
    }

    private static void CheckArguments(ImmutableArray<ITypeParameterSymbol> parameters, ImmutableArray<ITypeSymbol> arguments,
        Location[] argumentLocations, string displayName, Location location, ConcurrencyContracts contracts, AnalyzerOptions options,
        System.Threading.CancellationToken cancellationToken, Action<Diagnostic> report)
    {
        if (!parameters.Any(parameter => contracts.Requires(parameter, ConcurrencyCapability.Send) || contracts.Requires(parameter, ConcurrencyCapability.Sync)) || location.SourceTree is not { } tree) return;
        var analysis = new ConcurrencyTypeAnalysis(contracts, ConcurrencyContracts.MaximumSteps(options.AnalyzerConfigOptionsProvider, tree), cancellationToken);
        for (var index = 0; index < Math.Min(parameters.Length, arguments.Length); index++)
        foreach (var capability in new[] { ConcurrencyCapability.Send, ConcurrencyCapability.Sync })
        {
            if (!contracts.Requires(parameters[index], capability)) continue;
            var check = analysis.Check(arguments[index], capability);
            if (check.Satisfied || analysis.BudgetExceeded) continue;
            var failure = new ConcurrencyCheck("generic argument '" + arguments[index].ToDisplayString() + "' for '" + parameters[index].Name + "': " + check.Reason,
                parameters[index].Name + " -> " + check.Path, check.Evidence ?? parameters[index]);
            ReportDeclaration(report, displayName, index < argumentLocations.Length ? argumentLocations[index] : location, capability, failure);
        }
        if (analysis.BudgetExceeded) ReportBudget(report, location, displayName, "generic concurrency argument");
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, ConcurrencyContracts contracts)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (method.IsGenericMethod)
        {
            var genericName = invocation.Syntax.DescendantNodesAndSelf().OfType<GenericNameSyntax>().FirstOrDefault(name => name.Identifier.ValueText == method.Name);
            CheckArguments(method.TypeParameters, method.TypeArguments,
                genericName?.TypeArgumentList.Arguments.Select(argument => argument.GetLocation()).ToArray() ?? Array.Empty<Location>(),
                method.ToDisplayString(), invocation.Syntax.GetLocation(), contracts, context.Options, context.CancellationToken, context.ReportDiagnostic);
        }
        var taskRun = contracts.IsTaskRun(method);
        foreach (var argument in invocation.Arguments)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (argument.Parameter is not { } parameter) continue;
            var transfer = contracts.HasBoundary(parameter, "TransfersToThread");
            var sharing = contracts.HasBoundary(parameter, "SharesAcrossThreads");
            var declared = transfer || sharing;
            if (!declared && (!taskRun || parameter.Type.TypeKind != TypeKind.Delegate)) continue;
            if (transfer && sharing || parameter.Type.TypeKind != TypeKind.Delegate) continue; // Declaration diagnostic explains the malformed boundary.
            var capability = transfer ? ConcurrencyCapability.Send : ConcurrencyCapability.Sync;
            var analysis = new ConcurrencyTypeAnalysis(contracts, ConcurrencyContracts.MaximumSteps(context.Options.AnalyzerConfigOptionsProvider, invocation.Syntax.SyntaxTree), context.CancellationToken);
            var captures = new ConcurrencyCaptureAnalysis(contracts, analysis, invocation, includeAnnotatedFields: !declared, capability);
            captures.Resolve(argument.Value);
            foreach (var capture in captures.Captures)
            {
                if (!declared && !contracts.HasExplicitConcurrency(capture.Type)) continue;
                var required = capture.SharedStorage ? ConcurrencyCapability.Sync : capability;
                var check = capture.MutableCell ? new ConcurrencyCheck("the captured variable or static storage is writable; its cell is shared independently of the value's contract", capture.Name, capture.Symbol)
                    : analysis.Check(capture.Type, required);
                if (!check.Satisfied && !analysis.BudgetExceeded)
                    ReportCapture(context.ReportDiagnostic, capture.Name, capture.Location, required, check, invocation, capture.Symbol);
            }
            if (declared && captures.Unresolved && !analysis.BudgetExceeded)
                ReportCapture(context.ReportDiagnostic, argument.Value.Syntax.ToString(), argument.Value.Syntax.GetLocation(), capability,
                    new ConcurrencyCheck("the delegate target and its captured values are unavailable to source analysis", parameter.Name, parameter), invocation, parameter);
            if (analysis.BudgetExceeded) ReportBudget(context.ReportDiagnostic, invocation.Syntax.GetLocation(), method.Name, "concurrency capture");
        }
    }

    private static void ReportDeclaration(Action<Diagnostic> report, string name, Location location, ConcurrencyCapability capability, ConcurrencyCheck check) =>
        report(Diagnostic.Create(ConcurrencyDiagnostics.InvalidConcurrencyContract, location,
            additionalLocations: check.EvidencePath.SelectMany(symbol => symbol.Locations).Where(candidate => candidate.IsInSource).Distinct(), properties: Properties(capability, check),
            messageArgs: new object[] { name, capability.ToString(), check.Reason! }));
    private static void ReportCapture(Action<Diagnostic> report, string name, Location location, ConcurrencyCapability capability,
        ConcurrencyCheck check, IInvocationOperation invocation, ISymbol symbol) => report(Diagnostic.Create(
            ConcurrencyDiagnostics.UnsafeConcurrencyCapture, location,
            additionalLocations: new[] { invocation.Syntax.GetLocation() }.Concat(symbol.Locations.Where(candidate => candidate.IsInSource))
                .Concat(check.EvidencePath.SelectMany(evidence => evidence.Locations).Where(candidate => candidate.IsInSource)).Distinct(),
            properties: Properties(capability, check), messageArgs: new object[] { name, capability.ToString(), check.Reason! }));
    private static ImmutableDictionary<string, string?> Properties(ConcurrencyCapability capability, ConcurrencyCheck check) =>
        ImmutableDictionary<string, string?>.Empty.Add("SafeSharp.ConcurrencyCapability", capability.ToString())
            .Add("SafeSharp.ConcurrencyPath", check.Path).Add("SafeSharp.ConcurrencyReason", check.Reason);
    private static void ReportBudget(Action<Diagnostic> report, Location location, string name, string budget) =>
        report(Diagnostic.Create(ConcurrencyDiagnostics.IncompleteContractAnalysis, location, name, budget));
}
