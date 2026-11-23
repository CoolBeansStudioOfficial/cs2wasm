// A small evaluator over C# syntax: enough of the language to recover the test cases NetWasm's test classes build
// (a name, a namespace, a source text and a list of inputs, with init-only options), without compiling or running
// NetWasm's harness. Values are evaluated lazily, so an option the extractor never reads (an evidence path built from
// the file system, say) does not stop a case from being extracted. Anything outside this subset throws Unsupported,
// and the case is reported as not extracted, with the reason.
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Il2Wasm.NetWasmSuite;

sealed class Unsupported(string why) : Exception(why);

// An object built by `new T(...) { P = v }` (or a target-typed `new(...)`): its arguments and properties, unevaluated.
sealed class Obj(string type, List<Cell> args, Dictionary<string, Cell> props)
{
    public string Type { get; } = type;
    public List<Cell> Args { get; } = args;
    public Dictionary<string, Cell> Props { get; } = props;
    public Obj With(Dictionary<string, Cell> overrides)
    {
        var p = new Dictionary<string, Cell>(Props);
        foreach (var (k, v) in overrides) p[k] = v;
        return new Obj(Type, Args, p);
    }
}

sealed record EnumV(string Text)
{
    public override string ToString() => Text.Contains('.') ? Text[(Text.LastIndexOf('.') + 1)..] : Text;
}

sealed record Resource(string Path);

// A dictionary, in insertion order.
sealed class Dict : List<(object? Key, Cell Value)>
{
    public object? Get(object? key)
    {
        foreach (var (k, v) in this) if (Equals(k, key)) return v.Get();
        throw new Unsupported($"no key {key}");
    }
}

sealed record Lambda(IReadOnlyList<string> Params, CSharpSyntaxNode Body, Scope Scope);

// A variable: a thunk until first read (or assigned).
sealed class Cell
{
    Func<object?>? thunk;
    object? value;
    bool has;
    Exception? failure;
    public Cell(Func<object?> thunk) => this.thunk = thunk;
    public static Cell Of(object? v) => new(() => v);
    public object? Get()
    {
        if (failure is not null) throw failure;
        if (!has)
        {
            try { value = thunk!(); has = true; thunk = null; }
            catch (Exception e) { failure = e; throw; }
        }
        return value;
    }
    public void Set(object? v) { value = v; has = true; failure = null; thunk = null; }
}

sealed class Scope(Scope? parent, TypeDeclarationSyntax? type)
{
    readonly Dictionary<string, Cell> vars = new();
    public Scope? Parent { get; } = parent;
    public TypeDeclarationSyntax? Type { get; } = type ?? parent?.Type;
    public void Declare(string name, Cell cell) => vars[name] = cell;
    public Cell? Find(string name) => vars.TryGetValue(name, out var c) ? c : Parent?.Find(name);
}

sealed class ReturnSignal(object? value) : Exception { public object? Value { get; } = value; }
sealed class BreakSignal : Exception;
sealed class ContinueSignal : Exception;

sealed class Evaluator
{
    readonly Dictionary<string, List<BaseTypeDeclarationSyntax>> types = new(StringComparer.Ordinal);
    readonly Dictionary<SyntaxNode, Cell> memberCache = new();
    int depth;

    // Called for each `runner.Run(fixture)` the interpreter reaches: the argument, unevaluated.
    public Action<ExpressionSyntax, Scope>? OnRun { get; set; }

    // Where an embedded resource of the test project is read from: the project's directory (its resources are named by
    // their path in it).
    public string? ResourceRoot { get; set; }

    public Evaluator(IEnumerable<SyntaxTree> trees)
    {
        foreach (var t in trees)
            foreach (var d in t.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (!types.TryGetValue(d.Identifier.ValueText, out var list)) types[d.Identifier.ValueText] = list = new();
                list.Add(d);
            }
    }

    public IEnumerable<TypeDeclarationSyntax> AllTypes => types.Values.SelectMany(x => x).OfType<TypeDeclarationSyntax>();
    public IReadOnlyList<BaseTypeDeclarationSyntax> TypesNamed(string name) => types.TryGetValue(name, out var l) ? l : [];

    // ---- members ----

