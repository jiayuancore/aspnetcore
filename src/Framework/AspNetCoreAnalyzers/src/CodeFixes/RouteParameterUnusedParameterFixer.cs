// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Fixers;

[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public class RouteParameterUnusedParameterFixer : CodeFixProvider
{
    private static readonly TypeSyntax DefaultType = SyntaxFactory.ParseTypeName("string");

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(
        DiagnosticDescriptors.RoutePatternUnusedParameter.Id);

    public sealed override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public sealed override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (diagnostic.Properties.TryGetValue("RouteParameterName", out var routeParameterName) &&
                diagnostic.Properties.TryGetValue("RouteParameterPolicy", out var routeParameterPolicy) &&
                diagnostic.Properties.TryGetValue("RouteParameterIsOptional", out var routeParameterIsOptional))
            {
                context.RegisterCodeFix(
                    CodeAction.Create($"Add parameter '{routeParameterName}'",
                        cancellationToken => AddRouteParameterAsync(diagnostic, context.Document, routeParameterName, routeParameterPolicy, Convert.ToBoolean(routeParameterIsOptional, CultureInfo.InvariantCulture), cancellationToken),
                        equivalenceKey: $"{DiagnosticDescriptors.RoutePatternUnusedParameter.Id}-{routeParameterName}"),
                    diagnostic);
            }
        }

        return Task.CompletedTask;
    }

    private static async Task<Document> AddRouteParameterAsync(
        Diagnostic diagnostic, Document document, string routeParameterName, string routeParameterPolicy, bool routeParameterIsOptional, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root == null)
        {
            return document;
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel == null)
        {
            return document;
        }

        if (!WellKnownTypes.TryGetOrCreate(semanticModel.Compilation, out var wellKnownTypes))
        {
            return document;
        }

        var param = root.FindNode(diagnostic.Location.SourceSpan);

        var context = RoutePatternUsageDetector.BuildContext(param.GetFirstToken(), semanticModel, wellKnownTypes, cancellationToken);

        // Check that the route is used in a context with a method, e.g. attribute on an action or Map method.
        if (context.MethodSyntax == null)
        {
            return document;
        }

        var resolvedType = CalculateTypeFromPolicy(routeParameterPolicy);
        if (routeParameterIsOptional)
        {
            resolvedType = SyntaxFactory.NullableType(resolvedType);
        }

        // After fix, navigate to type with CodeAction_Navigation.
        var type = resolvedType.WithAdditionalAnnotations(new SyntaxAnnotation("CodeAction_Navigation"));
        var newParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(routeParameterName)).WithType(type);
        var updatedMethod = context.MethodSyntax switch
        {
            BaseMethodDeclarationSyntax methodSyntax => (SyntaxNode)methodSyntax.AddParameterListParameters(newParameter),
            ParenthesizedLambdaExpressionSyntax lambdaExpressionSyntax => lambdaExpressionSyntax.AddParameterListParameters(newParameter),
            _ => throw new InvalidOperationException($"Unexpected method syntax: {context.MethodSyntax.GetType().FullName}")
        };

        // Update document.
        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
        var updatedSyntaxTree = syntaxTree.GetRoot(cancellationToken).ReplaceNode(context.MethodSyntax, updatedMethod);
        return document.WithSyntaxRoot(updatedSyntaxTree);
    }

    private static TypeSyntax CalculateTypeFromPolicy(string? routeParameterPolicy)
    {
        if (routeParameterPolicy == null)
        {
            return DefaultType;
        }

        string? resolvedName = null;
        foreach (var policy in routeParameterPolicy.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // Match policy to a type.
            var typeName = policy switch
            {
                "int" => "int",
                "long" => "long",
                "bool" => "bool",
                "datetime" => "System.DateTime",
                "decimal" => "System.Decimal",
                "double" => "double",
                "float" => "float",
                "guid" => "System.Guid",
                _ => null
            };

            if (typeName != null)
            {
                // Route has conflicting policies, e.g. int and decimal. Default to string.
                if (resolvedName != null && typeName != resolvedName)
                {
                    return DefaultType;
                }

                resolvedName = typeName;
            }
        }

        return resolvedName != null ? SyntaxFactory.ParseTypeName(resolvedName) : DefaultType;
    }
}