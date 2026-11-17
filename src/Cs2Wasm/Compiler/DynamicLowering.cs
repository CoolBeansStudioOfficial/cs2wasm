using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

/// <summary>`dynamic` (section 8), as the C# compiler lowers it: each dynamic operation is a call site, a static
/// <c>CallSite&lt;D&gt;</c> created once from a C# runtime binder (Microsoft.CSharp's <c>Binder.InvokeMember</c>,
/// <c>GetMember</c>, <c>BinaryOperation</c>, <c>Convert</c>...) with the same flags, argument infos and delegate
/// type Roslyn emits, then invoked through its <c>Target</c>. The sites are written as C# in a generated class of
/// the program (<c>Cs2Wasm.Generated.DynamicSites</c>, one method per site) and compiled with it; the operation
/// compiles to a call of its site's method. The binder runs on the reflection metadata the compiler keeps (the
/// member names the sites use) and binds to expression trees that run on upstream's interpreter.</summary>
internal sealed class DynamicLowering
{
    public const string ClassName = "Cs2Wasm.Generated.DynamicSites";
    const string Ns = "global::Microsoft.CSharp.RuntimeBinder.";
    const string CallSiteT = "global::System.Runtime.CompilerServices.CallSite";

    /// <summary>A site: its method, whether it returns nothing (a discarded result), the member names it binds.</summary>
    public sealed record Site(string Method, bool Void);

    readonly Dictionary<string, Site> _sites = [];
    public readonly HashSet<string> MemberNames = [];
    public readonly List<INamedTypeSymbol> ConstructedTypes = [];
    public bool Any => _sites.Count > 0;

    static string Key(IOperation op, string role) => op.Syntax.SyntaxTree.FilePath + "|" + op.Syntax.Span.Start + "|" + op.Syntax.Span.Length + "|" + op.Kind + "|" + role;
    public Site? Find(IOperation op, string role) => _sites.TryGetValue(Key(op, role), out var s) ? s : null;

    readonly StringBuilder _sb = new();
    int _n;
    CSharpCompilation _comp = null!;
    SemanticModel _model = null!;