    // The types searched for a simple name used inside `type`: the type, its containing types, its base types (by name).
    IEnumerable<TypeDeclarationSyntax> LookupChain(TypeDeclarationSyntax? type)
    {
        var seen = new HashSet<TypeDeclarationSyntax>();
        var queue = new Queue<TypeDeclarationSyntax>();
        for (var t = type; t is not null; t = t.Parent as TypeDeclarationSyntax) queue.Enqueue(t);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!seen.Add(t)) continue;
            yield return t;
            // partial declarations of the same type
            foreach (var p in TypesNamed(t.Identifier.ValueText).OfType<TypeDeclarationSyntax>())
                if (p != t && p.Modifiers.Any(SyntaxKind.PartialKeyword)) queue.Enqueue(p);
            if (t.BaseList is not null)
                foreach (var b in t.BaseList.Types)
                {
                    var name = b.Type switch { SimpleNameSyntax s => s.Identifier.ValueText, QualifiedNameSyntax q => q.Right.Identifier.ValueText, _ => null };
                    if (name is null) continue;
                    foreach (var bt in TypesNamed(name).OfType<TypeDeclarationSyntax>()) queue.Enqueue(bt);
                }
        }
    }

    Cell? FindMember(TypeDeclarationSyntax? type, string name)
    {
        foreach (var t in LookupChain(type))
        {
            foreach (var m in t.Members)
            {
                switch (m)
                {
                    case FieldDeclarationSyntax f:
                        foreach (var v in f.Declaration.Variables)
                            if (v.Identifier.ValueText == name && v.Initializer is not null)
                                return Cached(v, () => Eval(v.Initializer.Value, new Scope(null, t)));
                        break;
                    case PropertyDeclarationSyntax p when p.Identifier.ValueText == name:
                        if (p.ExpressionBody is not null) return Cached(p, () => Eval(p.ExpressionBody.Expression, new Scope(null, t)));
                        if (p.Initializer is not null) return Cached(p, () => Eval(p.Initializer.Value, new Scope(null, t)));
                        var get = p.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
                        if (get?.ExpressionBody is not null) return Cached(p, () => Eval(get.ExpressionBody.Expression, new Scope(null, t)));
                        if (get?.Body is not null) return Cached(p, () => RunBody(get.Body, new Scope(null, t)));
                        break;
                }
            }
            if (t is EnumDeclarationSyntax) { }
        }
        return null;
    }

    Cell Cached(SyntaxNode key, Func<object?> f)
    {
        if (!memberCache.TryGetValue(key, out var c)) memberCache[key] = c = new Cell(f);
        return c;
    }

    MethodDeclarationSyntax? FindMethod(TypeDeclarationSyntax? type, string name, int argc)
    {
        foreach (var t in LookupChain(type))
            foreach (var m in t.Members.OfType<MethodDeclarationSyntax>())
                if (m.Identifier.ValueText == name && Accepts(m.ParameterList, argc)) return m;
        return null;
    }

    static bool Accepts(ParameterListSyntax ps, int argc)
    {
        var required = ps.Parameters.Count(p => p.Default is null && !p.Modifiers.Any(SyntaxKind.ParamsKeyword));
        var max = ps.Parameters.Any(p => p.Modifiers.Any(SyntaxKind.ParamsKeyword)) ? int.MaxValue : ps.Parameters.Count;
        return argc >= required && argc <= max;
    }

    // A theory's [MemberData(nameof(X))]: the member's value (a property or a parameterless method of the type).
    public object? MemberData(TypeDeclarationSyntax type, string name)
    {
        if (FindMember(type, name) is { } c) return c.Get();
        if (FindMethod(type, name, 0) is { } m) return CallMethod(m, [], new Scope(null, type));
        throw new Unsupported($"no member data {name}");
    }

    TypeDeclarationSyntax? TypeByName(string name) => TypesNamed(name).OfType<TypeDeclarationSyntax>().FirstOrDefault();
    bool IsEnumType(string name) => TypesNamed(name).OfType<EnumDeclarationSyntax>().Any();

    public object? CallMethod(MethodDeclarationSyntax m, IReadOnlyList<Cell> args, Scope caller)
    {
        if (++depth > 200) { depth--; throw new Unsupported("recursion too deep"); }
        try
        {
            var scope = new Scope(null, m.Parent as TypeDeclarationSyntax);
            for (var i = 0; i < m.ParameterList.Parameters.Count; i++)
            {
                var p = m.ParameterList.Parameters[i];
                if (p.Modifiers.Any(SyntaxKind.ParamsKeyword))
                {
                    var rest = args.Skip(i).ToList();
                    scope.Declare(p.Identifier.ValueText, new Cell(() => rest.Select(c => c.Get()).ToList()));
                    break;
                }
                if (i < args.Count) scope.Declare(p.Identifier.ValueText, args[i]);
                else if (p.Default is not null) { var d = p.Default.Value; scope.Declare(p.Identifier.ValueText, new Cell(() => Eval(d, scope))); }
            }
            if (m.ExpressionBody is not null) return Eval(m.ExpressionBody.Expression, scope);
            if (m.Body is not null) return RunBody(m.Body, scope);
            throw new Unsupported($"method {m.Identifier} has no body");
        }
        finally { depth--; }
    }

    // ---- statements ----

    public object? RunBody(BlockSyntax body, Scope scope)
    {
        try { Exec(body, scope); return null; }
        catch (ReturnSignal r) { return r.Value; }
    }

    public void Exec(StatementSyntax s, Scope scope)
    {
        switch (s)
        {
            case BlockSyntax b:
            {
                var inner = new Scope(scope, null);
                foreach (var st in b.Statements) Exec(st, inner);
                break;
            }
            case LocalDeclarationStatementSyntax ld:
                foreach (var v in ld.Declaration.Variables)
                {
                    if (v.Initializer is null) { scope.Declare(v.Identifier.ValueText, Cell.Of(null)); continue; }
                    var init = v.Initializer.Value;
                    // a call to the runner as an initializer (var result = runner.Run(...)) is a case too
                    if (IsRunCall(init, scope, out var arg)) { OnRun?.Invoke(arg!, scope); scope.Declare(v.Identifier.ValueText, Cell.Of(null)); continue; }
                    scope.Declare(v.Identifier.ValueText, new Cell(() => Eval(init, scope)));
                }
                break;
            case ReturnStatementSyntax r:
                if (r.Expression is not null && IsRunCall(r.Expression, scope, out var ra)) { OnRun?.Invoke(ra!, scope); throw new ReturnSignal(null); }
                throw new ReturnSignal(r.Expression is null ? null : Eval(r.Expression, scope));
            case ExpressionStatementSyntax es:
                ExecExpression(es.Expression, scope);
                break;
            case IfStatementSyntax ifs:
                if (Truth(Eval(ifs.Condition, scope))) Exec(ifs.Statement, new Scope(scope, null));
                else if (ifs.Else is not null) Exec(ifs.Else.Statement, new Scope(scope, null));
                break;
            case ForEachStatementSyntax fe:
            {
                var items = AsList(Eval(fe.Expression, scope));
                foreach (var item in items)
                {
                    var inner = new Scope(scope, null);
                    inner.Declare(fe.Identifier.ValueText, Cell.Of(item));
                    try { Exec(fe.Statement, inner); }
                    catch (ContinueSignal) { }
                    catch (BreakSignal) { break; }
                }
                break;
            }
            case ForStatementSyntax fs:
            {
                var inner = new Scope(scope, null);
                if (fs.Declaration is not null)
                    foreach (var v in fs.Declaration.Variables) inner.Declare(v.Identifier.ValueText, Cell.Of(v.Initializer is null ? null : Eval(v.Initializer.Value, inner)));
                foreach (var i in fs.Initializers) ExecExpression(i, inner);
                for (var guard = 0; fs.Condition is null || Truth(Eval(fs.Condition, inner)); guard++)
                {
                    if (guard > 1_000_000) throw new Unsupported("loop too long");
                    try { Exec(fs.Statement, new Scope(inner, null)); }
                    catch (ContinueSignal) { }
                    catch (BreakSignal) { break; }
                    foreach (var inc in fs.Incrementors) ExecExpression(inc, inner);
                }
                break;
            }
            case WhileStatementSyntax ws:
                for (var guard = 0; Truth(Eval(ws.Condition, scope)); guard++)
                {
                    if (guard > 1_000_000) throw new Unsupported("loop too long");
                    try { Exec(ws.Statement, new Scope(scope, null)); }
                    catch (ContinueSignal) { }
                    catch (BreakSignal) { break; }
                }
                break;
            case BreakStatementSyntax: throw new BreakSignal();
            case ContinueStatementSyntax: throw new ContinueSignal();
            case EmptyStatementSyntax: break;
            case LocalFunctionStatementSyntax: break; // (not supported as a callee; ignored as a statement)
            case UsingStatementSyntax u when u.Statement is not null: Exec(u.Statement, scope); break;
            case TryStatementSyntax ts:
                // the try block, then the finally block; a catch is never taken (a throw is Unsupported)
                try { Exec(ts.Block, scope); }
                finally { if (ts.Finally is not null) Exec(ts.Finally.Block, scope); }
                break;
            case ThrowStatementSyntax: throw new Unsupported("reaches a throw");
            default:
                throw new Unsupported($"statement {s.Kind()}");
        }
    }

    // An expression statement: an assignment, a mutation of a builder or list, a call to the runner (a case), a call
    // to a method of the test classes (run, for the cases it reaches), or anything else (an assertion: ignored).
    void ExecExpression(ExpressionSyntax e, Scope scope)
    {
        switch (e)
        {
            case AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.ValueText: "_" } }: return;
            case AssignmentExpressionSyntax a when a.Left is IdentifierNameSyntax id:
            {
                var cell = scope.Find(id.Identifier.ValueText) ?? throw new Unsupported($"assignment to {id}");
                var rhs = a.Right;
                if (a.IsKind(SyntaxKind.SimpleAssignmentExpression)) cell.Set(Eval(rhs, scope));
                else
                {
                    var op = a.Kind() switch
                    {
                        SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
                        SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
                        SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
                        SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
                        SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
                        _ => throw new Unsupported($"assignment {a.Kind()}"),
                    };
                    cell.Set(Binary(op, cell.Get(), Eval(rhs, scope)));
                }
                return;
            }
            case PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax:
            {
                var (operand, plus) = e switch
                {
                    PostfixUnaryExpressionSyntax p => (p.Operand, p.IsKind(SyntaxKind.PostIncrementExpression)),
                    PrefixUnaryExpressionSyntax p => (p.Operand, p.IsKind(SyntaxKind.PreIncrementExpression)),
                    _ => throw new InvalidOperationException(),
                };
                if (operand is IdentifierNameSyntax id && scope.Find(id.Identifier.ValueText) is { } cell)
                { cell.Set(Binary(plus ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression, cell.Get(), 1)); return; }
                throw new Unsupported("increment of a non-local");
            }
            case InvocationExpressionSyntax inv:
            {
                if (IsRunCall(inv, scope, out var arg)) { OnRun?.Invoke(arg!, scope); return; }
                if (inv.Expression is MemberAccessExpressionSyntax ma && ma.Expression is IdentifierNameSyntax recv
                    && scope.Find(recv.Identifier.ValueText) is { } rc)
                {
                    object? target;
                    try { target = rc.Get(); } catch (Unsupported) { return; }
                    if (target is StringBuilder or List<object?>) { Eval(inv, scope); return; }
                }
                var (name, argc) = (inv.Expression switch { IdentifierNameSyntax i => i.Identifier.ValueText, _ => null }, inv.ArgumentList.Arguments.Count);
                if (name is not null && FindMethod(scope.Type, name, argc) is { } m)
                {
                    var args = inv.ArgumentList.Arguments.Select(a => new Cell(() => Eval(a.Expression, scope))).ToList();
                    CallMethod(m, args, scope);
                }
                return; // an assertion or a call we do not follow
            }
            case AwaitExpressionSyntax aw: ExecExpression(aw.Expression, scope); return;
            default: return;
        }
    }

    // `runner.Run(x)`, `Runner.Run(x)`, `_runner.Run(x)`, or the base class's `Run(x)` with one argument.
    public bool IsRunCall(ExpressionSyntax e, Scope scope, out ExpressionSyntax? arg)
    {
        arg = null;
        if (e is AwaitExpressionSyntax aw) e = aw.Expression;
        if (e is not InvocationExpressionSyntax inv || inv.ArgumentList.Arguments.Count != 1) return false;
        var ok = inv.Expression switch
        {
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Run", Expression: IdentifierNameSyntax r } => r.Identifier.ValueText.Contains("runner", StringComparison.OrdinalIgnoreCase),
            IdentifierNameSyntax { Identifier.ValueText: "Run" } => IsSemanticBase(scope.Type),
            _ => false,
        };
        if (ok) arg = inv.ArgumentList.Arguments[0].Expression;
        return ok;
    }

    bool IsSemanticBase(TypeDeclarationSyntax? type)
    {
        foreach (var t in LookupChain(type))
            if (t.Identifier.ValueText == "CSharpSemanticTestBase") return true;
        return false;
    }

    // ---- expressions ----

    public object? Eval(ExpressionSyntax e, Scope scope)
    {
        if (++depth > 400) { depth--; throw new Unsupported("expression too deep"); }
        try { return EvalCore(e, scope); }
        finally { depth--; }
    }

    object? EvalCore(ExpressionSyntax e, Scope scope)
    {
        switch (e)
        {
            case ParenthesizedExpressionSyntax p: return Eval(p.Expression, scope);
            case CheckedExpressionSyntax c: return Eval(c.Expression, scope);
            case LiteralExpressionSyntax lit:
                return lit.Kind() switch
                {
                    SyntaxKind.NullLiteralExpression => null,
                    SyntaxKind.TrueLiteralExpression => true,
                    SyntaxKind.FalseLiteralExpression => false,
                    SyntaxKind.DefaultLiteralExpression => null,
                    _ => lit.Token.Value,
                };
            case InterpolatedStringExpressionSyntax istr:
            {
                var sb = new StringBuilder();
                foreach (var c in istr.Contents)
                    switch (c)
                    {
                        case InterpolatedStringTextSyntax t: sb.Append(t.TextToken.ValueText); break;
                        case InterpolationSyntax h:
                        {
                            var v = Eval(h.Expression, scope);
                            var fmt = h.FormatClause?.FormatStringToken.ValueText;
                            var s = v is IFormattable f ? f.ToString(fmt, CultureInfo.InvariantCulture) : Str(v);
                            if (h.AlignmentClause is not null)
                            {
                                var w = Convert.ToInt32(Eval(h.AlignmentClause.Value, scope));
                                s = w < 0 ? s.PadRight(-w) : s.PadLeft(w);
                            }
                            sb.Append(s);
                            break;
                        }
                    }
                return sb.ToString();
            }
            case IdentifierNameSyntax id:
            {
                var name = id.Identifier.ValueText;
                if (scope.Find(name) is { } cell) return cell.Get();
                if (FindMember(scope.Type, name) is { } m) return m.Get();
                throw new Unsupported($"unknown name {name}");
            }
            case MemberAccessExpressionSyntax ma: return MemberAccess(ma, scope);
            case BinaryExpressionSyntax b:
            {
                if (b.IsKind(SyntaxKind.LogicalAndExpression)) return Truth(Eval(b.Left, scope)) && Truth(Eval(b.Right, scope));
                if (b.IsKind(SyntaxKind.LogicalOrExpression)) return Truth(Eval(b.Left, scope)) || Truth(Eval(b.Right, scope));
                if (b.IsKind(SyntaxKind.CoalesceExpression)) return Eval(b.Left, scope) ?? Eval(b.Right, scope);
                if (b.IsKind(SyntaxKind.AsExpression) || b.IsKind(SyntaxKind.IsExpression)) throw new Unsupported("type test");
                return Binary(b.Kind(), Eval(b.Left, scope), Eval(b.Right, scope));
            }
            case PrefixUnaryExpressionSyntax u:
            {
                var v = Eval(u.Operand, scope);
                return u.Kind() switch
                {
                    SyntaxKind.UnaryMinusExpression => v switch { int i => unchecked(-i), long l => unchecked(-l), double d => -d, float f => -f, uint ui => -(long)ui, _ => throw new Unsupported("negate") },
                    SyntaxKind.UnaryPlusExpression => v,
                    SyntaxKind.BitwiseNotExpression => v switch { int i => ~i, long l => ~l, uint ui => ~ui, ulong ul => ~ul, _ => throw new Unsupported("complement") },
                    SyntaxKind.LogicalNotExpression => !Truth(v),
                    _ => throw new Unsupported($"unary {u.Kind()}"),
                };
            }
            case PostfixUnaryExpressionSyntax pu when pu.IsKind(SyntaxKind.SuppressNullableWarningExpression): return Eval(pu.Operand, scope);
            case ConditionalExpressionSyntax c: return Truth(Eval(c.Condition, scope)) ? Eval(c.WhenTrue, scope) : Eval(c.WhenFalse, scope);
            case CastExpressionSyntax c: return Cast(c.Type.ToString(), Eval(c.Expression, scope));
            case InvocationExpressionSyntax inv: return Invoke(inv, scope);
            case CollectionExpressionSyntax ce:
            {
                var list = new List<object?>();
                foreach (var el in ce.Elements)
                    switch (el)
                    {
                        case ExpressionElementSyntax x: list.Add(Eval(x.Expression, scope)); break;
                        case SpreadElementSyntax sp: list.AddRange(AsList(Eval(sp.Expression, scope))); break;
                        default: throw new Unsupported("collection element");
                    }
                return list;
            }
            case ImplicitArrayCreationExpressionSyntax ia: return ia.Initializer.Expressions.Select(x => Eval(x, scope)).ToList();
            case ArrayCreationExpressionSyntax ac:
                if (ac.Initializer is null) throw new Unsupported("array without initializer");
                return ac.Initializer.Expressions.Select(x => Eval(x, scope)).ToList();
            case ImplicitObjectCreationExpressionSyntax io: return Create("CorpusFixture?", io.ArgumentList, io.Initializer, scope);
            case ObjectCreationExpressionSyntax oc:
            {
                var tn = oc.Type.ToString();
                if (tn is "StringBuilder" or "System.Text.StringBuilder") return new StringBuilder();
                if (tn is "StreamReader" && oc.ArgumentList?.Arguments.Count == 1) return Eval(oc.ArgumentList.Arguments[0].Expression, scope) as Resource ?? throw new Unsupported("a reader of something else than a resource");
                if (tn.StartsWith("List<", StringComparison.Ordinal)) return oc.Initializer is null ? new List<object?>() : oc.Initializer.Expressions.Select(x => Eval(x, scope)).ToList();
                return Create(tn, oc.ArgumentList, oc.Initializer, scope);
            }
            case WithExpressionSyntax w:
            {
                if (Eval(w.Expression, scope) is not Obj o) throw new Unsupported("with on a non-object");
                return o.With(Initializers(w.Initializer, scope));
            }
            case ElementAccessExpressionSyntax ea:
            {
                var target = Eval(ea.Expression, scope);
                var key = Eval(ea.ArgumentList.Arguments[0].Expression, scope);
                if (target is Dict dict) return dict.Get(key);
                var index = Convert.ToInt32(key);
                return target switch { List<object?> l => l[index], string s => s[index], _ => throw new Unsupported("indexing") };
            }
            case SimpleLambdaExpressionSyntax sl: return new Lambda([sl.Parameter.Identifier.ValueText], sl.Body, scope);
            case ParenthesizedLambdaExpressionSyntax pl: return new Lambda(pl.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToList(), pl.Body, scope);
            case SwitchExpressionSyntax sw:
            {
                var v = Eval(sw.GoverningExpression, scope);
                foreach (var arm in sw.Arms)
                {
                    if (arm.WhenClause is not null) throw new Unsupported("switch arm with when");
                    var hit = arm.Pattern switch
                    {
                        DiscardPatternSyntax => true,
                        ConstantPatternSyntax cp => Equals(Normalize(Eval(cp.Expression, scope)), Normalize(v)),
                        _ => throw new Unsupported($"pattern {arm.Pattern.Kind()}"),
                    };
                    if (hit) return Eval(arm.Expression, scope);
                }
                throw new Unsupported("no switch arm matched");
            }
            case TypeOfExpressionSyntax: throw new Unsupported("typeof");
            default: throw new Unsupported($"expression {e.Kind()}");
        }
    }

    object Create(string type, ArgumentListSyntax? args, InitializerExpressionSyntax? init, Scope scope)
    {
        if (type.StartsWith("TheoryData", StringComparison.Ordinal)) return new List<object?>();
        // a dictionary: `new() { ["k"] = v }` or `new Dictionary<K, V> { { "k", v } }`
        if (init is not null && init.Expressions.Count > 0 && init.Expressions.All(x => x is AssignmentExpressionSyntax { Left: ImplicitElementAccessSyntax } || x is InitializerExpressionSyntax { RawKind: (int)SyntaxKind.ComplexElementInitializerExpression }))
        {
            // rows of a TheoryData of other than two columns
            if (init.Expressions.Any(x => x is InitializerExpressionSyntax { Expressions.Count: not 2 }))
                return init.Expressions.Select(x => (object?)((InitializerExpressionSyntax)x).Expressions.Select(e => Eval(e, scope)).ToList()).ToList();
            var d = new Dict();
            foreach (var x in init.Expressions)
            {
                if (x is AssignmentExpressionSyntax { Left: ImplicitElementAccessSyntax iea } asg)
                { var r = asg.Right; d.Add((Eval(iea.ArgumentList.Arguments[0].Expression, scope), new Cell(() => Eval(r, scope)))); }
                else if (x is InitializerExpressionSyntax ci && ci.Expressions.Count == 2)
                { var r = ci.Expressions[1]; d.Add((Eval(ci.Expressions[0], scope), new Cell(() => Eval(r, scope)))); }
            }
            return d;
        }
        if (type.Contains("Dictionary", StringComparison.Ordinal) && init is null) return new Dict();
        var a = args?.Arguments.Select(x => new Cell(() => Eval(x.Expression, scope))).ToList() ?? new List<Cell>();
        // named arguments become properties of the same name (a record's positional parameters)
        var props = new Dictionary<string, Cell>(StringComparer.Ordinal);
        if (args is not null)
            for (var i = 0; i < args.Arguments.Count; i++)
                if (args.Arguments[i].NameColon is { } nc) props[char.ToUpperInvariant(nc.Name.Identifier.ValueText[0]) + nc.Name.Identifier.ValueText[1..]] = a[i];
        foreach (var (k, v) in Initializers(init, scope)) props[k] = v;
        return new Obj(type, a, props);
    }

    Dictionary<string, Cell> Initializers(InitializerExpressionSyntax? init, Scope scope)
    {
        var d = new Dictionary<string, Cell>(StringComparer.Ordinal);
        if (init is null) return d;
        foreach (var x in init.Expressions)
            if (x is AssignmentExpressionSyntax { Left: IdentifierNameSyntax n } asg) { var r = asg.Right; d[n.Identifier.ValueText] = new Cell(() => Eval(r, scope)); }
        return d;
    }

    object? MemberAccess(MemberAccessExpressionSyntax ma, Scope scope)
    {
        var name = ma.Name.Identifier.ValueText;
        var left = ma.Expression.ToString();
        switch (left, name)
        {
            case ("int" or "Int32", "MaxValue"): return int.MaxValue;
            case ("int" or "Int32", "MinValue"): return int.MinValue;
            case ("long" or "Int64", "MaxValue"): return long.MaxValue;
            case ("long" or "Int64", "MinValue"): return long.MinValue;
            case ("string" or "String", "Empty"): return "";
        }
        // a type of the test sources: a member of it, or an enum member
        if (ma.Expression is IdentifierNameSyntax tid && scope.Find(tid.Identifier.ValueText) is null && FindMember(scope.Type, tid.Identifier.ValueText) is null)
        {
            var tname = tid.Identifier.ValueText;
            if (IsEnumType(tname)) return new EnumV($"{tname}.{name}");
            if (TypeByName(tname) is { } t)
            {
                if (FindMember(t, name) is { } m) return m.Get();
                throw new Unsupported($"unknown member {tname}.{name}");
            }
            // an enum or type of NetWasm's compiler, which these sources do not declare
            if (char.IsUpper(tname[0])) return new EnumV($"{tname}.{name}");
        }
        var target = Eval(ma.Expression, scope);
        return (target, name) switch
        {
            (string s, "Length") => s.Length,
            (List<object?> l, "Length" or "Count") => l.Count,
            (List<object?> l, "IsEmpty") => l.Count == 0,
            (List<object?>, "IsDefault") => false,
            (List<object?> l, "IsDefaultOrEmpty") => l.Count == 0,
            (null, "IsDefault" or "IsDefaultOrEmpty") => true,
            (Obj o, _) => ObjProperty(o, name),
            (Dict d, "Keys") => d.Select(x => x.Key).ToList(),
            (Dict d, "Values") => d.Select(x => x.Value.Get()).ToList(),
            (Dict d, "Count") => d.Count,
            _ => throw new Unsupported($"member {name} of {target?.GetType().Name ?? "null"}"),
        };
    }

    static object? ObjProperty(Obj o, string name) => o.Props.TryGetValue(name, out var c) ? c.Get() : throw new Unsupported($"property {name} of {o.Type}");

    object? Invoke(InvocationExpressionSyntax inv, Scope scope)
    {
        var argx = inv.ArgumentList.Arguments.Select(a => a.Expression).ToList();
        object? Arg(int i) => Eval(argx[i], scope);
        if (inv.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" })
            return argx[0] switch { IdentifierNameSyntax i => i.Identifier.ValueText, MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText, var x => x.ToString() };
        if (inv.Expression is IdentifierNameSyntax or GenericNameSyntax)
        {
            var name = ((SimpleNameSyntax)inv.Expression).Identifier.ValueText;
            if (scope.Find(name)?.Get() is Lambda lam) return Apply(lam, argx.Select(a => Eval(a, scope)).ToArray());
            var m = FindMethod(scope.Type, name, argx.Count) ?? throw new Unsupported($"unknown method {name}");
            return CallMethod(m, argx.Select(a => new Cell(() => Eval(a, scope))).ToList(), scope);
        }
        if (inv.Expression is not MemberAccessExpressionSyntax ma) throw new Unsupported($"call {inv.Expression.Kind()}");
        var mname = ma.Name.Identifier.ValueText;
        var recv = ma.Expression.ToString();
        switch (recv, mname)
        {
            case ("string" or "String", "Join"):
            {
                var sep = Str(Arg(0));
                var items = argx.Count == 2 && Eval(argx[1], scope) is List<object?> l ? l : argx.Skip(1).Select(a => Eval(a, scope)).ToList();
                return string.Join(sep, items.Select(Str));
            }
            case ("string" or "String", "Concat"):
                if (argx.Count == 1 && Arg(0) is List<object?> cl) return string.Concat(cl.Select(Str));
                return string.Concat(argx.Select(a => Str(Eval(a, scope))));
            case ("Enumerable", "Range"):
            {
                var start = Convert.ToInt32(Arg(0)); var count = Convert.ToInt32(Arg(1));
                return Enumerable.Range(start, count).Select(i => (object?)i).ToList();
            }
            case ("Enumerable", "Repeat"):
            {
                var v = Arg(0); var count = Convert.ToInt32(Arg(1));
                return Enumerable.Repeat(v, count).ToList();
            }
            case ("ImmutableArray", "Create"):
                if (argx.Count == 1 && Arg(0) is List<object?> one) return one;
                return argx.Select(a => Eval(a, scope)).ToList();
            case ("ImmutableArray", "CreateRange"): return AsList(Arg(0)).ToList();
            case ("Environment", "NewLine"): throw new Unsupported("Environment.NewLine");
            case ("Math", "Max"): return Binary(SyntaxKind.GreaterThanExpression, Arg(0), Arg(1)) is true ? Arg(0) : Arg(1);
            case ("Math", "Min"): return Binary(SyntaxKind.LessThanExpression, Arg(0), Arg(1)) is true ? Arg(0) : Arg(1);
            case ("Math", "Abs"): { var v = Arg(0); return Binary(SyntaxKind.LessThanExpression, v, 0) is true ? Binary(SyntaxKind.SubtractExpression, 0, v) : v; }
        }
        if (ma.Expression is IdentifierNameSyntax tid && scope.Find(tid.Identifier.ValueText) is null && FindMember(scope.Type, tid.Identifier.ValueText) is null
            && TypeByName(tid.Identifier.ValueText) is { } type)
        {
            var m = FindMethod(type, mname, argx.Count) ?? throw new Unsupported($"unknown method {tid}.{mname}");
            return CallMethod(m, argx.Select(a => new Cell(() => Eval(a, scope))).ToList(), scope);
        }
        if (mname == "GetManifestResourceStream" && argx.Count == 1)
        {
            var path = Path.Combine(ResourceRoot ?? throw new Unsupported("no resource root"), Str(Arg(0)));
            return File.Exists(path) ? new Resource(path) : throw new Unsupported($"resource {Str(Arg(0))} not found");
        }
        var target = Eval(ma.Expression, scope);
        // an extension method of the test sources (fixture.WithFullExecutionMatrix())
        if (target is Obj && FindExtension(mname, argx.Count + 1) is { } ext)
            return CallMethod(ext, new[] { Cell.Of(target) }.Concat(argx.Select(a => new Cell(() => Eval(a, scope)))).ToList(), scope);
        switch (target)
        {
            case Resource res when mname == "ReadToEnd": return File.ReadAllText(res.Path);
            case string s:
                return mname switch
                {
                    "Replace" => s.Replace(Str(Arg(0)), Str(Arg(1)), StringComparison.Ordinal),
                    "Trim" when argx.Count == 0 => s.Trim(),
                    "TrimEnd" when argx.Count == 0 => s.TrimEnd(),
                    "TrimStart" when argx.Count == 0 => s.TrimStart(),
                    "ReplaceLineEndings" when argx.Count == 1 => s.ReplaceLineEndings(Str(Arg(0))),
                    "ToString" => s,
                    "ToUpperInvariant" => s.ToUpperInvariant(),
                    "ToLowerInvariant" => s.ToLowerInvariant(),
                    "Substring" when argx.Count == 1 => s[Convert.ToInt32(Arg(0))..],
                    "Substring" => s.Substring(Convert.ToInt32(Arg(0)), Convert.ToInt32(Arg(1))),
                    "Contains" => s.Contains(Str(Arg(0)), StringComparison.Ordinal),
                    _ => throw new Unsupported($"string.{mname}"),
                };
            case StringBuilder sb:
                switch (mname)
                {
                    case "Append": foreach (var a in argx) sb.Append(Str(Eval(a, scope))); return sb;
                    case "AppendLine": if (argx.Count > 0) sb.Append(Str(Arg(0))); sb.Append('\n'); return sb;
                    case "ToString": return sb.ToString();
                }
                throw new Unsupported($"StringBuilder.{mname}");
            case List<object?> list:
                switch (mname)
                {
                    case "ToImmutableArray" or "ToArray" or "ToList" or "AsSpan" or "ToImmutableHashSet" or "ToHashSet" or "ToImmutableList": return list.ToList();
                    case "Add" when argx.Count > 1: list.Add(argx.Select(a => Eval(a, scope)).ToList()); return null; // TheoryData<A, B>.Add(a, b): a row
                    case "Add": list.Add(Arg(0)); return null;
                    case "AddRange": list.AddRange(AsList(Arg(0))); return null;
                    case "Concat": return list.Concat(AsList(Arg(0))).ToList();
                    case "Append": return list.Append(Arg(0)).ToList();
                    case "Distinct": return list.Distinct().ToList();
                    case "Skip": return list.Skip(Convert.ToInt32(Arg(0))).ToList();
                    case "Take": return list.Take(Convert.ToInt32(Arg(0))).ToList();
                    case "Reverse": { var r = list.ToList(); r.Reverse(); return r; }
                    case "Select" when Arg(0) is Lambda f:
                        return list.Select((x, i) => Apply(f, f.Params.Count == 2 ? [x, i] : [x])).ToList();
                    case "SelectMany" when Arg(0) is Lambda f: return list.SelectMany(x => AsList(Apply(f, [x]))).ToList();
                    case "Where" when Arg(0) is Lambda f: return list.Where(x => Truth(Apply(f, [x]))).ToList();
                    case "Contains": { var v = Normalize(Arg(0)); return list.Any(x => Equals(Normalize(x), v)); }
                    case "Except": { var ex = AsList(Arg(0)).Select(Normalize).ToHashSet(); return list.Where(x => !ex.Contains(Normalize(x))).ToList(); }
                    case "Order": return list.OrderBy(x => x).ToList();
                }
                throw new Unsupported($"list.{mname}");
            case IFormattable f when mname == "ToString":
                return f.ToString(argx.Count > 0 && Arg(0) is string fmt ? fmt : null, CultureInfo.InvariantCulture);
            case bool bo when mname == "ToString": return bo.ToString();
            case EnumV ev when mname == "ToString": return ev.ToString();
            case Lambda lam when mname == "Invoke": return Apply(lam, argx.Select(a => Eval(a, scope)).ToArray());
        }
        throw new Unsupported($"call {mname} on {target?.GetType().Name ?? "null"}");
    }

    MethodDeclarationSyntax? FindExtension(string name, int argc)
    {
        foreach (var t in AllTypes)
            if (t.Modifiers.Any(SyntaxKind.StaticKeyword))
                foreach (var m in t.Members.OfType<MethodDeclarationSyntax>())
                    if (m.Identifier.ValueText == name && m.ParameterList.Parameters.FirstOrDefault()?.Modifiers.Any(SyntaxKind.ThisKeyword) == true && Accepts(m.ParameterList, argc))
                        return m;
        return null;
    }

    object? Apply(Lambda f, object?[] args)
    {
        var scope = new Scope(f.Scope, null);
        for (var i = 0; i < f.Params.Count; i++) scope.Declare(f.Params[i], Cell.Of(i < args.Length ? args[i] : null));
        return f.Body switch
        {
            ExpressionSyntax x => Eval(x, scope),
            BlockSyntax b => RunBody(b, scope),
            _ => throw new Unsupported("lambda body"),
        };
    }

    // ---- values ----

    public static List<object?> AsList(object? v) => v switch
    {
        List<object?> l => l,
        string s => s.Select(c => (object?)c).ToList(),
        Dict d => d.Select(x => (object?)new List<object?> { x.Key, x.Value.Get() }).ToList(),
        _ => throw new Unsupported($"not a collection: {v?.GetType().Name ?? "null"}"),
    };

    public static bool Truth(object? v) => v is bool b ? b : throw new Unsupported("not a bool");

    public static string Str(object? v) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? "True" : "False",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    static object? Normalize(object? v) => v switch { int i => (long)i, uint u => (long)u, short s => (long)s, byte b => (long)b, char c => (long)c, ulong ul when ul <= long.MaxValue => (long)ul, _ => v };

    static object? Cast(string type, object? v) => (type, v) switch
    {
        ("int", IConvertible c) => unchecked((int)Convert.ToInt64(c is double or float ? Math.Truncate(Convert.ToDouble(c)) : c)),
        ("long", IConvertible c) => Convert.ToInt64(c),
        ("uint", IConvertible c) => unchecked((uint)Convert.ToInt64(c)),
        ("double", IConvertible c) => Convert.ToDouble(c),
        ("char", IConvertible c) => (char)Convert.ToInt32(c),
        ("string" or "string?", _) => v,
        ("object", _) => v,
        _ => throw new Unsupported($"cast to {type}"),
    };

    static object? Binary(SyntaxKind op, object? a, object? b)
    {
        if (op == SyntaxKind.AddExpression && (a is string || b is string)) return Str(a) + Str(b);
        if (op is SyntaxKind.EqualsExpression) return Equals(Normalize(a), Normalize(b));
        if (op is SyntaxKind.NotEqualsExpression) return !Equals(Normalize(a), Normalize(b));
        if (a is bool x && b is bool y)
            return op switch { SyntaxKind.BitwiseAndExpression => x & y, SyntaxKind.BitwiseOrExpression => x | y, SyntaxKind.ExclusiveOrExpression => x ^ y, _ => throw new Unsupported($"bool {op}") };
        if (a is double or float || b is double or float)
        {
            double p = Convert.ToDouble(a), q = Convert.ToDouble(b);
            return op switch
            {
                SyntaxKind.AddExpression => p + q, SyntaxKind.SubtractExpression => p - q, SyntaxKind.MultiplyExpression => p * q, SyntaxKind.DivideExpression => p / q,
                SyntaxKind.LessThanExpression => p < q, SyntaxKind.GreaterThanExpression => p > q, SyntaxKind.LessThanOrEqualExpression => p <= q, SyntaxKind.GreaterThanOrEqualExpression => p >= q,
                _ => throw new Unsupported($"double {op}"),
            };
        }
        if (a is char ca) a = (int)ca;
        if (b is char cb) b = (int)cb;
        if (a is int i && b is int j)
            return op switch
            {
                SyntaxKind.AddExpression => unchecked(i + j), SyntaxKind.SubtractExpression => unchecked(i - j), SyntaxKind.MultiplyExpression => unchecked(i * j),
                SyntaxKind.DivideExpression => i / j, SyntaxKind.ModuloExpression => i % j,
                SyntaxKind.LeftShiftExpression => i << j, SyntaxKind.RightShiftExpression => i >> j,
                SyntaxKind.BitwiseAndExpression => i & j, SyntaxKind.BitwiseOrExpression => i | j, SyntaxKind.ExclusiveOrExpression => i ^ j,
                SyntaxKind.LessThanExpression => i < j, SyntaxKind.GreaterThanExpression => i > j, SyntaxKind.LessThanOrEqualExpression => i <= j, SyntaxKind.GreaterThanOrEqualExpression => i >= j,
                _ => throw new Unsupported($"int {op}"),
            };
        if (a is IConvertible && b is IConvertible && a is not string && b is not string)
        {
            long p = Convert.ToInt64(a), q = Convert.ToInt64(b);
            return op switch
            {
                SyntaxKind.AddExpression => unchecked(p + q), SyntaxKind.SubtractExpression => unchecked(p - q), SyntaxKind.MultiplyExpression => unchecked(p * q),
                SyntaxKind.DivideExpression => p / q, SyntaxKind.ModuloExpression => p % q,
                SyntaxKind.LeftShiftExpression => p << (int)q, SyntaxKind.RightShiftExpression => p >> (int)q,
                SyntaxKind.BitwiseAndExpression => p & q, SyntaxKind.BitwiseOrExpression => p | q, SyntaxKind.ExclusiveOrExpression => p ^ q,
                SyntaxKind.LessThanExpression => p < q, SyntaxKind.GreaterThanExpression => p > q, SyntaxKind.LessThanOrEqualExpression => p <= q, SyntaxKind.GreaterThanOrEqualExpression => p >= q,
                _ => throw new Unsupported($"long {op}"),
            };
        }
        throw new Unsupported($"operator {op} on {a?.GetType().Name ?? "null"} and {b?.GetType().Name ?? "null"}");
    }
}
