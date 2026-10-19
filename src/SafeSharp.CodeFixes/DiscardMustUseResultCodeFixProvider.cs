using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace SafeSharp.CodeFixes;

/// <summary>Acknowledges SS0024 results without changing evaluation order or assigning to a user-declared underscore.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DiscardMustUseResultCodeFixProvider)), Shared]
public sealed class DiscardMustUseResultCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "SS0024";
    private const string EquivalenceKey = "SafeSharp.AcknowledgeUnusedResult";
    private static readonly ImmutableArray<string> DiagnosticIds = ImmutableArray.Create(DiagnosticId);

    public override ImmutableArray<string> FixableDiagnosticIds => DiagnosticIds;

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var cancellationToken = context.CancellationToken;
        var root = await context.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null) return;
        foreach (var diagnostic in context.Diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (diagnostic.Id != DiagnosticId || !root.FullSpan.Contains(diagnostic.Location.SourceSpan)) continue;
            var statement = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
                .AncestorsAndSelf().OfType<ExpressionStatementSyntax>().FirstOrDefault();
            if (statement is null) continue;
            var resultType = semanticModel.GetTypeInfo(statement.Expression, cancellationToken).Type;
            if (resultType is null || resultType.TypeKind == TypeKind.Error || resultType.SpecialType == SpecialType.System_Void) continue;
            var options = context.Document.Project.ParseOptions as CSharpParseOptions;
            var useDiscard = options is not null && options.LanguageVersion >= LanguageVersion.CSharp7
                && !semanticModel.LookupSymbols(statement.SpanStart, name: "_").Any();
            // A type named @var disables contextual type inference. Anonymous types cannot be spelled explicitly.
            if (!useDiscard && resultType.IsAnonymousType && semanticModel.LookupNamespacesAndTypes(statement.SpanStart, name: "var").Any()) continue;
            context.RegisterCodeFix(CodeAction.Create("Acknowledge unused result",
                token => ApplyAsync(context.Document, statement, useDiscard, resultType, token), EquivalenceKey), diagnostic);
        }
    }

    private static async Task<Document> ApplyAsync(Document document, ExpressionStatementSyntax statement, bool useDiscard, ITypeSymbol resultType, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null) return document;
        var expression = statement.Expression.WithoutLeadingTrivia();
        StatementSyntax replacement;
        if (useDiscard)
        {
            // A plain IdentifierName("_") lacks the contextual token kind and binds as an undeclared variable
            // in the live changed syntax tree. Re-parsing its text would hide that IDE-only correctness bug.
            var discard = SyntaxFactory.IdentifierName(SyntaxFactory.Identifier(default, SyntaxKind.UnderscoreToken, "_", "_", default));
            var assignment = SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, discard, expression)
                .WithLeadingTrivia(statement.Expression.GetLeadingTrivia());
            replacement = statement.WithExpression(assignment);
        }
        else
        {
            var name = UniqueLocalName(root, statement.SpanStart, cancellationToken);
            var type = resultType.IsAnonymousType
                ? SyntaxFactory.IdentifierName("var")
                : SyntaxFactory.ParseTypeName(resultType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                    SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)));
            var declaration = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(type,
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(name)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(expression)))))
                .WithSemicolonToken(statement.SemicolonToken)
                .WithLeadingTrivia(statement.GetLeadingTrivia())
                .WithTrailingTrivia(statement.GetTrailingTrivia());
            replacement = statement.Parent is BlockSyntax || statement.Parent is SwitchSectionSyntax || statement.Parent is GlobalStatementSyntax
                ? declaration
                : SyntaxFactory.Block(declaration.WithoutLeadingTrivia().WithoutTrailingTrivia())
                    .WithLeadingTrivia(statement.GetLeadingTrivia()).WithTrailingTrivia(statement.GetTrailingTrivia());
        }
        replacement = replacement.WithAdditionalAnnotations(Formatter.Annotation);
        var changed = document.WithSyntaxRoot(root.ReplaceNode(statement, replacement));
        return await Formatter.FormatAsync(changed, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string UniqueLocalName(SyntaxNode root, int sourcePosition, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in root.DescendantTokens())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.IsKind(SyntaxKind.IdentifierToken)) names.Add(token.ValueText);
        }
        // Distinct original spans produce distinct names even when the IDE computes BatchFixer changes concurrently.
        var stem = "unusedResult" + sourcePosition.ToString(CultureInfo.InvariantCulture);
        var candidate = stem;
        var suffix = 1;
        while (names.Contains(candidate)) candidate = stem + "_" + (suffix++).ToString(CultureInfo.InvariantCulture);
        return candidate;
    }
}
