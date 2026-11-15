using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// CoreLibManifest intrinsics <runtime-root> <upstream-dir>: the library's [Intrinsic] members whose C# body is not
// what they do (it throws PlatformNotSupportedException, or calls itself: the JIT supplies the code). cs2wasm's
// compiler has to implement each of these that a program reaches.
static class Intrinsics
{
    public static int Run(string root, string upstream)
    {
        var comp = Check.Compile(root, upstream, "System.Private.CoreLib", new Dictionary<string, CSharpCompilation>());
        int n = 0;
        foreach (var tree in comp.SyntaxTrees)
        {
            var model = comp.GetSemanticModel(tree);
            foreach (var decl in tree.GetRoot().DescendantNodes().Where(d => d is MethodDeclarationSyntax or PropertyDeclarationSyntax))
            {
                var attrs = decl is MethodDeclarationSyntax md ? md.AttributeLists : ((PropertyDeclarationSyntax)decl).AttributeLists;
                // (on a property or on one of its accessors)
                var accessorAttrs = decl is PropertyDeclarationSyntax pd && pd.AccessorList != null ? pd.AccessorList.Accessors.SelectMany(a => a.AttributeLists) : [];
                if (!attrs.Concat(accessorAttrs).SelectMany(a => a.Attributes).Any(a => a.Name.ToString() is "Intrinsic" or "IntrinsicAttribute")) continue;
                var sym = model.GetDeclaredSymbol(decl);
                if (sym == null) continue;
                SyntaxNode? body = decl switch
                {
                    MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                    PropertyDeclarationSyntax p => (SyntaxNode?)p.ExpressionBody ?? p.AccessorList,
                    _ => null,
                };
                if (body == null) continue;
                bool throwsPnse = body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any(o => o.Type.ToString().Contains("PlatformNotSupportedException"))
                    || body.DescendantNodes().OfType<ThrowExpressionSyntax>().Any() && body.ToString().Contains("PlatformNotSupported");
                bool recursive = body.DescendantNodes().OfType<SimpleNameSyntax>().Any(id =>
                    model.GetSymbolInfo(id).Symbol is { } s && (SymbolEqualityComparer.Default.Equals(s.OriginalDefinition, sym.OriginalDefinition)
                        || s is IMethodSymbol { AssociatedSymbol: { } a } && SymbolEqualityComparer.Default.Equals(a.OriginalDefinition, sym.OriginalDefinition)));
                if (!throwsPnse && !recursive) continue;
                n++;
                Console.WriteLine($"{(throwsPnse ? "pnse" : "self")} {sym.ContainingType.ToDisplayString()}.{sym.Name} {sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
            }
        }
        Console.Error.WriteLine($"{n} intrinsics with placeholder bodies");
        return 0;
    }
}
