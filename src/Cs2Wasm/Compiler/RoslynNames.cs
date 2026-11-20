using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>The names the C# compiler gives the methods and classes it makes for lambdas and local functions, as
/// stack traces show them: `Program.&lt;&gt;c.&lt;Main&gt;b__9_0()` (a lambda that captures nothing, in the
/// singleton class), `Program.&lt;&gt;c__DisplayClass3_0.&lt;Captured&gt;b__0()` (one that captures, in the class
/// of the innermost scope it captures from), `Program.&lt;Local&gt;g__Div|5_0(Int32 a, &lt;&gt;c__DisplayClass5_0&amp;)`
/// (a local function: its captures in a struct passed by reference). The numbers are the method's position among its
/// type's members, then the lambda's among the method's lambdas and local functions (in source order), or the
/// scope's among the method's scopes that hold captured variables.</summary>
internal sealed partial class Driver
{
    sealed class NestedNames
    {
        public int MethodOrdinal;
        public readonly Dictionary<SyntaxNode, int> Ordinal = [];
        public readonly Dictionary<SyntaxNode, (bool captures, bool usesThis, SyntaxNode? env)> Captures = [];
        public readonly Dictionary<SyntaxNode, int> EnvOrdinal = [];
        public readonly HashSet<SyntaxNode> ConvertedLocalFunctions = [];
    }

    readonly Dictionary<IMethodSymbol, NestedNames?> _nestedNames = new(SymbolEqualityComparer.Default);