    /// <summary>The compilation with the sites' class added (unchanged when the program has no dynamic operation).</summary>
    public static CSharpCompilation Rewrite(CSharpCompilation comp, out DynamicLowering lowering)
    {
        lowering = new DynamicLowering { _comp = comp };
        if (!comp.SyntaxTrees.Any(t => t.GetRoot().DescendantTokens().Any(k => k.ValueText == "dynamic"))) return comp;
        foreach (var tree in comp.SyntaxTrees) lowering.Scan(tree);
        if (!lowering.Any) return comp;
        // [DefaultMember("Item")], which the C# compiler puts on a type that declares an indexer (the binder finds
        // indexers by it): a class carrying each such attribute, for the reflection records to copy
        var indexerNames = comp.SyntaxTrees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<IndexerDeclarationSyntax>()
            .Select(ix => comp.GetSemanticModel(t).GetDeclaredSymbol(ix)?.MetadataName)).OfType<string>().Distinct().ToList();
        var carriers = string.Concat(indexerNames.Select(n => "    [global::System.Reflection.DefaultMemberAttribute(" + Lit(n) + ")] internal static class DefaultMember_" + n + " { }\n"));
        var text = "namespace Cs2Wasm.Generated\n{\n" + carriers + "    internal static class DynamicSites\n    {\n" + lowering._sb + "    }\n}\n";
        if (Environment.GetEnvironmentVariable("CS2WASM_DUMP_DYNAMIC") is { Length: > 0 } dumpTo) File.WriteAllText(dumpTo, text);
        var opts = (CSharpParseOptions)comp.SyntaxTrees.First().Options;
        var gen = CSharpSyntaxTree.ParseText(text, opts, path: "cs2wasm/generated/DynamicSites.cs", encoding: Encoding.UTF8);
        var r = comp.AddSyntaxTrees(gen);
        var errs = r.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree == gen).ToList();
        if (errs.Count > 0) throw new CompileException("dynamic: internal error in a generated call site: " + errs[0].GetMessage() + " in `" + gen.GetText().Lines[errs[0].Location.GetLineSpan().StartLinePosition.Line] + "`");
        return r;
    }

    void Scan(SyntaxTree tree)
    {
        _model = _comp.GetSemanticModel(tree);
        var seen = new HashSet<IOperation>();
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            if (node is not (BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax or GlobalStatementSyntax or ConstructorInitializerSyntax)) continue;
            if (node is ArrowExpressionClauseSyntax { Parent: BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax }) continue;
            if (node is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent.Parent: LocalDeclarationStatementSyntax } }) continue;
            var root = _model.GetOperation(node);
            if (root == null) continue;
            foreach (var op in root.DescendantsAndSelf())
                if (seen.Add(op)) Visit(op);
        }
    }

    static bool IsDyn(ITypeSymbol? t) => t is IDynamicTypeSymbol;
    /// <summary>An operand's type. (The variable of `foreach (var v in d)` over a dynamic d is an object to its symbol
    /// and to the operation tree, while what is done with it is bound as dynamic: the loop's element type says so.)</summary>
    ITypeSymbol? TypeOfOperand(IOperation? o)
    {
        if (o is ILocalReferenceOperation { Local: { Type.SpecialType: SpecialType.System_Object } l })
        {
            if (!_foreachDynamic.TryGetValue(l, out var dt))
                _foreachDynamic[l] = dt = l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ForEachStatementSyntax { Type.IsVar: true } fs
                    && fs.SyntaxTree == _model.SyntaxTree && _model.GetForEachStatementInfo(fs).ElementType is IDynamicTypeSymbol d ? d : null;
            if (dt != null) return dt;
        }
        return o?.Type;
    }
    readonly Dictionary<ILocalSymbol, ITypeSymbol?> _foreachDynamic = new(SymbolEqualityComparer.Default);
    bool Dyn(IOperation? o) => IsDyn(TypeOfOperand(o));

    void Visit(IOperation op)
    {
        switch (op)
        {
            case IDynamicInvocationOperation di: Invocation(di); break;
            case IDynamicMemberReferenceOperation mr when mr.Parent is not IDynamicInvocationOperation { Operation: var callee } || callee != mr:
                if (IsAssignTarget(mr)) break;
                Member(mr); break;
            case IDynamicIndexerAccessOperation ia:
                if (IsAssignTarget(ia)) break;
                Indexer(ia); break;
            case IDynamicObjectCreationOperation oc: Creation(oc); break;
            case ISimpleAssignmentOperation { Target: IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation } sa: Assignment(sa); break;
            case ICompoundAssignmentOperation ca when ca.OperatorMethod == null && (Dyn(ca.Target) || Dyn(ca.Value)): Compound(ca); break;
            case IIncrementOrDecrementOperation inc when inc.OperatorMethod == null && Dyn(inc.Target): IncDec(inc); break;
            case IBinaryOperation b when b.OperatorMethod == null && (Dyn(b.LeftOperand) || Dyn(b.RightOperand)): Binary(b); break;
            case IUnaryOperation u when u.OperatorMethod == null && Dyn(u.Operand): Unary(u); break;
            case IConversionOperation c when Dyn(c.Operand) && c.Type != null && !IsDyn(c.Type) && c.Type.SpecialType != SpecialType.System_Object: Conversion(c); break;
        }
    }

    static bool IsAssignTarget(IOperation op) => op.Parent is IAssignmentOperation a && a.Target == op || op.Parent is IIncrementOrDecrementOperation i && i.Target == op;

    // ---------------- argument infos ----------------
    sealed record Arg(string? ParamType, int Flags, string? Name, string? Value, string Ref = "");

    // CSharpArgumentInfoFlags
    const int UseCompileTimeType = 1, Constant = 2, NamedArgument = 4, IsRef = 8, IsOut = 16, IsStaticType = 32;
    // CSharpBinderFlags
    const int CheckedContext = 1, InvokeSimpleName = 2, ConvertExplicit = 16, ConvertArrayIndex = 32, ValueFromCompoundAssignment = 128, ResultDiscarded = 256;

    /// <summary>A value argument: its static type when it can be named (as the delegate's parameter), else object.</summary>
    Arg ValueArg(IOperation a, string? name = null, RefKind rk = RefKind.None)
    {
        int f = name != null ? NamedArgument : 0;
        var t = TypeOfOperand(a);
        string pt = "object";
        if (t == null) f |= UseCompileTimeType; // the null literal
        else if (!IsDyn(t) && TypeName(t) is { } n) { pt = n; f |= UseCompileTimeType; }
        if (a.ConstantValue.HasValue) f |= Constant;
        // a ref or out argument: the variable passed by reference through the site's delegate (its own type then)
        if (rk != RefKind.None)
        {
            if (t != null && !IsDyn(t) && pt == "object") throw new CompileException("dynamic call with a ref or out argument of a type that cannot be named here", a.Syntax.GetLocation());
            f |= rk == RefKind.Out ? IsOut : IsRef;
            return new Arg(pt, f, name, null, rk == RefKind.Out ? "out " : "ref ");
        }
        return new Arg(pt, f, name, null);
    }

    Arg StaticArg(ITypeSymbol t, SyntaxNode at) => new(null, IsStaticType | UseCompileTimeType, null, "typeof(" + (TypeName(t) ?? throw new CompileException("dynamic call on a type that cannot be named here", at.GetLocation())) + ")");

    /// <summary>A receiver: a type for a static call (or a simple name in a static context), `this` for a simple
    /// name in an instance member, else a value.</summary>
    Arg Receiver(IOperation? instance, ITypeSymbol? containing, SyntaxNode at, out bool hasParam)
    {
        hasParam = false;
        if (instance == null) return StaticArg(containing!, at);
        if (instance is IInstanceReferenceOperation { IsImplicit: true } ir && EnclosingIsStatic(at)) return StaticArg(ir.Type!, at);
        hasParam = true;
        return ValueArg(instance);
    }

    bool EnclosingIsStatic(SyntaxNode at) => _model.GetEnclosingSymbol(at.SpanStart) is { } s && (s.IsStatic || s is IMethodSymbol { MethodKind: MethodKind.LambdaMethod or MethodKind.LocalFunction } l && OuterIsStatic(l));
    static bool OuterIsStatic(ISymbol s) { for (var x = s; x != null; x = x.ContainingSymbol) if (x is IMethodSymbol { MethodKind: not (MethodKind.LambdaMethod or MethodKind.LocalFunction) } m) return m.IsStatic; return false; }

    // ---------------- names ----------------
    static readonly SymbolDisplayFormat s_fq = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>The type as C# the generated class can write: null for type parameters, anonymous types and types
    /// it cannot access (private or protected ones).</summary>
    static string? TypeName(ITypeSymbol t)
    {
        if (!Nameable(t)) return null;
        return t.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(s_fq);
    }

    static bool Nameable(ITypeSymbol t) => t switch
    {
        IDynamicTypeSymbol => true,
        IArrayTypeSymbol a => Nameable(a.ElementType),
        IPointerTypeSymbol => false,
        ITypeParameterSymbol => false,
        INamedTypeSymbol { IsAnonymousType: true } => false,
        INamedTypeSymbol n => Accessible(n) && n.TypeArguments.All(Nameable) && (n.ContainingType == null || Nameable(n.ContainingType)),
        _ => false,
    };

    static bool Accessible(INamedTypeSymbol n) => n.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal or Accessibility.NotApplicable;

    /// <summary>The binder's context: the type the operation is in (its access), the nearest the class can name.</summary>
    string Context(SyntaxNode at)
    {
        var s = _model.GetEnclosingSymbol(at.SpanStart);
        var t = s as INamedTypeSymbol ?? s?.ContainingType;
        for (; t != null; t = t.ContainingType)
        {
            var d = t.OriginalDefinition;
            if (d.IsGenericType) continue;
            if (TypeName(d) is { } n) return n;
        }
        return "object";
    }

    bool Checked(SyntaxNode at)
    {
        for (var n = at; n != null; n = n.Parent)
        {
            if (n is CheckedExpressionSyntax ce) return ce.IsKind(SyntaxKind.CheckedExpression);
            if (n is CheckedStatementSyntax cs) return cs.IsKind(SyntaxKind.CheckedStatement);
            if (n is MemberDeclarationSyntax) break;
        }
        return _comp.Options.CheckOverflow;
    }

    static string Lit(string s) => SymbolDisplay.FormatLiteral(s, true);

    static bool Discarded(IOperation op) => op.Parent is IExpressionStatementOperation;

    // ---------------- sites ----------------
    /// <summary>Writes a site: its field, and its method taking the parameters the arguments have (a type argument
    /// is written in the call, not passed).</summary>
    void Emit(IOperation op, string role, string binder, IReadOnlyList<Arg> args, string? ret)
    {
        int n = _n++;
        var types = string.Join("", args.Select(a => ", " + (a.ParamType ?? "global::System.Type")));
        string dt;
        if (args.Any(a => a.Ref.Length > 0) || args.Count + (ret != null ? 2 : 1) > 17)
        {
            // by-ref parameters (or too many for Func/Action): a delegate type of the site's own, as Roslyn makes
            dt = "D" + n;
            _sb.Append("        public delegate ").Append(ret ?? "void").Append(' ').Append(dt).Append('(').Append(CallSiteT).Append(" site")
                .Append(string.Concat(args.Select((a, i) => ", " + a.Ref + (a.ParamType ?? "global::System.Type") + " p" + i))).Append(");\n");
        }
        else dt = ret == null ? (args.Count == 0 ? "global::System.Action<" + CallSiteT + ">" : "global::System.Action<" + CallSiteT + types + ">") : "global::System.Func<" + CallSiteT + types + ", " + ret + ">";
        var ps = new List<string>();
        var vs = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].ParamType == null) { vs.Add(args[i].Value!); continue; }
            ps.Add(args[i].Ref + args[i].ParamType + " a" + i);
            vs.Add(args[i].Ref + "a" + i);
        }
        var infos = string.Join(", ", args.Select(a => Ns + "CSharpArgumentInfo.Create((" + Ns + "CSharpArgumentInfoFlags)" + a.Flags + ", " + (a.Name != null ? Lit(a.Name) : "null") + ")"));
        binder = binder.Replace("$INFOS", "new " + Ns + "CSharpArgumentInfo[] { " + infos + " }");
        _sb.Append("        static ").Append(CallSiteT).Append('<').Append(dt).Append(">? s").Append(n).Append(";\n");
        _sb.Append("        public static ").Append(ret ?? "void").Append(" Site").Append(n).Append('(').Append(string.Join(", ", ps)).Append(")\n        {\n");
        _sb.Append("            if (s").Append(n).Append(" == null) s").Append(n).Append(" = ").Append(CallSiteT).Append('<').Append(dt).Append(">.Create(").Append(binder).Append(");\n");
        _sb.Append("            ").Append(ret != null ? "return " : "").Append('s').Append(n).Append(".Target(s").Append(n).Append(string.Join("", vs.Select(v => ", " + v))).Append(");\n        }\n");
        _sites[Key(op, role)] = new Site("Site" + n, ret == null);
    }

    string Flags(IOperation op, int extra = 0) => "(" + Ns + "CSharpBinderFlags)" + ((Checked(op.Syntax) ? CheckedContext : 0) | extra);

    List<Arg> Args(IOperation dyn)
    {
        var r = new List<Arg>();
        var args = dyn switch
        {
            IDynamicInvocationOperation d => d.Arguments,
            IDynamicIndexerAccessOperation d => d.Arguments,
            IDynamicObjectCreationOperation d => d.Arguments,
            _ => [],
        };
        for (int i = 0; i < args.Length; i++)
        {
            var (name, rk) = dyn switch
            {
                IDynamicInvocationOperation d => (d.GetArgumentName(i), d.GetArgumentRefKind(i)),
                IDynamicIndexerAccessOperation d => (d.GetArgumentName(i), d.GetArgumentRefKind(i)),
                IDynamicObjectCreationOperation d => (d.GetArgumentName(i), d.GetArgumentRefKind(i)),
                _ => (null, null),
            };
            r.Add(ValueArg(args[i], name, rk ?? RefKind.None));
        }
        return r;
    }

    void Invocation(IDynamicInvocationOperation di)
    {
        bool discard = Discarded(di);
        int flags = discard ? ResultDiscarded : 0;
        var args = new List<Arg>();
        string binder;
        if (di.Operation is IDynamicMemberReferenceOperation mr)
        {
            if (mr.Syntax is IdentifierNameSyntax or GenericNameSyntax) flags |= InvokeSimpleName;
            args.Add(Receiver(mr.Instance, mr.ContainingType, mr.Syntax, out _));
            var typeArgs = mr.TypeArguments.Length == 0 ? "null" : "new global::System.Type[] { " + string.Join(", ", mr.TypeArguments.Select(t => "typeof(" + (TypeName(t) ?? throw new CompileException("dynamic call with a type argument that cannot be named here", mr.Syntax.GetLocation())) + ")")) + " }";
            binder = Ns + "Binder.InvokeMember(" + Flags(di, flags) + ", " + Lit(mr.MemberName) + ", " + typeArgs + ", typeof(" + Context(di.Syntax) + "), $INFOS)";
            MemberNames.Add(mr.MemberName);
            if (mr.Instance == null && mr.ContainingType is INamedTypeSymbol st) ConstructedTypes.Add(st);
        }
        else
        {
            args.Add(ValueArg(di.Operation));
            binder = Ns + "Binder.Invoke(" + Flags(di, flags) + ", typeof(" + Context(di.Syntax) + "), $INFOS)";
            MemberNames.Add("Invoke");
        }
        args.AddRange(Args(di));
        Emit(di, "op", binder, args, discard ? null : "object");
    }

    void Member(IDynamicMemberReferenceOperation mr)
    {
        var recv = Receiver(mr.Instance, mr.ContainingType, mr.Syntax, out _);
        Emit(mr, "op", Ns + "Binder.GetMember(" + Flags(mr) + ", " + Lit(mr.MemberName) + ", typeof(" + Context(mr.Syntax) + "), $INFOS)", [recv], "object");
        MemberNames.Add(mr.MemberName);
    }

    void Indexer(IDynamicIndexerAccessOperation ia)
    {
        var args = new List<Arg> { ValueArg(ia.Operation) };
        args.AddRange(Args(ia));
        Emit(ia, "op", Ns + "Binder.GetIndex(" + Flags(ia) + ", typeof(" + Context(ia.Syntax) + "), $INFOS)", args, "object");
        MemberNames.Add("get_Item"); MemberNames.Add("Item");
    }

    void Creation(IDynamicObjectCreationOperation oc)
    {
        var t = oc.Type!;
        var args = new List<Arg> { StaticArg(t, oc.Syntax) };
        args.AddRange(Args(oc));
        Emit(oc, "op", Ns + "Binder.InvokeConstructor(" + Flags(oc) + ", typeof(" + Context(oc.Syntax) + "), $INFOS)", args, TypeName(t) ?? "object");
        if (t is INamedTypeSymbol nt) ConstructedTypes.Add(nt);
        MemberNames.Add(".ctor");
    }

    void Assignment(ISimpleAssignmentOperation sa)
    {
        bool discard = Discarded(sa);
        int flags = discard ? ResultDiscarded : 0;
        var value = ValueArg(sa.Value);
        if (sa.Target is IDynamicMemberReferenceOperation mr)
        {
            var recv = Receiver(mr.Instance, mr.ContainingType, mr.Syntax, out _);
            Emit(sa, "op", Ns + "Binder.SetMember(" + Flags(sa, flags) + ", " + Lit(mr.MemberName) + ", typeof(" + Context(sa.Syntax) + "), $INFOS)", [recv, value], discard ? null : "object");
            MemberNames.Add(mr.MemberName);
        }
        else
        {
            var ia = (IDynamicIndexerAccessOperation)sa.Target;
            var args = new List<Arg> { ValueArg(ia.Operation) };
            args.AddRange(Args(ia));
            args.Add(value);
            Emit(sa, "op", Ns + "Binder.SetIndex(" + Flags(sa, flags) + ", typeof(" + Context(sa.Syntax) + "), $INFOS)", args, discard ? null : "object");
            MemberNames.Add("set_Item"); MemberNames.Add("Item");
        }
    }

    static string? ExpressionType(BinaryOperatorKind k, bool assign) => (k, assign) switch
    {
        (BinaryOperatorKind.Add, _) => assign ? "AddAssign" : "Add",
        (BinaryOperatorKind.Subtract, _) => assign ? "SubtractAssign" : "Subtract",
        (BinaryOperatorKind.Multiply, _) => assign ? "MultiplyAssign" : "Multiply",
        (BinaryOperatorKind.Divide, _) => assign ? "DivideAssign" : "Divide",
        (BinaryOperatorKind.Remainder, _) => assign ? "ModuloAssign" : "Modulo",
        (BinaryOperatorKind.And, _) => assign ? "AndAssign" : "And",
        (BinaryOperatorKind.Or, _) => assign ? "OrAssign" : "Or",
        (BinaryOperatorKind.ExclusiveOr, _) => assign ? "ExclusiveOrAssign" : "ExclusiveOr",
        (BinaryOperatorKind.LeftShift, _) => assign ? "LeftShiftAssign" : "LeftShift",
        (BinaryOperatorKind.RightShift, _) => assign ? "RightShiftAssign" : "RightShift",
        (BinaryOperatorKind.Equals, false) => "Equal",
        (BinaryOperatorKind.NotEquals, false) => "NotEqual",
        (BinaryOperatorKind.LessThan, false) => "LessThan",
        (BinaryOperatorKind.LessThanOrEqual, false) => "LessThanOrEqual",
        (BinaryOperatorKind.GreaterThan, false) => "GreaterThan",
        (BinaryOperatorKind.GreaterThanOrEqual, false) => "GreaterThanOrEqual",
        _ => null,
    };

    static string? OperatorName(BinaryOperatorKind k) => k switch
    {
        BinaryOperatorKind.Add => "op_Addition", BinaryOperatorKind.Subtract => "op_Subtraction", BinaryOperatorKind.Multiply => "op_Multiply",
        BinaryOperatorKind.Divide => "op_Division", BinaryOperatorKind.Remainder => "op_Modulus", BinaryOperatorKind.And => "op_BitwiseAnd",
        BinaryOperatorKind.Or => "op_BitwiseOr", BinaryOperatorKind.ExclusiveOr => "op_ExclusiveOr", BinaryOperatorKind.LeftShift => "op_LeftShift",
        BinaryOperatorKind.RightShift => "op_RightShift", BinaryOperatorKind.Equals => "op_Equality", BinaryOperatorKind.NotEquals => "op_Inequality",
        BinaryOperatorKind.LessThan => "op_LessThan", BinaryOperatorKind.LessThanOrEqual => "op_LessThanOrEqual",
        BinaryOperatorKind.GreaterThan => "op_GreaterThan", BinaryOperatorKind.GreaterThanOrEqual => "op_GreaterThanOrEqual", _ => null,
    };

    string BinaryBinder(IOperation op, BinaryOperatorKind k, bool assign, int extra = 0)
    {
        var et = ExpressionType(k, assign) ?? throw new CompileException("dynamic operator " + k + " is not supported yet", op.Syntax.GetLocation());
        if (OperatorName(k) is { } on) MemberNames.Add(on);
        return Ns + "Binder.BinaryOperation(" + Flags(op, extra) + ", global::System.Linq.Expressions.ExpressionType." + et + ", typeof(" + Context(op.Syntax) + "), $INFOS)";
    }

    void Binary(IBinaryOperation b)
    {
        if (b.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
        {
            // x && y: IsFalse(x) ? x : x & y (x || y: IsTrue(x) ? x : x | y), the operator a logical one (C# 12.12.8)
            bool and = b.OperatorKind == BinaryOperatorKind.ConditionalAnd;
            if (!Dyn(b.LeftOperand))
            {
                // a static left operand: the test is the static one (the bool itself, or the type's operator false
                // for && and operator true for ||), the result when it decides is x as dynamic
                MemberNames.Add(and ? "op_BitwiseAnd" : "op_BitwiseOr");
                int bn = _n++;
                string leftT = b.LeftOperand.Type?.SpecialType == SpecialType.System_Boolean ? "bool" : ValueArg(b.LeftOperand).ParamType ?? "object";
                _sb.Append("        public static object Site").Append(bn).Append('(').Append(leftT).Append(" v) => v;\n");
                _sites[Key(b, "box")] = new Site("Site" + bn, false);
                Emit(b, "op", Ns + "Binder.BinaryOperation(" + Flags(b, 8) + ", global::System.Linq.Expressions.ExpressionType." + (and ? "And" : "Or") + ", typeof(" + Context(b.Syntax) + "), $INFOS)", [ValueArg(b.LeftOperand), ValueArg(b.RightOperand)], "object");
                return;
            }
            MemberNames.Add(and ? "op_False" : "op_True"); MemberNames.Add(and ? "op_BitwiseAnd" : "op_BitwiseOr");
            Emit(b, "test", Ns + "Binder.UnaryOperation(" + Flags(b) + ", global::System.Linq.Expressions.ExpressionType." + (and ? "IsFalse" : "IsTrue") + ", typeof(" + Context(b.Syntax) + "), $INFOS)", [ValueArg(b.LeftOperand)], "bool");
            Emit(b, "op", Ns + "Binder.BinaryOperation(" + Flags(b, 8) + ", global::System.Linq.Expressions.ExpressionType." + (and ? "And" : "Or") + ", typeof(" + Context(b.Syntax) + "), $INFOS)", [ValueArg(b.LeftOperand), ValueArg(b.RightOperand)], "object");
            return;
        }
        Emit(b, "op", BinaryBinder(b, b.OperatorKind, false), [ValueArg(b.LeftOperand), ValueArg(b.RightOperand)], "object");
    }

    void Unary(IUnaryOperation u)
    {
        var (et, on) = u.OperatorKind switch
        {
            UnaryOperatorKind.Minus => ("Negate", "op_UnaryNegation"),
            UnaryOperatorKind.Plus => ("UnaryPlus", "op_UnaryPlus"),
            UnaryOperatorKind.Not => ("Not", "op_LogicalNot"),
            UnaryOperatorKind.BitwiseNegation => ("OnesComplement", "op_OnesComplement"),
            UnaryOperatorKind.True => ("IsTrue", "op_True"),
            UnaryOperatorKind.False => ("IsFalse", "op_False"),
            _ => throw new CompileException("dynamic operator " + u.OperatorKind + " is not supported yet", u.Syntax.GetLocation()),
        };
        MemberNames.Add(on);
        bool test = u.OperatorKind is UnaryOperatorKind.True or UnaryOperatorKind.False;
        Emit(u, "op", Ns + "Binder.UnaryOperation(" + Flags(u) + ", global::System.Linq.Expressions.ExpressionType." + et + ", typeof(" + Context(u.Syntax) + "), $INFOS)", [ValueArg(u.Operand)], test ? "bool" : "object");
    }

    void Conversion(IConversionOperation c)
    {
        var t = TypeName(c.Type!) ?? throw new CompileException("dynamic conversion to a type that cannot be named here", c.Syntax.GetLocation());
        int flags = c.Conversion.IsImplicit ? 0 : ConvertExplicit;
        if (c.Parent is IArrayElementReferenceOperation ae && ae.ArrayReference != c) flags |= ConvertArrayIndex;
        Emit(c, "conv", Ns + "Binder.Convert(" + Flags(c, flags) + ", typeof(" + t + "), typeof(" + Context(c.Syntax) + "))".Replace("$INFOS", ""), [ValueArg(c.Operand)], t);
        MemberNames.Add("op_Implicit"); MemberNames.Add("op_Explicit");
    }

    // x op= v: on a variable, a BinaryOperation site whose result is stored as for x = x op v; on a dynamic member
    // or element, one site method that gets it, applies the operator and sets it (the receiver once)
    void Compound(ICompoundAssignmentOperation ca)
    {
        if (ca.Target is IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation)
        {
            CompoundOnMember(ca, ca.Target, ca.Value, BinaryBinder(ca, ca.OperatorKind, true), Discarded(ca));
            return;
        }
        Emit(ca, "op", BinaryBinder(ca, ca.OperatorKind, true), [ValueArg(ca.Target), ValueArg(ca.Value)], "object");
        // a variable of a type of its own (`int sum; sum += d`): the result goes back as that type, by an explicit
        // conversion found at run time, as Roslyn writes it
        if (TypeOfOperand(ca.Target) is { } tt && !IsDyn(tt) && tt.SpecialType != SpecialType.System_Object && TypeName(tt) is { } tn)
        {
            Emit(ca, "conv", Ns + "Binder.Convert(" + Flags(ca, ConvertExplicit) + ", typeof(" + tn + "), typeof(" + Context(ca.Syntax) + "))", [new Arg("object", 0, null, null)], tn);
            MemberNames.Add("op_Implicit"); MemberNames.Add("op_Explicit");
        }
    }

    void IncDec(IIncrementOrDecrementOperation inc)
    {
        bool isInc = inc.Kind == OperationKind.Increment;
        MemberNames.Add(isInc ? "op_Increment" : "op_Decrement");
        var binder = Ns + "Binder.UnaryOperation(" + Flags(inc) + ", global::System.Linq.Expressions.ExpressionType." + (isInc ? "Increment" : "Decrement") + ", typeof(" + Context(inc.Syntax) + "), $INFOS)";
        if (inc.Target is IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation)
        {
            CompoundOnMember(inc, inc.Target, null, binder, Discarded(inc), postfix: inc.IsPostfix);
            return;
        }
        Emit(inc, "op", binder, [ValueArg(inc.Target)], "object");
    }

    /// <summary>One method: get the member (or element), apply the operator's site, set it; returns the new value.</summary>
    void CompoundOnMember(IOperation op, IOperation target, IOperation? value, string opBinder, bool discard, bool postfix = false)
    {
        int n = _n++;
        string ctx = Context(op.Syntax);
        var recvArgs = new List<Arg>();
        string getB, setB, name;
        List<IOperation> recvOps = [];
        if (target is IDynamicMemberReferenceOperation mr)
        {
            recvArgs.Add(Receiver(mr.Instance, mr.ContainingType, mr.Syntax, out _));
            getB = Ns + "Binder.GetMember(" + Flags(op) + ", " + Lit(mr.MemberName) + ", typeof(" + ctx + "), $INFOS)";
            setB = Ns + "Binder.SetMember(" + Flags(op, ValueFromCompoundAssignment | (discard ? ResultDiscarded : 0)) + ", " + Lit(mr.MemberName) + ", typeof(" + ctx + "), $INFOS)";
            name = mr.MemberName;
        }
        else
        {
            var ia = (IDynamicIndexerAccessOperation)target;
            recvArgs.Add(ValueArg(ia.Operation));
            recvArgs.AddRange(Args(ia));
            getB = Ns + "Binder.GetIndex(" + Flags(op) + ", typeof(" + ctx + "), $INFOS)";
            setB = Ns + "Binder.SetIndex(" + Flags(op, ValueFromCompoundAssignment | (discard ? ResultDiscarded : 0)) + ", typeof(" + ctx + "), $INFOS)";
            name = "Item";
            MemberNames.Add("get_Item"); MemberNames.Add("set_Item");
        }
        MemberNames.Add(name);
        var valueArg = value != null ? ValueArg(value) : null;
        // the parts as sites of their own, called from the method
        var ps = new List<string>(); var vs = new List<string>(); var types = new List<string>();
        for (int i = 0; i < recvArgs.Count; i++)
        {
            types.Add(recvArgs[i].ParamType ?? "global::System.Type");
            if (recvArgs[i].ParamType == null) { vs.Add(recvArgs[i].Value!); continue; }
            ps.Add(recvArgs[i].ParamType + " r" + i); vs.Add("r" + i);
        }
        if (valueArg != null) ps.Add(valueArg.ParamType + " v");
        string Infos(IEnumerable<Arg> args) => "new " + Ns + "CSharpArgumentInfo[] { " + string.Join(", ", args.Select(a => Ns + "CSharpArgumentInfo.Create((" + Ns + "CSharpArgumentInfoFlags)" + a.Flags + ", " + (a.Name != null ? Lit(a.Name) : "null") + ")")) + " }";
        string Fn(IEnumerable<string> ts, string? ret) => ret == null ? "global::System.Action<" + CallSiteT + string.Join("", ts.Select(x => ", " + x)) + ">" : "global::System.Func<" + CallSiteT + string.Join("", ts.Select(x => ", " + x)) + ", " + ret + ">";
        var getT = Fn(types, "object");
        var opArgs = valueArg != null ? new[] { new Arg("object", 0, null, null), valueArg } : new[] { new Arg("object", 0, null, null) };
        var opT = Fn(opArgs.Select(a => a.ParamType!), "object");
        var setArgs = recvArgs.Append(new Arg("object", 0, null, null)).ToList();
        var setT = Fn(setArgs.Select(a => a.ParamType ?? "global::System.Type"), discard ? null : "object");
        _sb.Append($"        static {CallSiteT}<{getT}>? g{n};\n        static {CallSiteT}<{opT}>? o{n};\n        static {CallSiteT}<{setT}>? t{n};\n");
        _sb.Append($"        public static {(discard ? "void" : "object")} Site{n}({string.Join(", ", ps)})\n        {{\n");
        _sb.Append($"            if (g{n} == null) g{n} = {CallSiteT}<{getT}>.Create({getB.Replace("$INFOS", Infos(recvArgs))});\n");
        _sb.Append($"            if (o{n} == null) o{n} = {CallSiteT}<{opT}>.Create({opBinder.Replace("$INFOS", Infos(opArgs))});\n");
        _sb.Append($"            if (t{n} == null) t{n} = {CallSiteT}<{setT}>.Create({setB.Replace("$INFOS", Infos(setArgs))});\n");
        _sb.Append($"            object cur = g{n}.Target(g{n}{string.Join("", vs.Select(v => ", " + v))});\n");
        _sb.Append($"            object nv = o{n}.Target(o{n}, cur{(valueArg != null ? ", v" : "")});\n");
        // (a postfix ++/-- has the value from before)
        _sb.Append($"            {(discard || postfix ? "" : "return ")}t{n}.Target(t{n}{string.Join("", vs.Select(v => ", " + v))}, nv);\n");
        if (postfix && !discard) _sb.Append("            return cur;\n");
        _sb.Append("        }\n");
        _sites[Key(op, "op")] = new Site("Site" + n, discard);
    }

}
