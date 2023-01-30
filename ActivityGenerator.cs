using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Text;

namespace InstrumentationGenerator;

[Generator]
public class ActivityGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // For debugging.
        //if (!System.Diagnostics.Debugger.IsAttached)
        //{
        //    System.Diagnostics.Debugger.Launch();
        //}

        // define the execution pipeline here via a series of transformations:
        IncrementalValuesProvider<ClassDeclarationSyntax> classDeclarations = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (s,_) => IsGenerationRequired(s),
            transform: static (ctx, _) => GetSemanticTargetForGeneration(ctx)).Where(static m => m is not null);

        IncrementalValueProvider<(Compilation, ImmutableArray<ClassDeclarationSyntax>)> compilationAndClasses
    = context.CompilationProvider.Combine(classDeclarations.Collect());

        context.RegisterSourceOutput(compilationAndClasses,
            (spc, source) => Execute(source.Item1, source.Item2, spc));
        
        static bool IsGenerationRequired(SyntaxNode syntaxNode) => syntaxNode is ClassDeclarationSyntax classDeclarationSyntax &&
            classDeclarationSyntax.AttributeLists.Count > 0 &&
            classDeclarationSyntax.AttributeLists.Any(static a => a.Attributes.Any(static a => a.Name.ToString() == "Instrumentation"));

        static ClassDeclarationSyntax GetSemanticTargetForGeneration(GeneratorSyntaxContext context) => (ClassDeclarationSyntax)context.Node;
    }

    private void Execute(Compilation compilation, ImmutableArray<ClassDeclarationSyntax> classes, SourceProductionContext spc)
    {
        if (classes.IsDefaultOrEmpty)
        {
            return;
        }

        IEnumerable<ClassDeclarationSyntax> distinctClasses = classes.Distinct();
        foreach (var decoratedClass in distinctClasses)
        {
            // The class should be abstract based on the builder pattern we follow.
            if (!decoratedClass.Modifiers.Any(static m => m.IsKind(SyntaxKind.AbstractKeyword)))
            {
                // We can't generate code for this class.
                continue;
            }

            var model = compilation.GetSemanticModel(decoratedClass.SyntaxTree);

            var classSymbol = model.GetDeclaredSymbol(decoratedClass);
            if (classSymbol is null)
            {
                // We can't generate code for this class.
                continue;
            }

            var methodList = new List<MethodDeclarationSyntax>();
            var originalUsings = new List<UsingDirectiveSyntax>();
            var syntaxTrees = classSymbol.DeclaringSyntaxReferences;
            var checkList = new List<string>();
            foreach (var item in syntaxTrees)
            {
                var unit = item.SyntaxTree.GetCompilationUnitRoot();
                var allMethods = unit.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Where(j => j.Modifiers.Any(SyntaxKind.PublicKeyword) &&
                    j.Modifiers.Any(SyntaxKind.VirtualKeyword) &&
                    !j.Modifiers.Any(SyntaxKind.StaticKeyword))
                    .ToList();
                methodList.AddRange(allMethods);

                var allUsings = unit.DescendantNodes().OfType<UsingDirectiveSyntax>().ToList();
                foreach (var ud in allUsings)
                {
                    var usingName = ud.Name.GetText().ToString().Trim();
                    if(!checkList.Contains(usingName))
                    {
                        originalUsings.Add(ud);
                        checkList.Add(usingName);
                    }
                }
            }

            // Create a decorator class for the class we're decorating.
            // Header
            var classBuilder = new StringBuilder();
            var root = decoratedClass.SyntaxTree.GetCompilationUnitRoot();
            var header = $@"// Copyright {DateTime.UtcNow.Year} Google LLC
//
// Licensed under the Apache License, Version 2.0 (the ""License"").
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at 
//
// https://www.apache.org/licenses/LICENSE-2.0 
//
// Unless required by applicable law or agreed to in writing, software 
// distributed under the License is distributed on an ""AS IS"" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and 
// limitations under the License.";
            classBuilder.Append(header);
            classBuilder.AppendLine();
            // usings
            originalUsings.Distinct().ToList().ForEach(u => classBuilder.AppendLine(u.ToString()));
            // namespace
            var namespaceDeclaration = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().First().Name.ToString();
            classBuilder.AppendLine();
            classBuilder.AppendLine($"namespace {namespaceDeclaration};");
            // sealed class
            classBuilder.AppendLine();
            string decoratedClassName = decoratedClass.Identifier.Text;
            string decoratorClassName = $"Instrumented{decoratedClassName}Impl";
            string implClassName = $"{decoratedClassName}Impl";
            string implVariableName = $"_impl";
            classBuilder.AppendLine("/// <summary>");
            classBuilder.AppendLine($"/// The instrumented decorated class for {decoratedClassName}.");
            classBuilder.AppendLine("/// </summary>");
            classBuilder.AppendLine($"public sealed partial class {decoratorClassName} : {decoratedClassName}");
            classBuilder.AppendLine("{");
            // fields
            // _impl will be the name of implementor. 
            classBuilder.AppendLine($"private readonly {implClassName} {implVariableName};");
            // Get virtual properties
            classBuilder.AppendLine();
            var properties = root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Where(j => j.Modifiers.Any(k => k.IsKind(SyntaxKind.VirtualKeyword)));
            foreach (var item in properties)
            {
                string name = item.Identifier.Text;
                classBuilder.AppendLine("/// <inheritdoc/>");
                classBuilder.AppendLine($"public override {item.Type.GetText().ToString().Trim()} {name} => {implVariableName}.{name};");
                classBuilder.AppendLine();
            }

            // constructor
            classBuilder.AppendLine("/// <summary>");
            classBuilder.AppendLine($"/// Initialize a new instance of <see cref=\"{decoratorClassName}\"/> class.");
            classBuilder.AppendLine("/// </summary>");
            classBuilder.AppendLine($"/// <param name=\"client\">The {decoratedClassName}.</param>");
            classBuilder.AppendLine($"public {decoratorClassName}({implClassName} client) => {implVariableName} = client;");
            classBuilder.AppendLine();

            // methods
            foreach (var item in methodList.Distinct())
            {
                string name = item.Identifier.Text;
                var parameters = item.DescendantNodes().OfType<ParameterListSyntax>().FirstOrDefault();
                var signatureAndInvocation = GetSignatureAndInvocation(parameters);
                classBuilder.AppendLine("/// <inheritdoc/>");
                classBuilder.AppendLine($"public override {item.ReturnType.GetText().ToString().Trim()} {name}{signatureAndInvocation.Item1}");
                classBuilder.AppendLine("{");
                classBuilder.AppendLine($" using var activity = TypeActivitySource<{decoratedClassName}>.ActivitySource?.StartActivity();");
                var invocation = signatureAndInvocation.Item2;
                var list = invocation.Split(',');
                foreach (var tag in list)
                {
                    var trimmedTag = tag.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmedTag))
                    {
                        classBuilder.AppendLine($"_ = activity?.SetTag(nameof({trimmedTag}), {trimmedTag});");
                    }
                }

                if (item.ReturnType.ToString().Trim() == "void")
                {
                    classBuilder.AppendLine($"{implVariableName}.{name}({invocation});");
                }
                else
                {
                    classBuilder.AppendLine($"return {implVariableName}.{name}({invocation});");
                }

                classBuilder.AppendLine("}");
                
                classBuilder.AppendLine();
            }

            // Format the class.
            // Add it to the context.
            classBuilder.AppendLine("}");
            // Add the source code to the compilation
            spc.AddSource($"{decoratorClassName}.g.cs", classBuilder.ToString());

            static (string, string) GetSignatureAndInvocation(ParameterListSyntax pls)
            {
                var signature = pls?.ToString();
                var parameters = pls?.DescendantNodes().OfType<ParameterSyntax>().ToList();
                var invocationBuilder = new StringBuilder();
                foreach (var item in parameters)
                {
                    invocationBuilder.Append($"{item.Identifier.Text}, ");
                }

                var invocation = invocationBuilder.ToString().TrimEnd(',', ' ');
                return (signature, invocation);
            }
        }
    }
}