    NestedNames? NamesOf(IMethodSymbol outer)
    {
        var def = outer.OriginalDefinition;
        if (_nestedNames.TryGetValue(def, out var n)) return n;
        n = null;
        var body = BodyOperation(this, def);
        if (body != null && def.ContainingType != null)
        {
            n = new NestedNames { MethodOrdinal = def.ContainingType.GetMembers().IndexOf(def) };
            var funcs = body.Descendants().Where(o => o is IAnonymousFunctionOperation or ILocalFunctionOperation).ToList();
            // (a lambda quoted as an expression tree takes its ordinal too)
            for (int i = 0; i < funcs.Count; i++) n.Ordinal[funcs[i].Syntax] = i;
            // the scopes that hold captured variables, in source order
            var envs = new SortedSet<SyntaxNode>(Comparer<SyntaxNode>.Create((a, b) => a.SpanStart != b.SpanStart ? a.SpanStart.CompareTo(b.SpanStart) : b.Span.Length.CompareTo(a.Span.Length)));
            foreach (var f in funcs)
            {
                var inner = f.Syntax;
                bool usesThis = false; SyntaxNode? env = null; bool captures = false;
                foreach (var o in f.Descendants())
                {
                    ISymbol? s = o switch { ILocalReferenceOperation l => l.Local, IParameterReferenceOperation p => p.Parameter, _ => null };
                    if (o is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }) usesThis = true;
                    if (s == null || s.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not { } decl) continue;
                    if (inner.Span.Contains(decl.Span) && decl.SyntaxTree == inner.SyntaxTree) continue; // its own
                    var scope = ScopeOf(decl, s is IParameterSymbol);
                    if (scope == null) continue;
                    captures = true;
                    envs.Add(scope);
                    if (env == null || scope.SpanStart > env.SpanStart || scope.SpanStart == env.SpanStart && scope.Span.Length < env.Span.Length) env = scope;
                }
                n.Captures[inner] = (captures, usesThis, env);
            }
            int k = 0;
            foreach (var e in envs) n.EnvOrdinal[e] = k++;
            // local functions converted to delegates live in their closure's class; the others take it by reference
            foreach (var mr in body.Descendants().OfType<IMethodReferenceOperation>())
                if (mr.Method.MethodKind == MethodKind.LocalFunction && mr.Method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is { } lf)
                    n.ConvertedLocalFunctions.Add(lf);
        }
        _nestedNames[def] = n;
        return n;
    }

    /// <summary>The scope a variable belongs to: the block, loop, using, catch or switch section that declares it;
    /// for a parameter, its method or lambda.</summary>
    static SyntaxNode? ScopeOf(SyntaxNode decl, bool parameter)
    {
        for (var p = decl.Parent; p != null; p = p.Parent)
        {
            // (a parameter shares its method's or lambda's outermost scope: its body)
            if (parameter && p is BaseMethodDeclarationSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax or TypeDeclarationSyntax)
                return p switch
                {
                    BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                    LocalFunctionStatementSyntax l => (SyntaxNode?)l.Body ?? l.ExpressionBody,
                    AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                    AnonymousFunctionExpressionSyntax f => f.Body,
                    _ => null,
                };
            if (!parameter && p is BlockSyntax or ForStatementSyntax or CommonForEachStatementSyntax or UsingStatementSyntax or CatchClauseSyntax or SwitchSectionSyntax
                or ArrowExpressionClauseSyntax or FixedStatementSyntax)
                return p;
            if (!parameter && p is AnonymousFunctionExpressionSyntax af) return af.Body;
            if (p is MemberDeclarationSyntax) return null;
        }
        return null;
    }

    /// <summary>The C# compiler's name for a closure (display) class: `&lt;&gt;c__DisplayClass{method}_{scope}`, the
    /// scope its first variable belongs to; full: nested in the method's type with '+'.</summary>
    internal string? RoslynDisplayName(Display d, bool full)
    {
        if (d.Owner is not FunctionCompiler fc) return null;
        var outer = fc.OuterMethod;
        var names = NamesOf(outer);
        var v = d.Vars.FirstOrDefault(x => x is ILocalSymbol or IParameterSymbol);
        if (names == null || v?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not { } decl) return null;
        var scope = ScopeOf(decl, v is IParameterSymbol);
        if (scope == null || !names.EnvOrdinal.TryGetValue(scope, out int co)) return null;
        var shortName = "<>c__DisplayClass" + names.MethodOrdinal + "_" + co;
        return full ? ClrPath(outer.ContainingType.OriginalDefinition) + "+" + shortName : shortName;
    }

    /// <summary>The frame name of a lambda or local function, as the C# compiler names its method.</summary>
    internal string RoslynNestedFrameName(FunctionCompiler owner, SyntaxNode syntax, IMethodSymbol sym, bool localFunction)
    {
        var outer = owner.OuterMethod;
        var names = NamesOf(outer);
        if (names == null || !names.Ordinal.TryGetValue(syntax, out int lo) || !names.Captures.TryGetValue(syntax, out var cap))
            return NestedFrameName(owner, sym, localFunction ? "g__" + sym.Name + "|0_0" : "b__0");
        int mo = names.MethodOrdinal;
        var sb = new StringBuilder();
        AppendTypeFull(sb, outer.ContainingType);
        string method = outer.MetadataName;
        bool inClosure = cap.captures && cap.env != null && (!localFunction || names.ConvertedLocalFunctions.Contains(syntax));
        if (inClosure) sb.Append(".<>c__DisplayClass").Append(mo).Append('_').Append(names.EnvOrdinal[cap.env!]);
        else if (!cap.captures && !cap.usesThis && !localFunction) sb.Append(".<>c");
        sb.Append(".<").Append(method).Append('>');
        if (localFunction) sb.Append("g__").Append(sym.Name).Append('|');
        else sb.Append("b__");
        if (!inClosure) sb.Append(mo).Append('_');
        sb.Append(lo);
        sb.Append('(');
        for (int i = 0; i < sym.Parameters.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(ShortTypeName(sym.Parameters[i].Type));
            if (sym.Parameters[i].RefKind != RefKind.None) sb.Append('&');
            sb.Append(' ').Append(sym.Parameters[i].Name);
        }
        // a local function's captured variables come in a struct, by reference
        if (localFunction && cap.captures && cap.env != null && !inClosure)
        {
            if (sym.Parameters.Length > 0) sb.Append(", ");
            sb.Append("<>c__DisplayClass").Append(mo).Append('_').Append(names.EnvOrdinal[cap.env]).Append('&');
        }
        sb.Append(')');
        return sb.ToString();
    }
}
