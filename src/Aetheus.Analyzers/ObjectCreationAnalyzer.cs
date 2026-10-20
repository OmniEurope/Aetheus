// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Aetheus.Analyzers;

/// <summary> An analyzer with one rule that looks at every object construction, written <c>new
/// T(...)</c> or <c>new(...)</c>. The registration is the same for each of them, so it is written
/// once here and an analyzer only says what it reports. </summary>
public abstract class ObjectCreationAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The single rule this analyzer reports.</summary>
    protected abstract DiagnosticDescriptor Descriptor { get; }

    public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Descriptor];

    public sealed override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeCreation,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.ImplicitObjectCreationExpression);
        RegisterFurtherActions(context);
    }

    /// <summary>Called for each construction in analyzed (non generated) code.</summary>
    protected abstract void AnalyzeCreation(SyntaxNodeAnalysisContext context);

    /// <summary>For an analyzer that also looks at other syntax, such as member accesses on the type.</summary>
    protected virtual void RegisterFurtherActions(AnalysisContext context)
    {
    }
}
