using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// Plugin interface version 6: the closed-form solver (audit A4.6).
internal sealed partial class PluginHost
{
    public ClosedForm Solve(IMethodSymbol tick) => new Solver(this).SolveMethod(tick);
    public ClosedForm Solve(IOperation loop) => new Solver(this).SolveLoop(loop);
    internal IOperation? BodyOf(IMethodSymbol m) => Body(m);
}

/// <summary>Symbolic execution of a per-tick method or a loop body over Roslyn's operation tree: each state variable's
/// new value as an affine function of the old state vector, in pieces chosen by guards on the state; then how each
/// guard moves under its piece, and C# that advances the state n ticks (audit A4.6).</summary>
internal sealed class Solver
{
    internal enum NK { I32, I64, F32, F64 }
    static bool IsF(NK k) => k >= NK.F32;
    static int Width(NK k) => k == NK.I32 ? 32 : 64;
    static NK? KindOf(ITypeSymbol? t) => t?.SpecialType switch
    {
        SpecialType.System_Int32 => NK.I32, SpecialType.System_Int64 => NK.I64,
        SpecialType.System_Single => NK.F32, SpecialType.System_Double => NK.F64, _ => null,
    };
    static string TypeName(NK k) => k switch { NK.I32 => "int", NK.I64 => "long", NK.F32 => "float", _ => "double" };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    sealed class Fail(string reason, IOperation? at) : Exception(reason) { public IOperation? At { get; } = at; }

    /// <summary>A value: C + Σ X[j]·x[j], in the domain of K (integers modulo 2^width, or reals for floating point);
    /// Code is exact C# for it when it does not depend on the state and the solver has it from the program.</summary>
    internal sealed class Lin
    {
        public NK K;
        public SolverTerm C = null!;
        public SolverTerm?[] X = null!;
        public string? Code;
        public bool StateFree => X.All(t => t == null || t.IsZero);
    }

    /// <summary>An invariant condition read where a branch needs a value.</summary>
    sealed class BoolInv(SolverAtom atom) { public SolverAtom Atom { get; } = atom; }

    sealed class Path
    {
        public List<SolverGuard> G = [];
        public Dictionary<ISymbol, object> Env = new(SymbolEqualityComparer.Default);
        public bool Ended, Throws;
        public object? Ret;
        public Path Copy() => new() { G = [.. G], Env = new(Env, SymbolEqualityComparer.Default), Ended = Ended, Throws = Throws, Ret = Ret };
    }

    const int MaxPieces = 64;
    readonly PluginHost _host;
    public Solver(PluginHost host) { _host = host; }

    IMethodSymbol _method = null!;
    INamedTypeSymbol? _self;
    bool _loop, _usedSelf, _approx;
    ILabelSymbol? _continue;
    readonly List<SolverVariable> _vars = [];
    readonly Dictionary<ISymbol, int> _index = new(SymbolEqualityComparer.Default);
    readonly List<NK> _vk = [];
    readonly HashSet<ISymbol> _temps = new(SymbolEqualityComparer.Default);
    readonly List<ISymbol> _outer = []; // (a loop's invariant locals and parameters, in order of first use)
    readonly List<SolverAtom> _atoms = [];
    readonly Dictionary<string, SolverAtom> _atomByKey = [];
    readonly List<IMethodSymbol> _stack = [];
    int _frames;

    // ---------------- entry points ----------------
    public ClosedForm SolveMethod(IMethodSymbol tick)
    {
        tick = tick.OriginalDefinition;
        _method = tick;
        var body = _host.BodyOf(tick);
        try
        {
            if (body == null) throw new Fail($"{tick.Name} has no body in the program's source", null);
            CheckOwner(tick, body);
            if (tick.IsGenericMethod) throw new Fail($"{tick.Name} is generic", body);
            if (tick.IsAsync || tick.IsIterator) throw new Fail($"{tick.Name} is async or an iterator", body);
            if (tick.Parameters.Any(p => p.RefKind != RefKind.None)) throw new Fail($"{tick.Name} takes a ref, in or out parameter", body);
            foreach (var p in tick.Parameters) _temps.Add(p);
            Prescan([body]);
            var paths = ExecRoot(body, [new Path()]);
            return Finish(body, paths);
        }
        catch (Fail f) { return new ClosedForm { Region = body, Method = tick, Solved = false, Reason = f.Message, ReasonAt = f.At }; }
    }

    public ClosedForm SolveLoop(IOperation loopOp)
    {
        var method = loopOp.SemanticModel?.GetEnclosingSymbol(loopOp.Syntax.SpanStart) as IMethodSymbol;
        _method = method?.OriginalDefinition!;
        _loop = true;
        try
        {
            if (loopOp is not ILoopOperation loop || method == null) throw new Fail("not a loop in a method", loopOp);
            CheckOwner(method, loopOp);
            if (method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction) throw new Fail("the loop is in a lambda or a local function", loopOp);
            if (loop is IForEachLoopOperation) throw new Fail("a foreach loop runs an enumerator: the solver follows for, while and do loops", loopOp);
            _continue = loop.ContinueLabel;
            var parts = new List<IOperation> { loop.Body };
            if (loop is IForLoopOperation f) parts.AddRange(f.AtLoopBottom);
            Prescan(parts);
            var paths = new List<Path> { new() };
            paths = Exec(loop.Body, paths);
            foreach (var p in paths) if (p.Ended && !p.Throws) p.Ended = false; // (continue goes to the bottom)
            if (loop is IForLoopOperation fl) foreach (var s in fl.AtLoopBottom) paths = Exec(s, paths);
            return Finish(loopOp, paths);
        }
        catch (Fail x) { return new ClosedForm { Region = loopOp, Method = _method!, IsLoop = true, Solved = false, Reason = x.Message, ReasonAt = x.At }; }
    }

    void CheckOwner(IMethodSymbol m, IOperation at)
    {
        if (!m.IsStatic)
        {
            _self = m.ContainingType;
            if (_self.IsGenericType || _self.ContainingType?.IsGenericType == true) throw new Fail($"{_self.Name} is generic", at);
        }
    }

    List<Path> ExecRoot(IOperation body, List<Path> paths)
    {
        paths = Exec(body, paths);
        foreach (var p in paths) if (!p.Throws) p.Ended = false;
        return paths;
    }

    // ---------------- what the region writes ----------------
    void Prescan(List<IOperation> parts)
    {
        var all = parts.SelectMany(p => p.DescendantsAndSelf()).ToList();
        foreach (var d in all.OfType<IVariableDeclaratorOperation>()) _temps.Add(d.Symbol);
        foreach (var o in all)
        {
            IOperation? target = o switch
            {
                ISimpleAssignmentOperation a => a.Target,
                ICompoundAssignmentOperation c => c.Target,
                IIncrementOrDecrementOperation i => i.Target,
                _ => null,
            };
            if (target == null || TargetSymbol(target) is not { } s || _temps.Contains(s) || _index.ContainsKey(s)) continue;
            var type = s switch { IFieldSymbol f => f.Type, IPropertySymbol p => p.Type, ILocalSymbol l => l.Type, IParameterSymbol p => p.Type, _ => null };
            if (KindOf(type) is not { } k)
                throw new Fail($"the state {s.Name} is a {type}: the solver follows int, long, float and double state", o);
            _index[s] = _vars.Count;
            _vars.Add(new SolverVariable(s, type!, _vars.Count));
            _vk.Add(k);
            if (s is ILocalSymbol or IParameterSymbol) UseOuter(s);
        }
    }

    void UseOuter(ISymbol s) { if (!_outer.Contains(s, SymbolEqualityComparer.Default)) _outer.Add(s); }

    ISymbol? TargetSymbol(IOperation t) => t switch
    {
        ILocalReferenceOperation l => l.Local,
        IParameterReferenceOperation p => p.Parameter,
        IFieldReferenceOperation f when OwnInstance(f.Instance) => f.Field.OriginalDefinition,
        IPropertyReferenceOperation p when OwnInstance(p.Instance) && p.Arguments.Length == 0 && IsAuto(p.Property) => p.Property.OriginalDefinition,
        _ => null,
    };

