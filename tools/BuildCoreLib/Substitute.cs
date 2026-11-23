using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

/// <summary>The manifest's `substitute method` lines (the trimmer's substitutions for il2wasm's features), applied to
/// the source: the method's body becomes what the compiler's FunctionCompiler.EmitSubstitutedBody emits for it.
/// <list type="bullet">
/// <item>`=>Type.Method`: a call to that boundary method with the same arguments, the instance first;</item>
/// <item>`true`/`false`/a number: that constant returned;</item>
/// <item>empty: an empty body (void), else `default`.</item>
/// </list>
/// `substitute field` lines are not source changes: they are kept in il2wasm.substitutions.txt for the front end's
/// folding (Folding.cs reads them for `static readonly` fields only).</summary>
static class Substitute
{
    public static CSharpCompilation Apply(CSharpCompilation comp, List<string[]> subs, CSharpParseOptions po)
    {
        var changes = new Dictionary<SyntaxTree, List<TextChange>>();
        var missing = new List<string>();
        foreach (var p in subs)
        {
            if (p[1] != "method") continue;
            var (typeName, sig, value) = (p[2], p[3], p[4]);
            var type = comp.GetTypeByMetadataName(typeName.Replace('/', '+'));
            var methods = type?.GetMembers().OfType<IMethodSymbol>().Where(m => Sig(m) == sig).ToList() ?? [];
            if (methods.Count == 0) { missing.Add(typeName + " " + sig); continue; }
            foreach (var m0 in methods)
            {
                var m = m0.PartialImplementationPart ?? m0;
                var refs = m.DeclaringSyntaxReferences;
                if (refs.Length != 1) throw new ToolException($"substitution {typeName} {sig}: {refs.Length} declarations");
                var node = refs[0].GetSyntax();
                var body = Body(comp, m, value);
                var change = node switch
                {
                    BaseMethodDeclarationSyntax { Body: { } b } => new TextChange(b.Span, body),
                    BaseMethodDeclarationSyntax { ExpressionBody: { } e } bm => new TextChange(TextSpan.FromBounds(e.Span.Start, bm.SemicolonToken.Span.End), body),
                    AccessorDeclarationSyntax { Body: { } b } => new TextChange(b.Span, body),
                    AccessorDeclarationSyntax { ExpressionBody: { } e } a => new TextChange(TextSpan.FromBounds(e.Span.Start, a.SemicolonToken.Span.End), body),
                    // a get-only auto-property (with its initializer): a property whose getter is the body
                    AccessorDeclarationSyntax { Body: null, ExpressionBody: null, Parent.Parent: PropertyDeclarationSyntax { AccessorList.Accessors.Count: 1 } pd } =>
                        new TextChange(TextSpan.FromBounds(pd.AccessorList!.Span.Start, pd.Initializer != null ? pd.SemicolonToken.Span.End : pd.AccessorList.Span.End), "{ get " + body + " }"),
                    ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax pd } e => new TextChange(TextSpan.FromBounds(e.Span.Start, pd.SemicolonToken.Span.End), "{ get " + body + " }"),
                    _ => throw new ToolException($"substitution {typeName} {sig}: no body to replace ({node.Kind()}{(m.IsExtern ? ", extern" : "")})"),
                };
                if (!changes.TryGetValue(node.SyntaxTree, out var list)) changes[node.SyntaxTree] = list = [];
                list.Add(change);
            }
        }
        if (missing.Count > 0) throw new ToolException("substitutions that match no method:\n  " + string.Join("\n  ", missing));
        foreach (var (tree, list) in changes)
        {
            // (a line given twice, or two lines for one member: the same change once; different ones are an error)
            var distinct = list.Distinct().OrderBy(c => c.Span.Start).ToList();
            for (int i = 1; i < distinct.Count; i++)
                if (distinct[i].Span.Start < distinct[i - 1].Span.End)
                    throw new ToolException($"two different substitutions for one body in {tree.FilePath}: '{distinct[i - 1].NewText}' and '{distinct[i].NewText}'");
            var text = tree.GetText().WithChanges(distinct);
            comp = comp.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(text, po, tree.FilePath));
        }
        return comp;
    }

    static string Body(CSharpCompilation comp, IMethodSymbol m, string value)
    {
        if (value.StartsWith("=>"))
        {
            // (the boundary method takes the same values; where it declares an enum's underlying type for an enum,
            // as the compiler passed it, the value is converted)
            int dot = value.LastIndexOf('.');
            int n = m.Parameters.Length + (m.IsStatic ? 0 : 1);
            var targets = comp.GetTypeByMetadataName(value[2..dot])?.GetMembers(value[(dot + 1)..]).OfType<IMethodSymbol>().Where(t => t.Parameters.Length == n).ToList() ?? [];
            if (targets.Count != 1) throw new ToolException($"substitution {m}: {targets.Count} boundary methods {value[2..]} with {n} parameters");
            var tps = targets[0].Parameters;
            int k = m.IsStatic ? 0 : 1;
            var args = (m.IsStatic ? [] : new[] { "this" }).Concat(m.Parameters.Select((p, i) => p.RefKind switch
            {
                RefKind.Ref => "ref " + Id(p.Name),
                RefKind.Out => "out " + Id(p.Name),
                RefKind.In or RefKind.RefReadOnlyParameter => "in " + Id(p.Name),
                _ when p.Type.TypeKind == TypeKind.Enum && !SymbolEqualityComparer.Default.Equals(p.Type, tps[i + k].Type)
                    => "(" + tps[i + k].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")" + Id(p.Name),
                _ => Id(p.Name),
            }));
            var call = "global::" + value[2..] + "(" + string.Join(", ", args) + ")";
            return m.ReturnsVoid ? "{ " + call + "; }" : "{ return " + call + "; }";
        }
        if (m.ReturnsVoid) return value == "" ? "{ }" : throw new ToolException($"substitution of void {m}: value '{value}'");
        if (m.Parameters.Any(p => p.RefKind == RefKind.Out)) throw new ToolException($"substitution of {m}: an out parameter left unassigned");
        return "{ return " + (value == "" ? "default" : value) + "; }";
    }

    static string Id(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    /// <summary>A method's signature as the manifest writes it (Library.Substitution's).</summary>
    static string Sig(IMethodSymbol def) =>
        (def.ReturnsVoid ? "System.Void" : IlName(def.ReturnType)) + " " + def.MetadataName + "(" + string.Join(",", def.Parameters.Select(p => IlName(p.Type) + (p.RefKind != RefKind.None ? "&" : ""))) + ")";

    /// <summary>A type's name as the trimmer's files write it (Outer/Inner, System.Int32): Library.IlName.</summary>
    static string IlName(ITypeSymbol t) => t switch
    {
        IArrayTypeSymbol a => IlName(a.ElementType) + "[" + new string(',', a.Rank - 1) + "]",
        IPointerTypeSymbol p => IlName(p.PointedAtType) + "*",
        INamedTypeSymbol { ContainingType: { } outer } n => IlName(outer) + "/" + n.MetadataName,
        INamedTypeSymbol n => (n.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : "") + n.MetadataName,
        _ => t.Name,
    };
}
