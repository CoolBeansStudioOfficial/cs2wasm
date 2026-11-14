using System.Text;
using Microsoft.CodeAnalysis;

namespace Cs2Wasm.Compiler;

/// <summary>Stack frame names in .NET's StackTrace format (section 6.3).</summary>
internal sealed partial class Driver
{
    static bool IsHiddenFrame(IMethodSymbol m)
    {
        var ns = m.ContainingNamespace?.ToDisplayString() ?? "";
        return ns.StartsWith("Cs2Wasm.Runtime", StringComparison.Ordinal);
    }

    /// <summary>"Namespace.Type`1.Method[T](Int32 x, String&amp; s)" from the method's definition.</summary>
    public static string FrameName(IMethodSymbol m)
    {
        var def = m.OriginalDefinition;
        if (def.ReducedFrom != null) def = def.ReducedFrom;
        var sb = new StringBuilder();
        AppendTypeFull(sb, def.ContainingType);
        sb.Append('.').Append(def.MetadataName.Split('`')[0]);
        if (def.TypeParameters.Length > 0) sb.Append('[').Append(string.Join(",", def.TypeParameters.Select(t => t.Name))).Append(']');
        AppendParams(sb, def.Parameters);
        return sb.ToString();
    }

    internal static string NestedFrameName(FunctionCompiler owner, IMethodSymbol sym, string suffix)
    {
        var outer = owner.OuterMethod;
        var sb = new StringBuilder();
        AppendTypeFull(sb, outer.ContainingType);
        sb.Append(".<").Append(outer.MetadataName).Append('>').Append(suffix);
        AppendParams(sb, sym.Parameters);
        return sb.ToString();
    }

    static void AppendTypeFull(StringBuilder sb, INamedTypeSymbol t)
    {
        t = t.OriginalDefinition;
        if (t.ContainingType != null) { AppendTypeFull(sb, t.ContainingType); sb.Append('.'); }
        else if (t.ContainingNamespace is { IsGlobalNamespace: false } ns) sb.Append(ns.ToDisplayString()).Append('.');
        sb.Append(t.MetadataName);
    }

    static void AppendParams(StringBuilder sb, System.Collections.Immutable.ImmutableArray<IParameterSymbol> ps)
    {
        sb.Append('(');
        for (int i = 0; i < ps.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(ShortTypeName(ps[i].Type));
            if (ps[i].RefKind != RefKind.None) sb.Append('&');
            sb.Append(' ').Append(ps[i].Name);
        }
        sb.Append(')');
    }

    static string ShortTypeName(ITypeSymbol t) => t switch
    {
        IArrayTypeSymbol a => ShortTypeName(a.ElementType) + "[" + new string(',', a.Rank - 1) + "]",
        IPointerTypeSymbol p => ShortTypeName(p.PointedAtType) + "*",
        ITypeParameterSymbol tp => tp.Name,
        INamedTypeSymbol n => n.OriginalDefinition.MetadataName,
        _ => t.Name,
    };
}