    static bool OwnInstance(IOperation? i) => i is null or IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance };

    static bool IsAuto(IPropertySymbol p)
    {
        foreach (var r in p.OriginalDefinition.DeclaringSyntaxReferences)
            if (r.GetSyntax() is PropertyDeclarationSyntax { ExpressionBody: null, AccessorList: { } al } && al.Accessors.All(a => a.Body == null && a.ExpressionBody == null))
                return true;
        return false;
    }

    // ---------------- terms ----------------
    static SolverTerm Zero(NK k) => new(IsF(k), Width(k), []);
    static SolverTerm Num(NK k, long i, double f)
    {
        var t = Zero(k);
        if (IsF(k)) { if (f != 0) t.M[""] = (0, f, []); }
        else { i = Norm(i, Width(k)); if (i != 0) t.M[""] = (i, 0, []); }
        return t;
    }
    static SolverTerm One(NK k) => Num(k, 1, 1);
    static long Norm(long v, int w) => w == 32 ? (int)v : v;
    static SolverTerm OfAtom(NK k, SolverAtom a) { var t = Zero(k); t.M[a.Id.ToString(Inv)] = (1, 1, [a]); return t; }

    static SolverTerm Add(SolverTerm a, SolverTerm b, int sign = 1)
    {
        var r = new SolverTerm(a.IsFloat, a.Width, new(a.M));
        foreach (var (key, (i, f, fs)) in b.M)
        {
            var (ri, rf, _) = r.M.TryGetValue(key, out var e) ? e : (0, 0, fs);
            long ni = Norm(unchecked(ri + sign * i), a.Width); double nf = rf + sign * f;
            if (a.IsFloat ? nf == 0 : ni == 0) r.M.Remove(key); else r.M[key] = (ni, nf, fs);
        }
        return r;
    }

    static SolverTerm Mul(SolverTerm a, SolverTerm b)
    {
        var r = new SolverTerm(a.IsFloat, a.Width, []);
        foreach (var (_, (ai, af, afs)) in a.M)
            foreach (var (_, (bi, bf, bfs)) in b.M)
            {
                var fs = afs.Concat(bfs).OrderBy(x => x.Id).ToArray();
                var one = new SolverTerm(a.IsFloat, a.Width, []) { };
                one.M[string.Join(",", fs.Select(x => x.Id))] = (Norm(unchecked(ai * bi), a.Width), af * bf, fs);
                r = Add(r, one);
            }
        return r;
    }

    /// <summary>A term in another domain: an int's to a long's (only a number widens: it is sign-extended already), a
    /// long's to an int's (modulo 2^32), an integer's to a real one.</summary>
    static SolverTerm Retype(SolverTerm t, NK k)
    {
        var r = Zero(k);
        foreach (var (key, (i, f, fs)) in t.M)
        {
            long ni = t.IsFloat ? 0 : Norm(i, Width(k)); double nf = t.IsFloat ? f : i;
            if (IsF(k) ? nf != 0 : ni != 0) r.M[key] = (ni, nf, fs);
        }
        return r;
    }

    static string TermKey(SolverTerm t) => string.Join(";", t.M.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + (t.IsFloat ? kv.Value.F.ToString("R", Inv) : kv.Value.I.ToString(Inv))));
    static string LinKey(Lin l) => TermKey(l.C) + "|" + string.Join("|", l.X.Select((x, j) => x == null || x.IsZero ? "" : j + ":" + TermKey(x)));

    // ---------------- values ----------------
    Lin New(NK k, SolverTerm c, string? code = null) => new() { K = k, C = c, X = new SolverTerm?[_vars.Count], Code = code };
    Lin Const(NK k, long i, double f) => New(k, Num(k, i, f), Literal(k, i, f));
    Lin Var(int j) { var l = New(_vk[j], Zero(_vk[j])); l.X[j] = One(_vk[j]); return l; }

    Lin Invariant(NK k, ITypeSymbol type, string code, SolverAtomKind kind, ISymbol? sym, IOperation? at)
    {
        var a = Atom(kind, sym, at, type, code);
        return New(k, OfAtom(k, a), code);
    }

    SolverAtom Atom(SolverAtomKind kind, ISymbol? sym, IOperation? at, ITypeSymbol type, string code)
    {
        var key = type.ToDisplayString() + " " + code;
        if (_atomByKey.TryGetValue(key, out var a)) return a;
        a = new SolverAtom(_atoms.Count, kind, sym, at, type, code);
        _atoms.Add(a); _atomByKey[key] = a;
        return a;
    }

    static string Literal(NK k, long i, double f) => k switch
    {
        NK.I32 => (int)i == int.MinValue ? "(-2147483647 - 1)" : (int)i < 0 ? "(" + ((int)i).ToString(Inv) + ")" : ((int)i).ToString(Inv),
        NK.I64 => i == long.MinValue ? "(-9223372036854775807L - 1L)" : i < 0 ? "(" + i.ToString(Inv) + "L)" : i.ToString(Inv) + "L",
        NK.F32 => float.IsNaN((float)f) ? "float.NaN" : float.IsPositiveInfinity((float)f) ? "float.PositiveInfinity" : float.IsNegativeInfinity((float)f) ? "float.NegativeInfinity"
            : "(" + ((float)f).ToString("R", Inv) + "f)",
        _ => double.IsNaN(f) ? "double.NaN" : double.IsPositiveInfinity(f) ? "double.PositiveInfinity" : double.IsNegativeInfinity(f) ? "double.NegativeInfinity"
            : "(" + f.ToString("R", Inv) + "d)",
    };

    Lin Plus(Lin a, Lin b, int sign, string? code)
    {
        var r = New(a.K, Add(a.C, b.C, sign), code);
        for (int j = 0; j < r.X.Length; j++)
            r.X[j] = a.X[j] == null ? (b.X[j] == null ? null : Add(Zero(a.K), b.X[j]!, sign)) : b.X[j] == null ? a.X[j] : Add(a.X[j]!, b.X[j]!, sign);
        return r;
    }

    Lin Scale(Lin a, SolverTerm t, string? code)
    {
        var r = New(a.K, Mul(a.C, t), code);
        for (int j = 0; j < r.X.Length; j++) r.X[j] = a.X[j] == null ? null : Mul(a.X[j]!, t);
        return r;
    }

    /// <summary>C# for a value that does not depend on the state: the program's own when the solver has it, else its
    /// terms (exact for an integer; for a floating-point value the real sum, so approximate).</summary>
    string CodeOf(Lin l)
    {
        if (l.Code != null) return l.Code;
        if (IsF(l.K)) _approx = true;
        var e = RenderTerm(l.C, IsF(l.K), a => "(" + a.Code + ")");
        return l.K switch { NK.I32 => $"unchecked((int)({e}))", NK.I64 => $"unchecked({e})", NK.F32 => $"((float)({e}))", _ => $"({e})" };
    }

    // ---------------- statements ----------------
    List<Path> Exec(IOperation s, List<Path> paths)
    {
        var r = new List<Path>();
        foreach (var p in paths)
        {
            if (p.Ended || p.Throws) { r.Add(p); continue; }
            r.AddRange(Exec1(s, p));
        }
        if (r.Count(p => !p.Throws) > MaxPieces) throw new Fail($"more than {MaxPieces} pieces", s);
        return r;
    }

    IEnumerable<Path> Exec1(IOperation s, Path p)
    {
        switch (s)
        {
            case IMethodBodyOperation mb:
                return Exec((IOperation?)mb.BlockBody ?? mb.ExpressionBody!, [p]);
            case IBlockOperation b:
                {
                    var ps = new List<Path> { p };
                    foreach (var st in b.Operations) ps = Exec(st, ps);
                    return ps;
                }
            case IExpressionStatementOperation es:
                return Eval(es.Operation, p).Select(x => x.p);
            case IEmptyOperation:
                return [p];
            case IVariableDeclarationGroupOperation g:
                {
                    var ps = new List<Path> { p };
                    foreach (var decl in g.Declarations)
                    {
                        if (decl.IgnoredDimensions.Length > 0 || decl.Initializer != null) throw new Fail("an array declaration", decl);
                        foreach (var d in decl.Declarators)
                        {
                            if (d.Initializer == null) continue;
                            var next = new List<Path>();
                            foreach (var q in ps)
                            {
                                if (q.Throws) { next.Add(q); continue; }
                                foreach (var (q2, v) in Eval(d.Initializer.Value, q)) { if (!q2.Throws) Set(q2, d.Symbol, v, d); next.Add(q2); }
                            }
                            ps = next;
                        }
                    }
                    return ps;
                }
            case IConditionalOperation c:
                {
                    var r = new List<Path>();
                    foreach (var (q, v) in Eval(c.Condition, p))
                    {
                        if (q.Throws) { r.Add(q); continue; }
                        var branch = (bool)v! ? c.WhenTrue : c.WhenFalse;
                        if (branch == null) r.Add(q); else r.AddRange(Exec(branch, [q]));
                    }
                    return r;
                }
            case IReturnOperation ret when ret.Kind == OperationKind.Return:
                {
                    if (_loop && _frames == 0) throw new Fail("a return leaves the loop", s);
                    if (ret.ReturnedValue == null) { p.Ended = true; return [p]; }
                    return Eval(ret.ReturnedValue, p).Select(x => { if (!x.p.Throws) { x.p.Ended = true; x.p.Ret = x.v; } return x.p; });
                }
            case IBranchOperation { BranchKind: BranchKind.Continue } br when _loop && _frames == 0 && SymbolEqualityComparer.Default.Equals(br.Target, _continue):
                p.Ended = true;
                return [p];
            case IThrowOperation:
                p.Throws = true;
                return [p];
            case ILoopOperation:
                throw new Fail("a loop inside the region", s);
            default:
                throw new Fail($"a {Describe(s)} statement: the solver follows assignments, declarations, if, return and calls it can expand", s);
        }
    }

    static string Describe(IOperation o) => o.Kind.ToString().ToLowerInvariant();

    void Set(Path p, ISymbol sym, object? v, IOperation at)
    {
        sym = sym is ILocalSymbol or IParameterSymbol ? sym : sym.OriginalDefinition;
        if (_index.TryGetValue(sym, out int j))
        {
            if (v is not Lin l || l.K != _vk[j]) throw new Fail($"{sym.Name} is given a value the solver does not follow", at);
            p.Env[sym] = l;
            return;
        }
        if (!_temps.Contains(sym)) throw new Fail($"writes {sym.Name}, which the region does not own (an expanded method writes nothing)", at);
        if (v is BoolInv) throw new Fail("a condition kept in a local", at);
        p.Env[sym] = v!;
    }

    // ---------------- expressions ----------------
    List<(Path p, object? v)> Eval(IOperation e, Path p)
    {
        if (p.Throws) return [(p, null)];
        if (e.ConstantValue is { HasValue: true } cv && e.Kind is not (OperationKind.SimpleAssignment or OperationKind.CompoundAssignment))
        {
            if (cv.Value is bool b) return [(p, b)];
            if (KindOf(e.Type) is not { } k) throw new Fail($"a constant of type {e.Type}", e);
            return [(p, k switch
            {
                NK.I32 => Const(k, Convert.ToInt32(cv.Value, Inv), 0),
                NK.I64 => Const(k, Convert.ToInt64(cv.Value, Inv), 0),
                NK.F32 => Const(k, 0, Convert.ToSingle(cv.Value, Inv)),
                _ => Const(k, 0, Convert.ToDouble(cv.Value, Inv)),
            })];
        }
        switch (e)
        {
            case ILocalReferenceOperation l: return Read(l.Local, e, p);
            case IParameterReferenceOperation pr: return Read(pr.Parameter, e, p);
            case IFieldReferenceOperation f:
                if (!OwnInstance(f.Instance)) throw new Fail($"reads {f.Field.Name} of another object", e);
                return Read(f.Field.OriginalDefinition, e, p);
            case IPropertyReferenceOperation pr:
                if (!OwnInstance(pr.Instance) || pr.Arguments.Length > 0) throw new Fail($"reads {pr.Property.Name} of another object or an indexer", e);
                if (IsAuto(pr.Property)) return Read(pr.Property.OriginalDefinition, e, p);
                if (pr.Property.GetMethod is not { } getter) throw new Fail($"{pr.Property.Name} has no getter", e);
                return Inline(getter, [], e, p);
            case IConversionOperation c:
                {
                    if (c.OperatorMethod != null) throw new Fail("a user-defined conversion", e);
                    if (c.IsTryCast) throw new Fail("an as conversion", e);
                    return Then(Eval(c.Operand, p), (q, v) => [(q, Conv(v, c.Operand.Type, c.Type, c.IsChecked, e))]);
                }
            case IUnaryOperation u:
                {
                    if (u.OperatorMethod != null) throw new Fail("a user-defined operator", e);
                    return Then(Eval(u.Operand, p), (q, v) => [(q, Unary(u, v))]);
                }
            case IBinaryOperation b:
                {
                    if (b.OperatorMethod != null) throw new Fail("a user-defined operator", e);
                    if (b.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
                    {
                        bool and = b.OperatorKind == BinaryOperatorKind.ConditionalAnd;
                        return Then(Eval(b.LeftOperand, p), (q, v) => (bool)v! == and ? Eval(b.RightOperand, q) : [(q, v)]);
                    }
                    return Then(Eval(b.LeftOperand, p), (q, l) => Then(Eval(b.RightOperand, q), (q2, r) => Binary(q2, b, b.OperatorKind, l, r, b.IsChecked)));
                }
            case IConditionalOperation c when c.WhenFalse != null:
                return Then(Eval(c.Condition, p), (q, v) => Eval((bool)v! ? c.WhenTrue : c.WhenFalse, q));
            case ISimpleAssignmentOperation a:
                {
                    if (a.IsRef) throw new Fail("a ref assignment", e);
                    var sym = TargetSymbol(a.Target) ?? throw new Fail($"writes {a.Target.Syntax}, which is not state the solver follows", e);
                    return Then(Eval(a.Value, p), (q, v) => { Set(q, sym, v, e); return [(q, v)]; });
                }
            case ICompoundAssignmentOperation ca:
                {
                    if (ca.OperatorMethod != null) throw new Fail("a user-defined operator", e);
                    var sym = TargetSymbol(ca.Target) ?? throw new Fail($"writes {ca.Target.Syntax}, which is not state the solver follows", e);
                    var opType = ca.InConversion.IsIdentity ? ca.Target.Type : ca.Value.Type;
                    return Then(Read(sym, ca.Target, p), (q, old) =>
                    {
                        var o2 = ca.InConversion.IsIdentity ? old : Conv(old, ca.Target.Type, opType, ca.IsChecked, e);
                        return Then(Eval(ca.Value, q), (q2, v) => Then(Binary(q2, e, ca.OperatorKind, o2, v, ca.IsChecked), (q3, res) =>
                        {
                            var stored = ca.OutConversion.IsIdentity ? res : Conv(res, opType, ca.Target.Type, ca.IsChecked, e);
                            Set(q3, sym, stored, e);
                            return [(q3, stored)];
                        }));
                    });
                }
            case IIncrementOrDecrementOperation inc:
                {
                    if (inc.OperatorMethod != null) throw new Fail("a user-defined operator", e);
                    var sym = TargetSymbol(inc.Target) ?? throw new Fail($"writes {inc.Target.Syntax}, which is not state the solver follows", e);
                    return Then(Read(sym, inc.Target, p), (q, old) =>
                    {
                        if (old is not Lin l) throw new Fail("++ or -- on a value the solver does not follow", e);
                        if (inc.IsChecked && !IsF(l.K)) throw new Fail("checked arithmetic throws on overflow", e);
                        var nv = Plus(l, Const(l.K, 1, 1), inc.Kind == OperationKind.Increment ? 1 : -1, null);
                        Set(q, sym, nv, e);
                        return [(q, inc.IsPostfix ? old : nv)];
                    });
                }
            case IInvocationOperation inv:
                return Call(inv, p);
            case IThrowOperation:
                p.Throws = true;
                return [(p, null)];
            case IParenthesizedOperation par:
                return Eval(par.Operand, p);
            default:
                throw new Fail($"a {Describe(e)} expression ({e.Syntax}): the solver follows arithmetic, comparisons, conversions and calls it can expand", e);
        }
    }

    static List<(Path p, object? v)> Then(List<(Path p, object? v)> xs, Func<Path, object?, List<(Path p, object? v)>> f)
    {
        var r = new List<(Path, object?)>();
        foreach (var (p, v) in xs)
        {
            if (p.Throws) { r.Add((p, null)); continue; }
            r.AddRange(f(p, v));
        }
        return r;
    }

    List<(Path p, object? v)> Read(ISymbol sym, IOperation at, Path p)
    {
        if (p.Env.TryGetValue(sym, out var v)) return Bool([(p, v)], at);
        if (_index.TryGetValue(sym, out int j)) return [(p, Var(j))];
        if (_temps.Contains(sym) && sym is ILocalSymbol) throw new Fail($"{sym.Name} is read before the region writes it", at);
        var type = sym switch { IFieldSymbol f => f.Type, IPropertySymbol pp => pp.Type, ILocalSymbol l => l.Type, IParameterSymbol pa => pa.Type, _ => null };
        string code;
        SolverAtomKind kind;
        switch (sym)
        {
            case IParameterSymbol ps:
                if (_frames > 0 && !SymbolEqualityComparer.Default.Equals(ps.ContainingSymbol, _method)) throw new Fail($"{ps.Name} is read before it is bound", at);
                code = "@" + ps.Name; kind = SolverAtomKind.Parameter; if (_loop) UseOuter(ps); break;
            case ILocalSymbol ls:
                code = "@" + ls.Name; kind = SolverAtomKind.Local; UseOuter(ls); break;
            default:
                if (sym.IsStatic) code = sym.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ".@" + sym.Name;
                else { code = "__self.@" + sym.Name; _usedSelf = true; }
                kind = SolverAtomKind.Member; break;
        }
        if (type?.SpecialType == SpecialType.System_Boolean)
            return Bool([(p, new BoolInv(Atom(kind, sym, at, type, code)))], at);
        if (KindOf(type) is not { } k) throw new Fail($"{sym.Name} is a {type}: the solver follows int, long, float, double and bool values", at);
        return [(p, Invariant(k, type!, code, kind, sym, at))];
    }

    /// <summary>An invariant condition becomes two pieces where a branch needs its value.</summary>
    List<(Path p, object? v)> Bool(List<(Path p, object? v)> xs, IOperation at)
    {
        var r = new List<(Path, object?)>();
        foreach (var (p, v) in xs)
        {
            if (v is not BoolInv bi) { r.Add((p, v)); continue; }
            foreach (var (q, holds) in Fork(p, w => new SolverGuard { Operation = at, WhenTrue = w, Invariant = bi.Atom, Key = "inv|" + bi.Atom.Id }, "inv|" + bi.Atom.Id))
                r.Add((q, holds));
        }
        return r;
    }

    List<(Path p, bool holds)> Fork(Path p, Func<bool, SolverGuard> make, string key)
    {
        foreach (var g in p.G) if (g.Key == key) return [(p, g.WhenTrue)];
        var t = p.Copy(); t.G.Add(make(true));
        var f = p; f.G.Add(make(false));
        return [(t, true), (f, false)];
    }

    object Conv(object? v, ITypeSymbol? from, ITypeSymbol? to, bool isChecked, IOperation at)
    {
        if (v is bool && to?.SpecialType == SpecialType.System_Boolean) return v;
        if (v is not Lin l) throw new Fail($"a conversion of a value the solver does not follow", at);
        if (KindOf(to) is not { } tk) throw new Fail($"a conversion to {to}: the solver follows int, long, float and double", at);
        var fk = l.K;
        if (fk == tk) return l;
        if (isChecked && !IsF(tk)) throw new Fail("a checked conversion throws on overflow", at);
        string? code = l.Code != null ? $"(({TypeName(tk)})({l.Code}))" : null;
        bool free = l.StateFree;
        if (free && l.C.IsConstant)
        {
            // (a number converts as C# converts it)
            long i = l.C.IntegerValue; double d = l.C.Value;
            return tk switch
            {
                NK.I32 => Const(tk, IsF(fk) ? unchecked((int)d) : (int)i, 0),
                NK.I64 => Const(tk, IsF(fk) ? unchecked((long)d) : i, 0),
                NK.F32 => Const(tk, 0, IsF(fk) ? (float)d : (float)i),
                _ => Const(tk, 0, IsF(fk) ? d : i),
            };
        }
        if (free) return Invariant(tk, to!, code ?? $"(({TypeName(tk)})({CodeOf(l)}))", SolverAtomKind.Expression, null, at);
        if (!IsF(fk) && !IsF(tk))
        {
            if (tk == NK.I64) throw new Fail("an int that depends on the state widens to long (not affine modulo 2^64)", at);
            var r = New(tk, Retype(l.C, tk));
            for (int j = 0; j < r.X.Length; j++) r.X[j] = l.X[j] == null ? null : Retype(l.X[j]!, tk);
            return r;
        }
        if (IsF(fk) && !IsF(tk)) throw new Fail("a floating-point value that depends on the state converts to an integer (not affine)", at);
        {
            var r = New(tk, Retype(l.C, tk));
            for (int j = 0; j < r.X.Length; j++) r.X[j] = l.X[j] == null ? null : Retype(l.X[j]!, tk);
            return r;
        }
    }

    object Unary(IUnaryOperation u, object? v)
    {
        switch (u.OperatorKind)
        {
            case UnaryOperatorKind.Not when v is bool b: return !b;
            case UnaryOperatorKind.Plus when v is Lin: return v;
            case UnaryOperatorKind.Minus when v is Lin l:
                if (u.IsChecked && !IsF(l.K)) throw new Fail("checked arithmetic throws on overflow", u);
                return Scale(l, Num(l.K, -1, -1), l.Code != null ? $"(-{l.Code})" : null);
            case UnaryOperatorKind.BitwiseNegation when v is Lin l && !IsF(l.K):
                // ~x = -x - 1
                return Plus(Scale(l, Num(l.K, -1, -1), null), Const(l.K, 1, 1), -1, l.Code != null ? $"(~{l.Code})" : null);
            default: throw new Fail($"the operator {u.OperatorKind} here", u);
        }
    }

    List<(Path p, object? v)> Binary(Path p, IOperation at, BinaryOperatorKind k, object? lv, object? rv, bool isChecked)
    {
        if (lv is bool lb && rv is bool rb)
            return k switch
            {
                BinaryOperatorKind.And => [(p, lb & rb)], BinaryOperatorKind.Or => [(p, lb | rb)], BinaryOperatorKind.ExclusiveOr => [(p, lb ^ rb)],
                BinaryOperatorKind.Equals => [(p, lb == rb)], BinaryOperatorKind.NotEquals => [(p, lb != rb)],
                _ => throw new Fail($"the operator {k} on conditions", at),
            };
        if (lv is not Lin l || rv is not Lin r || l.K != r.K) throw new Fail($"the operator {k} on values the solver does not follow", at);
        bool f = IsF(l.K);
        if (isChecked && !f && k is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply)
            throw new Fail("checked arithmetic throws on overflow", at);
        string? Code(string op) => l.Code != null && r.Code != null ? $"({l.Code} {op} {r.Code})" : null;
        bool lf = l.StateFree, rf = r.StateFree;
        switch (k)
        {
            case BinaryOperatorKind.Add: return [(p, Plus(l, r, 1, Code("+")))];
            case BinaryOperatorKind.Subtract: return [(p, Plus(l, r, -1, Code("-")))];
            case BinaryOperatorKind.Multiply:
                if (rf) return [(p, Scale(l, r.C, Code("*")))];
                if (lf) return [(p, Scale(r, l.C, Code("*")))];
                throw new Fail($"a product of two values that depend on the state ({at.Syntax})", at);
            case BinaryOperatorKind.Divide when f:
                {
                    if (!rf) throw new Fail($"a division by a value that depends on the state ({at.Syntax})", at);
                    SolverTerm inv;
                    if (r.C.IsConstant) inv = Num(l.K, 0, 1 / r.C.Value);
                    else inv = OfAtom(l.K, Atom(SolverAtomKind.Expression, null, at, DoubleType(at), $"(1.0 / (double){CodeOf(r)})"));
                    return [(p, Scale(l, inv, Code("/")))];
                }
            case BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder:
                {
                    if (!lf || !rf) throw new Fail($"an integer {(k == BinaryOperatorKind.Divide ? "division" : "remainder")} of a value that depends on the state ({at.Syntax})", at);
                    if (!f && !(r.C.IsConstant && r.C.IntegerValue is not (0 or -1)))
                        throw new Fail($"an integer {(k == BinaryOperatorKind.Divide ? "division" : "remainder")} by an invariant that can be 0 or -1 (it throws)", at);
                    return [(p, Invariant(l.K, at.Type!, $"({CodeOf(l)} {(k == BinaryOperatorKind.Divide ? "/" : "%")} {CodeOf(r)})", SolverAtomKind.Expression, null, at))];
                }
            case BinaryOperatorKind.LeftShift when !f && r.StateFree && r.C.IsConstant:
                {
                    int count = (int)(r.C.IntegerValue & (Width(l.K) - 1));
                    return [(p, Scale(l, Num(l.K, unchecked(1L << count), 0), Code("<<")))];
                }
            case BinaryOperatorKind.LeftShift or BinaryOperatorKind.RightShift or BinaryOperatorKind.UnsignedRightShift
                or BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr when !f:
                {
                    if (!lf || !rf) throw new Fail($"a bitwise operation on a value that depends on the state ({at.Syntax})", at);
                    var op = k switch
                    {
                        BinaryOperatorKind.LeftShift => "<<", BinaryOperatorKind.RightShift => ">>", BinaryOperatorKind.UnsignedRightShift => ">>>",
                        BinaryOperatorKind.And => "&", BinaryOperatorKind.Or => "|", _ => "^",
                    };
                    return [(p, Invariant(l.K, at.Type!, $"({CodeOf(l)} {op} {CodeOf(r)})", SolverAtomKind.Expression, null, at))];
                }
            case BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals or BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual
                or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual:
                return Compare(p, at, k, l, r).Select(x => (x.p, (object?)x.v)).ToList();
            default:
                throw new Fail($"the operator {k} ({at.Syntax})", at);
        }
    }

    ITypeSymbol DoubleType(IOperation at) => (at.SemanticModel?.Compilation ?? _host.Main).GetSpecialType(SpecialType.System_Double);
    ITypeSymbol BoolType(IOperation at) => (at.SemanticModel?.Compilation ?? _host.Main).GetSpecialType(SpecialType.System_Boolean);

    List<(Path p, bool v)> Compare(Path p, IOperation at, BinaryOperatorKind k, Lin l, Lin r)
    {
        bool f = IsF(l.K);
        if (l.StateFree && r.StateFree && l.C.IsConstant && r.C.IsConstant)
        {
            bool res;
            if (f) { double a = l.C.Value, b = r.C.Value; res = k switch { BinaryOperatorKind.Equals => a == b, BinaryOperatorKind.NotEquals => a != b, BinaryOperatorKind.LessThan => a < b, BinaryOperatorKind.LessThanOrEqual => a <= b, BinaryOperatorKind.GreaterThan => a > b, _ => a >= b }; }
            else { long a = l.C.IntegerValue, b = r.C.IntegerValue; res = k switch { BinaryOperatorKind.Equals => a == b, BinaryOperatorKind.NotEquals => a != b, BinaryOperatorKind.LessThan => a < b, BinaryOperatorKind.LessThanOrEqual => a <= b, BinaryOperatorKind.GreaterThan => a > b, _ => a >= b }; }
            return [(p, res)];
        }
        if (l.StateFree && r.StateFree)
        {
            var a = Atom(SolverAtomKind.Expression, null, at, BoolType(at), $"({CodeOf(l)} {SolverGuard.Op(k)} {CodeOf(r)})");
            return Fork(p, w => new SolverGuard { Operation = at, WhenTrue = w, Invariant = a, Key = "inv|" + a.Id }, "inv|" + a.Id);
        }
        bool truth = true;
        if (k == BinaryOperatorKind.NotEquals) { k = BinaryOperatorKind.Equals; truth = false; }
        if (k == BinaryOperatorKind.GreaterThan) { k = BinaryOperatorKind.LessThan; (l, r) = (r, l); }
        if (k == BinaryOperatorKind.GreaterThanOrEqual) { k = BinaryOperatorKind.LessThanOrEqual; (l, r) = (r, l); }
        if (!f && k == BinaryOperatorKind.LessThanOrEqual) { k = BinaryOperatorKind.LessThan; (l, r) = (r, l); truth = !truth; }
        if (k == BinaryOperatorKind.Equals && string.CompareOrdinal(LinKey(l), LinKey(r)) > 0) (l, r) = (r, l);
        var key = k + "|" + LinKey(l) + "|" + LinKey(r);
        var (L, R, K) = (l, r, k);
        return Fork(p, w => new SolverGuard { Operation = at, WhenTrue = w, Comparison = K, L = L, R = R, Key = key }, key)
            .Select(x => (x.p, x.holds == truth)).ToList();
    }

    // ---------------- calls ----------------
    static readonly HashSet<string> s_pureFloat = ["Sqrt", "Cbrt", "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2", "Sinh", "Cosh", "Tanh",
        "Asinh", "Acosh", "Atanh", "Exp", "Log10", "Log2", "Pow", "Floor", "Ceiling", "Truncate", "FusedMultiplyAdd", "CopySign", "MaxMagnitude", "MinMagnitude",
        "ReciprocalEstimate", "ReciprocalSqrtEstimate", "BitIncrement", "BitDecrement", "ScaleB", "IEEERemainder"];

    List<(Path p, object? v)> Call(IInvocationOperation inv, Path p)
    {
        var m = inv.TargetMethod;
        foreach (var a in inv.Arguments)
            if (a.Parameter?.RefKind is not (RefKind.None or null) || a.ArgumentKind == ArgumentKind.ParamArray) throw new Fail($"a call of {m.Name} with a ref, out or params argument", inv);
        var ct = m.ContainingType;
        if (m.IsStatic && ct?.ContainingNamespace?.ToDisplayString() == "System" && ct.Name is "Math" or "MathF" or "Single" or "Double")
            return Known(inv, p);
        if (_host.BodyOf(m) == null)
            throw new Fail($"a call of {ct?.Name}.{m.Name}: the solver expands Math's Min, Max, Abs, Clamp, Lerp and pure functions, and the program's methods that write nothing", inv);
        if (!m.IsStatic && !OwnInstance(inv.Instance)) throw new Fail($"a call of {m.Name} on another object", inv);
        return Inline(m, inv.Arguments.Select(a => a.Value).ToList(), inv, p);
    }

    List<(Path p, object?[] vs)> EvalAll(List<IOperation> args, Path p)
    {
        var acc = new List<(Path p, object?[] vs)> { (p, []) };
        foreach (var a in args)
        {
            var next = new List<(Path, object?[])>();
            foreach (var (q, vs) in acc)
            {
                if (q.Throws) { next.Add((q, vs)); continue; }
                foreach (var (q2, v) in Eval(a, q)) next.Add((q2, [.. vs, v]));
            }
            acc = next;
        }
        return acc;
    }

    List<(Path p, object? v)> Known(IInvocationOperation inv, Path p)
    {
        var m = inv.TargetMethod;
        var name = m.Name;
        var r = new List<(Path, object?)>();
        foreach (var (q, vs) in EvalAll(inv.Arguments.Select(a => a.Value).ToList(), p))
        {
            if (q.Throws) { r.Add((q, null)); continue; }
            var ls = vs.Select(v => v as Lin).ToArray();
            if (ls.Any(x => x == null)) throw new Fail($"{name} of a value the solver does not follow", inv);
            var k = ls.Length > 0 ? ls[0]!.K : NK.F64;
            bool f = IsF(k);
            switch (name)
            {
                case "Min" or "Max" when ls.Length == 2:
                    foreach (var (q2, aFirst) in Compare(q, inv, name == "Min" ? BinaryOperatorKind.LessThanOrEqual : BinaryOperatorKind.GreaterThanOrEqual, ls[0]!, ls[1]!))
                        r.Add((q2, aFirst ? ls[0] : ls[1]));
                    continue;
                case "Abs" when ls.Length == 1:
                    {
                        var x = ls[0]!;
                        var cases = new List<(Path p, bool v)>();
                        if (!f)
                            foreach (var (q2, isMin) in Compare(q, inv, BinaryOperatorKind.Equals, x, Const(k, k == NK.I32 ? int.MinValue : long.MinValue, 0)))
                            {
                                if (isMin) { q2.Throws = true; r.Add((q2, null)); } // (OverflowException)
                                else cases.Add((q2, false));
                            }
                        else cases.Add((q, false));
                        foreach (var (q2, _) in cases)
                            foreach (var (q3, neg) in Compare(q2, inv, BinaryOperatorKind.LessThan, x, Const(k, 0, 0)))
                                r.Add((q3, neg ? Scale(x, Num(k, -1, -1), x.Code != null ? $"(-{x.Code})" : null) : x));
                        continue;
                    }
                case "Clamp" when ls.Length == 3:
                    {
                        var (v, lo, hi) = (ls[0]!, ls[1]!, ls[2]!);
                        foreach (var (q2, wrong) in Compare(q, inv, BinaryOperatorKind.GreaterThan, lo, hi))
                        {
                            if (wrong) { q2.Throws = true; r.Add((q2, null)); continue; } // (ArgumentException)
                            foreach (var (q3, below) in Compare(q2, inv, BinaryOperatorKind.LessThan, v, lo))
                            {
                                if (below) { r.Add((q3, lo)); continue; }
                                foreach (var (q4, above) in Compare(q3, inv, BinaryOperatorKind.GreaterThan, v, hi)) r.Add((q4, above ? hi : v));
                            }
                        }
                        continue;
                    }
                case "Lerp" when ls.Length == 3 && f:
                    {
                        // (value1 * (1 - amount)) + (value2 * amount), as Single.Lerp and Double.Lerp compute it
                        var (a, b, t) = (ls[0]!, ls[1]!, ls[2]!);
                        var oneMinus = Plus(Const(k, 1, 1), t, -1, t.Code != null ? $"({Literal(k, 1, 1)} - {t.Code})" : null);
                        var left = Mul2(a, oneMinus, inv); var right = Mul2(b, t, inv);
                        r.Add((q, Plus(left, right, 1, left.Code != null && right.Code != null ? $"({left.Code} + {right.Code})" : null)));
                        continue;
                    }
            }
            // a pure function of invariants
            bool pure = f ? s_pureFloat.Contains(name) || name is "Min" or "Max" or "Log" && ls.Length <= 2 || name == "Round" && ls.Length == 1
                : name is "Sign" or "Min" or "Max";
            if (KindOf(m.ReturnType) is not { } rk || !pure) throw new Fail($"{m.ContainingType.Name}.{name} here: the solver expands Min, Max, Abs, Clamp, Lerp and pure functions of invariants", inv);
            if (!ls.All(x => x!.StateFree)) throw new Fail($"{m.ContainingType.Name}.{name} of a value that depends on the state is not affine", inv);
            var code = $"{m.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{name}({string.Join(", ", ls.Select(x => CodeOf(x!)))})";
            r.Add((q, Invariant(rk, m.ReturnType, code, SolverAtomKind.Expression, null, inv)));
        }
        return r;
    }

    Lin Mul2(Lin a, Lin b, IOperation at)
    {
        string? code = a.Code != null && b.Code != null ? $"({a.Code} * {b.Code})" : null;
        if (b.StateFree) return Scale(a, b.C, code);
        if (a.StateFree) return Scale(b, a.C, code);
        throw new Fail($"a product of two values that depend on the state ({at.Syntax})", at);
    }

    List<(Path p, object? v)> Inline(IMethodSymbol m, List<IOperation> args, IOperation at, Path p)
    {
        m = m.OriginalDefinition;
        if (m.IsAbstract || m.IsVirtual || m.IsOverride && !m.IsSealed) throw new Fail($"a call of the virtual method {m.Name}", at);
        if (m.IsAsync || m.IsIterator || m.IsGenericMethod) throw new Fail($"a call of {m.Name} (async, an iterator or generic)", at);
        if (m.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction) throw new Fail($"a call of the local function {m.Name}", at);
        if (_stack.Contains(m, SymbolEqualityComparer.Default) || _stack.Count >= 8) throw new Fail($"a recursive or deep call of {m.Name}", at);
        if (!m.IsStatic && _self == null) throw new Fail($"a call of the instance method {m.Name} from a static region", at);
        if (!m.IsStatic && !IsSelfOrBase(m.ContainingType)) throw new Fail($"a call of {m.Name} of another type", at);
        var body = _host.BodyOf(m) ?? throw new Fail($"{m.Name} has no body in source", at);
        foreach (var d in body.DescendantsAndSelf().OfType<IVariableDeclaratorOperation>()) _temps.Add(d.Symbol);
        foreach (var prm in m.Parameters) _temps.Add(prm);
        var r = new List<(Path, object?)>();
        foreach (var (q, vs) in EvalAll(args, p))
        {
            if (q.Throws) { r.Add((q, null)); continue; }
            for (int i = 0; i < m.Parameters.Length && i < vs.Length; i++) q.Env[m.Parameters[i]] = vs[i]!;
            _stack.Add(m); _frames++;
            List<Path> outs;
            try { outs = Exec(body, [q]); }
            finally { _stack.RemoveAt(_stack.Count - 1); _frames--; }
            foreach (var o in outs)
            {
                if (o.Throws) { r.Add((o, null)); continue; }
                var v = o.Ret; o.Ended = false; o.Ret = null;
                r.Add((o, v));
            }
        }
        return r;
    }

    bool IsSelfOrBase(INamedTypeSymbol t)
    {
        for (var x = _self; x != null; x = x.BaseType) if (SymbolEqualityComparer.Default.Equals(x.OriginalDefinition, t.OriginalDefinition)) return true;
        return false;
    }

    // ---------------- pieces and flips ----------------
    // a guard's side under a piece: 0 does not depend on the state, 1 moves by a fixed step each tick, 2 does from the
    // first tick on, 3 neither
    sealed record Side(int Mode, SolverTerm? Step);
    internal sealed record FlipPlan(int Mode, bool Float, bool RightMoves, SolverTerm? Step);

    SolverTerm Entry(Lin[] map, int i, int j, NK k) => map[i].X[j] is { } t ? Retype(t, k) : Zero(k);

    /// <summary>v·A over the variables (in domain k).</summary>
    SolverTerm[] TimesA(SolverTerm[] v, Lin[] map, NK k)
    {
        var r = new SolverTerm[_vars.Count];
        for (int j = 0; j < r.Length; j++)
        {
            var s = Zero(k);
            for (int i = 0; i < v.Length; i++) if (!v[i].IsZero) s = Add(s, Mul(v[i], Entry(map, i, j, k)));
            r[j] = s;
        }
        return r;
    }

    SolverTerm Dot(SolverTerm[] v, Lin[] map, NK k)
    {
        var s = Zero(k);
        for (int i = 0; i < v.Length; i++) if (!v[i].IsZero) s = Add(s, Mul(v[i], Retype(map[i].C, k)));
        return s;
    }

    Side Classify(Lin s, Lin[] map)
    {
        var k = s.K;
        var c = s.X.Select(x => x == null ? Zero(k) : x).ToArray();
        if (c.All(x => x.IsZero)) return new Side(0, null);
        var v = TimesA(c, map, k);
        if (v.Select((x, j) => Add(x, c[j], -1)).All(x => x.IsZero)) return new Side(1, Dot(c, map, k));
        var w = TimesA(v, map, k);
        if (w.Select((x, j) => Add(x, v[j], -1)).All(x => x.IsZero)) return new Side(2, Dot(v, map, k));
        return new Side(3, null);
    }

    ClosedForm Finish(IOperation region, List<Path> paths)
    {
        var pieces = new List<SolverPiece>();
        bool exact = _vk.All(k => !IsF(k));
        foreach (var p in paths)
        {
            if (p.Throws)
            {
                if (_loop) throw new Fail("the loop body can throw (a clamp's bounds the wrong way round, Math.Abs of the smallest integer, a throw)", region);
                pieces.Add(new SolverPiece { Guards = p.G, Throws = true, Rows = [], Flips = [] });
                continue;
            }
            var map = new Lin[_vars.Count];
            for (int i = 0; i < map.Length; i++) map[i] = p.Env.TryGetValue(_vars[i].Symbol, out var v) && v is Lin l ? l : Var(i);
            var flips = new List<SolverFlip>(); var plans = new List<FlipPlan>();
            foreach (var g in p.G)
            {
                if (g.Invariant != null) { flips.Add(new SolverFlip(g, SolverFlipKind.Never, null, "does not depend on the state")); plans.Add(new FlipPlan(0, false, false, null)); continue; }
                bool f = IsF(g.L!.K);
                if (f) exact = false;
                string where = p.G.Count == 1 ? "" : " where " + string.Join(" && ", p.G.Where(x => x != g).Select(x => x.Operation.Syntax.ToString() + (x.WhenTrue ? "" : " is false")));
                if (f)
                {
                    var d = Plus(g.L!, g.R!, -1, null);
                    var side = Classify(d, map);
                    if (side.Mode == 3) throw new Fail($"the guard {g.Operation.Syntax} is not monotone in the number of ticks{where}: its value does not change by a fixed amount each tick", g.Operation);
                    if (side.Mode != 0 && side.Step!.IsZero == false && g.Comparison == BinaryOperatorKind.Equals)
                        throw new Fail($"the guard {g.Operation.Syntax} compares floating-point values for equality while they change", g.Operation);
                    flips.Add(Flip(g, side, "(" + Affine(g.L!) + ") - (" + Affine(g.R!) + ")"));
                    plans.Add(new FlipPlan(side.Mode, true, false, side.Step));
                }
                else
                {
                    var ls = Classify(g.L!, map); var rs = Classify(g.R!, map);
                    if (ls.Mode == 3 || rs.Mode == 3) throw new Fail($"the guard {g.Operation.Syntax} is not monotone in the number of ticks{where}: its value does not change by a fixed amount each tick", g.Operation);
                    bool lFixed = ls.Mode == 0 || ls.Mode == 1 && ls.Step!.IsZero, rFixed = rs.Mode == 0 || rs.Mode == 1 && rs.Step!.IsZero;
                    if (lFixed && rFixed) { flips.Add(new SolverFlip(g, SolverFlipKind.Never, null, "does not change under this piece")); plans.Add(new FlipPlan(0, false, false, null)); continue; }
                    if (!lFixed && !rFixed) throw new Fail($"both sides of the integer comparison {g.Operation.Syntax} change each tick{where}", g.Operation);
                    var moving = lFixed ? rs : ls;
                    flips.Add(Flip(g, moving, Affine(lFixed ? g.R! : g.L!).ToString()));
                    plans.Add(new FlipPlan(moving.Mode, false, lFixed, moving.Step));
                }
            }
            pieces.Add(new SolverPiece
            {
                Guards = p.G, Throws = false, Rows = map.Select(Affine).ToList(), Flips = flips, Map = map,
            });
            _plans[pieces[^1]] = plans;
        }
        if (_approx) exact = false;
        foreach (var g in pieces.SelectMany(x => x.Guards))
            if (g.L != null) { g.Left = Affine(g.L); g.Right = Affine(g.R!); }
        var parameters = Parameters();
        var form = new ClosedForm
        {
            Region = region, Method = _method, IsLoop = _loop, Solved = true, Exact = exact, Variables = [.. _vars], Pieces = pieces,
            Invariants = [.. _atoms], Parameters = parameters,
        };
        form.Generate = cls => Generate(cls, pieces, parameters);
        return form;
    }

    readonly Dictionary<SolverPiece, List<FlipPlan>> _plans = [];

    SolverFlip Flip(SolverGuard g, Side s, string value) => s.Mode switch
    {
        0 => new SolverFlip(g, SolverFlipKind.Never, null, "does not depend on the state"),
        1 => new SolverFlip(g, SolverFlipKind.Steps, s.Step, $"v(n) = v(0) + n*({s.Step}), v = {value}: flips at the first n where {g.Operation.Syntax} changes (n = ceil((limit - v(0)) / step))"),
        _ => new SolverFlip(g, SolverFlipKind.StepsAfterFirstTick, s.Step, $"v(n) = v(1) + (n - 1)*({s.Step}) from the first tick, v = {value}"),
    };

    SolverAffine Affine(Lin l) => new(_vars, l.X.Select(x => x ?? Zero(l.K)).ToList(), l.C);

    List<string> Parameters()
    {
        var r = new List<string>();
        string Self() => (_self!.IsValueType ? "ref " : "") + _self.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + " __self";
        static string T(ITypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!_loop)
        {
            if (!_method.IsStatic) r.Add(Self());
            foreach (var p in _method.Parameters) r.Add(T(p.Type) + " @" + p.Name);
            return r;
        }
        if (_usedSelf || _vars.Any(v => v.Symbol is IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false })) r.Add(Self());
        foreach (var s in _outer.Where(s => _index.ContainsKey(s))) r.Add("ref " + T(TypeOf(s)) + " @" + s.Name);
        foreach (var s in _outer.Where(s => !_index.ContainsKey(s))) r.Add(T(TypeOf(s)) + " @" + s.Name);
        return r;
    }

    static ITypeSymbol TypeOf(ISymbol s) => s switch { ILocalSymbol l => l.Type, IParameterSymbol p => p.Type, IFieldSymbol f => f.Type, IPropertySymbol p => p.Type, _ => throw new InvalidOperationException() };

    // ---------------- the generated Advance ----------------
    static string RenderTerm(SolverTerm t, bool asDouble, Func<SolverAtom, string> atom)
    {
        if (t.M.Count == 0) return asDouble ? "0d" : "0L";
        var parts = new List<string>();
        foreach (var (_, (i, f, fs)) in t.M.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string c = asDouble ? Literal(NK.F64, 0, t.IsFloat ? f : i) : Literal(NK.I64, i, 0);
            if (fs.Length == 0) { parts.Add(c); continue; }
            var factors = fs.Select(a => (asDouble ? "(double)" : "(long)") + atom(a));
            parts.Add("(" + (c is "1L" or "(1d)" ? "" : c + " * ") + string.Join(" * ", factors) + ")");
        }
        return "(" + string.Join(" + ", parts) + ")";
    }

    static string A(SolverAtom a) => "__a" + a.Id.ToString(Inv);

    /// <summary>A value in its computing domain (long for an integer, double for floating point), from state named s.</summary>
    string Render(Lin l, string[] s, bool asDouble)
    {
        var parts = new List<string> { RenderTerm(l.C, asDouble, A) };
        for (int j = 0; j < l.X.Length; j++)
        {
            if (l.X[j] is not { IsZero: false } x) continue;
            var term = RenderTerm(x, asDouble, A);
            parts.Add(term + " * " + (asDouble ? "(double)" : "") + s[j]);
        }
        return "(" + string.Join(" + ", parts) + ")";
    }

    string Typed(Lin l, string[] s) => l.K switch
    {
        NK.I32 => $"((int){Render(l, s, false)})",
        NK.I64 => Render(l, s, false),
        NK.F32 => $"((float){Render(l, s, true)})",
        _ => Render(l, s, true),
    };

    string GuardCode(SolverGuard g, string[] s)
    {
        string c = g.Invariant != null ? A(g.Invariant) : $"({Typed(g.L!, s)} {SolverGuard.Op(g.Comparison!.Value)} {Typed(g.R!, s)})";
        return g.WhenTrue ? c : "!" + c;
    }

    static int OpCode(BinaryOperatorKind k, bool mirror) => (k, mirror) switch
    {
        (BinaryOperatorKind.LessThan, false) => 0, (BinaryOperatorKind.LessThanOrEqual, false) => 1,
        (BinaryOperatorKind.LessThan, true) => 2, (BinaryOperatorKind.LessThanOrEqual, true) => 3,
        (BinaryOperatorKind.Equals, _) => 4, _ => 5,
    };

    string Access(SolverVariable v) => v.Symbol switch
    {
        IFieldSymbol or IPropertySymbol when v.Symbol.IsStatic => v.Symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ".@" + v.Symbol.Name,
        IFieldSymbol or IPropertySymbol => "__self.@" + v.Symbol.Name,
        _ => "@" + v.Symbol.Name,
    };

    /// <summary>The state after one tick of a piece, into new locals named y.</summary>
    void OneTick(StringBuilder sb, string ind, Lin[] map, string[] s, string[] y)
    {
        for (int i = 0; i < map.Length; i++)
        {
            var k = _vk[i];
            string e = Render(map[i], s, IsF(k));
            sb.Append(ind).Append(IsF(k) ? "double " : "long ").Append(y[i]).Append(" = ").Append(k switch
            {
                NK.I32 => $"(long)(int){e}", NK.F32 => $"(double)(float){e}", _ => e,
            }).Append(";\n");
        }
    }

    string Generate(string cls, List<SolverPiece> pieces, List<string> parameters)
    {
        var sb = new StringBuilder();
        int d = _vars.Count;
        var s = Enumerable.Range(0, d).Select(i => "__s" + i).ToArray();
        sb.Append("// <auto-generated/> closed form of ").Append(_method.ToDisplayString()).Append(_loop ? " (a loop)" : "").Append(", cs2wasm's solver (plugin interface version 6)\n");
        sb.Append("static class ").Append(cls).Append("\n{\n");
        sb.Append("    public static void Advance(").Append(string.Join(", ", parameters.Append("long __n"))).Append(")\n    {\n");
        sb.Append("        if (__n <= 0) return;\n        unchecked\n        {\n");
        string ind = "            ";
        foreach (var a in _atoms)
            sb.Append(ind).Append(a.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(' ').Append(A(a)).Append(" = ").Append(a.Code).Append(";\n");
        void Load(string i2) { for (int i = 0; i < d; i++) sb.Append(i2).Append(s[i]).Append(" = ").Append(Access(_vars[i])).Append(";\n"); }
        void Store(string i2) { for (int i = 0; i < d; i++) sb.Append(i2).Append(Access(_vars[i])).Append(" = (").Append(TypeName(_vk[i])).Append(')').Append(s[i]).Append(";\n"); }
        for (int i = 0; i < d; i++) sb.Append(ind).Append(IsF(_vk[i]) ? "double " : "long ").Append(s[i]).Append(" = ").Append(Access(_vars[i])).Append(";\n");
        sb.Append(ind).Append("while (__n > 0)\n").Append(ind).Append("{\n");
        string i1 = ind + "    ";
        sb.Append(i1).Append("long __k = __n;\n");
        for (int pi = 0; pi < pieces.Count; pi++)
        {
            var piece = pieces[pi];
            var cond = piece.Guards.Count == 0 ? "true" : string.Join(" && ", piece.Guards.Select(g => GuardCode(g, s)));
            sb.Append(i1).Append(pi == 0 ? "if (" : "else if (").Append(cond).Append(")\n").Append(i1).Append("{\n");
            string i2 = i1 + "    ";
            if (piece.Throws)
            {
                // the program's tick throws here: run it, so it throws as the program does
                Store(i2);
                string call = (_method.IsStatic ? _method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : "__self") + ".@" + _method.Name
                    + "(" + string.Join(", ", _method.Parameters.Select(p => "@" + p.Name)) + ");";
                sb.Append(i2).Append(call).Append('\n');
                Load(i2);
                sb.Append(i2).Append("__k = 1;\n");
            }
            else
            {
                EmitFlips(sb, i2, piece, s);
                EmitApply(sb, i2, piece.Map!, s);
            }
            sb.Append(i1).Append("}\n");
        }
        sb.Append(i1).Append("else throw new global::System.InvalidOperationException(\"closed form: no piece applies\");\n");
        sb.Append(i1).Append("__n -= __k;\n");
        sb.Append(ind).Append("}\n");
        Store(ind);
        sb.Append("        }\n    }\n");
        sb.Append(Helpers);
        sb.Append("}\n");
        return sb.ToString();
    }

    void EmitFlips(StringBuilder sb, string ind, SolverPiece piece, string[] s)
    {
        var plans = _plans[piece];
        for (int gi = 0; gi < piece.Guards.Count; gi++)
        {
            var g = piece.Guards[gi]; var plan = plans[gi];
            if (plan.Mode == 0) continue;
            string Call(string[] st, string extra)
            {
                if (plan.Float)
                {
                    var dlin = Plus(g.L!, g.R!, -1, null);
                    return $"__FlipFloat({Render(dlin, st, true)}, {RenderTerm(plan.Step!, true, A)}, {OpCode(g.Comparison!.Value, false)}, {extra})";
                }
                var (moving, fixd) = plan.RightMoves ? (g.R!, g.L!) : (g.L!, g.R!);
                return $"__FlipInt({Typed(moving, st)}, {RenderTerm(plan.Step!, false, A)}, {Typed(fixd, st)}, {OpCode(g.Comparison!.Value, plan.RightMoves)}, {(moving.K == NK.I32 ? "true" : "false")}, {extra})";
            }
            if (plan.Mode == 1) { sb.Append(ind).Append("__k = global::System.Math.Min(__k, ").Append(Call(s, "__k")).Append(");\n"); continue; }
            // from the first tick on
            var y = Enumerable.Range(0, s.Length).Select(i => $"__y{gi}_{i}").ToArray();
            sb.Append(ind).Append("if (__k > 1)\n").Append(ind).Append("{\n");
            OneTick(sb, ind + "    ", piece.Map!, s, y);
            string g0 = GuardCode(g, s), g1 = GuardCode(g, y);
            sb.Append(ind).Append("    if (").Append(g0).Append(" != ").Append(g1).Append(") __k = 1;\n");
            sb.Append(ind).Append("    else { long __f = ").Append(Call(y, "__k - 1")).Append("; if (__f != long.MaxValue) __k = global::System.Math.Min(__k, 1 + __f); }\n");
            sb.Append(ind).Append("}\n");
        }
    }

    void EmitApply(StringBuilder sb, string ind, Lin[] map, string[] s)
    {
        int d = map.Length;
        bool Id(int i, int j) { var t = map[i].X[j]; return i == j ? t != null && Add(t, One(_vk[i]), -1).IsZero : t == null || t.IsZero; }
        bool identityA = Enumerable.Range(0, d).All(i => Enumerable.Range(0, d).All(j => Id(i, j)));
        bool zeroA = map.All(l => l.StateFree);
        var b = map.Select(l => l.C).ToArray();
        if (identityA)
        {
            for (int i = 0; i < d; i++)
            {
                if (b[i].IsZero) continue;
                bool f = IsF(_vk[i]);
                sb.Append(ind).Append(s[i]).Append(" = ").Append(s[i]).Append(" + ").Append(f ? "(double)__k" : "__k").Append(" * ").Append(RenderTerm(b[i], f, A)).Append(";\n");
                Normalize(sb, ind, i, s[i]);
            }
            return;
        }
        if (zeroA)
        {
            for (int i = 0; i < d; i++)
            {
                sb.Append(ind).Append(s[i]).Append(" = ").Append(RenderTerm(b[i], IsF(_vk[i]), A)).Append(";\n");
                Normalize(sb, ind, i, s[i]);
            }
            return;
        }
        var ints = Enumerable.Range(0, d).Where(i => !IsF(_vk[i])).ToList();
        var floats = Enumerable.Range(0, d).Where(i => IsF(_vk[i])).ToList();
        sb.Append(ind).Append("{\n");
        string i1 = ind + "    ";
        var y = new string[d];
        if (ints.Count > 0)
        {
            Pow(sb, i1, "__i", ints, (i, j) => RenderTerm(map[i].X[j] ?? Zero(_vk[i]), false, A), i => RenderTerm(b[i], false, A), "long");
            foreach (var i in ints)
            {
                y[i] = "__yi" + i;
                sb.Append(i1).Append("long ").Append(y[i]).Append(" = ").Append(string.Join(" + ", ints.Select((j, jj) => $"__ir{ints.IndexOf(i)}_{jj} * {s[j]}").Append($"__irb{ints.IndexOf(i)}"))).Append(";\n");
            }
        }
        if (floats.Count > 0)
        {
            var all = Enumerable.Range(0, d).ToList();
            Pow(sb, i1, "__f", all, (i, j) => RenderTerm(map[i].X[j] ?? Zero(_vk[i]), true, A), i => RenderTerm(b[i], true, A), "double");
            foreach (var i in floats)
            {
                y[i] = "__yf" + i;
                sb.Append(i1).Append("double ").Append(y[i]).Append(" = ").Append(string.Join(" + ", all.Select(j => $"__fr{i}_{j} * (double){s[j]}").Append($"__frb{i}"))).Append(";\n");
            }
        }
        for (int i = 0; i < d; i++) { sb.Append(i1).Append(s[i]).Append(" = ").Append(y[i]).Append(";\n"); Normalize(sb, i1, i, s[i]); }
        sb.Append(ind).Append("}\n");
    }

    void Normalize(StringBuilder sb, string ind, int i, string name)
    {
        if (_vk[i] == NK.I32) sb.Append(ind).Append(name).Append(" = (int)").Append(name).Append(";\n");
        else if (_vk[i] == NK.F32) sb.Append(ind).Append(name).Append(" = (float)").Append(name).Append(";\n");
    }

    /// <summary>The k-th power of the affine map (P, pb) over the variables idx by repeated squaring: R, rb with
    /// x(k) = R x + rb. In long arithmetic (wrapping) it is exact; no division.</summary>
    static void Pow(StringBuilder sb, string ind, string pre, List<int> idx, Func<int, int, string> entry, Func<int, string> bterm, string T)
    {
        int n = idx.Count;
        string P(int a, int c) => $"{pre}p{a}_{c}";
        string PB(int a) => $"{pre}pb{a}";
        string R(int a, int c) => $"{pre}r{a}_{c}";
        string RB(int a) => $"{pre}rb{a}";
        string one = T == "long" ? "1L" : "1d", zero = T == "long" ? "0L" : "0d";
        for (int a = 0; a < n; a++)
        {
            for (int c = 0; c < n; c++) sb.Append(ind).Append(T).Append(' ').Append(P(a, c)).Append(" = ").Append(entry(idx[a], idx[c])).Append(";\n");
            sb.Append(ind).Append(T).Append(' ').Append(PB(a)).Append(" = ").Append(bterm(idx[a])).Append(";\n");
        }
        for (int a = 0; a < n; a++)
        {
            for (int c = 0; c < n; c++) sb.Append(ind).Append(T).Append(' ').Append(R(a, c)).Append(" = ").Append(a == c ? one : zero).Append(";\n");
            sb.Append(ind).Append(T).Append(' ').Append(RB(a)).Append(" = ").Append(zero).Append(";\n");
        }
        sb.Append(ind).Append("for (long ").Append(pre).Append("e = __k; ; )\n").Append(ind).Append("{\n");
        string i1 = ind + "    ", i2 = i1 + "    ";
        void Compose(Func<int, int, string> left, Func<int, string> leftB, Func<int, int, string> right, Func<int, string> rightB, Func<int, int, string> dst, Func<int, string> dstB)
        {
            // dst = left ∘ right: dst = left·right, dstB = left·rightB + leftB
            sb.Append(i1).Append("{\n");
            for (int a = 0; a < n; a++)
            {
                for (int c = 0; c < n; c++)
                    sb.Append(i2).Append(T).Append($" {pre}t{a}_{c} = ").Append(string.Join(" + ", Enumerable.Range(0, n).Select(l => $"{left(a, l)} * {right(l, c)}"))).Append(";\n");
                sb.Append(i2).Append(T).Append($" {pre}tb{a} = ").Append(string.Join(" + ", Enumerable.Range(0, n).Select(l => $"{left(a, l)} * {rightB(l)}").Append(leftB(a)))).Append(";\n");
            }
            for (int a = 0; a < n; a++)
            {
                for (int c = 0; c < n; c++) sb.Append(i2).Append(dst(a, c)).Append($" = {pre}t{a}_{c};\n");
                sb.Append(i2).Append(dstB(a)).Append($" = {pre}tb{a};\n");
            }
            sb.Append(i1).Append("}\n");
        }
        sb.Append(i1).Append($"if (({pre}e & 1) != 0)\n");
        Compose(P, PB, R, RB, R, RB);
        sb.Append(i1).Append($"{pre}e >>= 1;\n").Append(i1).Append($"if ({pre}e == 0) break;\n");
        Compose(P, PB, P, PB, P, PB);
        sb.Append(ind).Append("}\n");
    }

    const string Helpers = """
            static bool __Cmp(long a, long b, int op) => op switch { 0 => a < b, 1 => a <= b, 2 => a > b, 3 => a >= b, 4 => a == b, _ => a != b };
            static bool __CmpD(double a, int op) => op switch { 0 => a < 0, 1 => a <= 0, 2 => a > 0, 3 => a >= 0, 4 => a == 0, _ => a != 0 };

            // the first t >= 1 at which (l + t*s op r) is no longer `now`, the sum not wrapping; 0: never
            static ulong __First(long l, long s, ulong step, long r, int op, bool now)
            {
                unchecked
                {
                    bool up = s > 0;
                    if (op >= 4)
                    {
                        bool equal = op == 4 ? now : !now;
                        if (equal) return 1;
                        if (up ? l > r : l < r) return 0;
                        ulong e = up ? (ulong)r - (ulong)l : (ulong)l - (ulong)r;
                        return e % step == 0 ? e / step : 0;
                    }
                    bool strict = op == 0 || op == 3;
                    bool below = op <= 1 ? now : !now;
                    if (up)
                    {
                        if (!below) return 0;
                        ulong e = (ulong)r - (ulong)l;
                        if (strict) return e / step + (e % step != 0 ? 1UL : 0UL);
                        ulong q = e / step; return q == ulong.MaxValue ? q : q + 1;
                    }
                    else
                    {
                        if (below) return 0;
                        ulong e = (ulong)l - (ulong)r;
                        if (strict) { ulong q = e / step; return q == ulong.MaxValue ? q : q + 1; }
                        return e / step + (e % step != 0 ? 1UL : 0UL);
                    }
                }
            }

            // ticks (at most limit; long.MaxValue: not within limit) before (l op r) changes, l moving by s each tick
            // in int (is32) or long arithmetic, wrapping as the program wraps
            static long __FlipInt(long l, long s, long r, int op, bool is32, long limit)
            {
                unchecked
                {
                    if (is32) { l = (int)l; s = (int)s; r = (int)r; }
                    if (s == 0) return long.MaxValue;
                    long min = is32 ? int.MinValue : long.MinValue, max = is32 ? int.MaxValue : long.MaxValue;
                    bool now = __Cmp(l, r, op);
                    ulong step = s > 0 ? (ulong)s : 0UL - (ulong)s;
                    long done = 0;
                    while (done < limit)
                    {
                        ulong room = s > 0 ? (ulong)max - (ulong)l : (ulong)l - (ulong)min;
                        ulong q = room / step;
                        ulong t = __First(l, s, step, r, op, now);
                        if (t != 0 && t <= q) return t > (ulong)(limit - done) ? long.MaxValue : done + (long)t;
                        if (q == ulong.MaxValue) return long.MaxValue;
                        ulong w = q + 1;
                        if (w > (ulong)(limit - done)) return long.MaxValue;
                        long lw = l + (long)(w * (ulong)s);
                        if (is32) lw = (int)lw;
                        if (__Cmp(lw, r, op) != now) return done + (long)w;
                        l = lw; done += (long)w;
                    }
                    return long.MaxValue;
                }
            }

            // the same for a floating-point difference d moving by s (approximate: the sum in double)
            static long __FlipFloat(double d, double s, int op, long limit)
            {
                if (!(s != 0) || double.IsNaN(d)) return long.MaxValue;
                bool now = __CmpD(d, op);
                double t0 = -d / s;
                long t = !(t0 > 1) ? 1 : t0 >= limit ? limit : (long)global::System.Math.Ceiling(t0);
                for (int i = 0; i < 4 && t > 1 && __CmpD(d + (t - 1) * s, op) != now; i++) t--;
                for (int i = 0; i < 4 && __CmpD(d + t * s, op) == now; i++) { if (t >= limit) return long.MaxValue; t++; }
                return __CmpD(d + t * s, op) == now ? long.MaxValue : t;
            }

        """;
}
