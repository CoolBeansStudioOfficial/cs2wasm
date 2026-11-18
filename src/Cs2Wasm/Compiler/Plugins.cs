using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

sealed record MemberHook(IMethodSymbol Hook, int Tag, InstrumentPoint Point);

/// <summary>A hook the value of one expression goes through (PluginContext.InterceptValue).</summary>
sealed class ValueHook
{
    public required IMethodSymbol Hook;
    public required int Tag;
    public required Location Location;
    public required string Text;
    /// <summary>Some compiled code calls it (else the build reports it: the expression is not computed as a value).</summary>
    public bool Applied;
}

/// <summary>A clone of methods (section 7.1) in which calls to some imports are suppressed.</summary>
sealed class CloneSpec
{
    public required int Id;
    public required HashSet<string> Suppressed; // method keys of imports
}

internal sealed partial class Driver
{
    public readonly Dictionary<ISymbol, List<MemberHook>> WriteHooks = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<ISymbol, List<MemberHook>> ReadHooks = new(SymbolEqualityComparer.Default);
    public readonly Dictionary<(string path, int start, int length), List<(IMethodSymbol hook, int tag)>> StatementHooks = [];
    public readonly Dictionary<((string path, int start, int length) site, OperationKind kind), List<ValueHook>> ValueHooks = [];
    public readonly Dictionary<(string path, int start, int length), List<MemberHook>> SiteWriteHooks = [];
    public bool HasInstrumentation => WriteHooks.Count > 0 || ReadHooks.Count > 0 || StatementHooks.Count > 0 || ValueHooks.Count > 0 || SiteWriteHooks.Count > 0;
    readonly HashSet<ISymbol> _instrumented = new(SymbolEqualityComparer.Default);
    readonly List<CloneSpec> _clones = [];

    public static (string, int, int) SiteKey(SyntaxNode s) => (s.SyntaxTree.FilePath, s.SpanStart, s.Span.Length);

    /// <summary>Runs the embedder's plugin passes after the program is lowered (section 7.1).</summary>
    void RunPlugins(RuntimeGen rt)
    {
        if (Options.Plugins.Count == 0) return;
        var host = new PluginHost(this, rt);
        foreach (var p in Options.Plugins)
        {
            if (p.InterfaceVersion is < 1 or > PluginContext.InterfaceVersion)
            {
                Report(Diags.Plugin, null, $"plugin '{p.Name}' targets interface version {p.InterfaceVersion}; this compiler provides {PluginContext.InterfaceVersion}");
                continue;
            }
            var ctx = new PluginContext(host);
            try { p.Run(ctx); }
            catch (Exception ex) { Report(Diags.Plugin, null, $"plugin '{p.Name}' failed: {ex.Message}"); continue; }
            foreach (var (name, data) in ctx.Sections) Module.CustomSections.Add(new CustomSection(name, data));
            Diagnostics.AddRange(ctx.Diagnostics);
        }
        Drain(rt); // generated sources and clones
        if (HasInstrumentation) Recompile(rt);
        foreach (var list in ValueHooks.Values)
            foreach (var h in list)
                if (!h.Applied) Report(Diags.Plugin, h.Location, $"the value hook {h.Hook.Name} at '{h.Text}' was not applied: no reachable code computes this expression as a value");
    }

    internal void MarkInstrumented(ISymbol member) => _instrumented.Add(RootMember(member));

    static ISymbol RootMember(ISymbol s)
    {
        while (s is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } m && m.ContainingSymbol != null) s = m.ContainingSymbol;
        if (s is IMethodSymbol { AssociatedSymbol: { } a }) s = a;
        return s.OriginalDefinition;
    }

    /// <summary>Methods with instrumented sites are compiled again with their hooks (generic instances, lambdas and
    /// state machine bodies included; a state machine's kickoff is left alone).</summary>
    void Recompile(RuntimeGen rt)
    {
        foreach (var mi in AllMethods.ToList())
        {
            if (!mi.Compiled || mi.Impl is not (MethodImpl.Body)) continue;
            if (!_instrumented.Contains(RootMember(mi.Sym))) continue;
            bool kickoff = (mi.Sym.IsAsync || mi.Sym.IsIterator || mi.Sym.OriginalDefinition.IsAsync || mi.Sym.OriginalDefinition.IsIterator) && !mi.Key.EndsWith(" MoveNext");
            if (kickoff) continue;
            mi.Fn.Body.Clear(); mi.Fn.Locals.Clear(); mi.Callees.Clear();
            mi.Compiled = false;
            _work.Enqueue(mi);
        }
        Drain(rt);
    }

    public MethodInst CloneMethod(IMethodSymbol m, CloneSpec spec)
    {
        m = CanonicalMethod(m);
        var key = Types.MethodKey(m) + " #clone" + spec.Id;
        if (_methods.TryGetValue(key, out var mi)) return mi;
        var orig = Method(m);
        if (orig.Impl is not (MethodImpl.Body or MethodImpl.Synthesized)) return orig;
        var fn = Module.AddFunction(Display(m) + " (clone " + spec.Id + ")", Signature(m, Types));
        fn.FrameName = orig.Fn.FrameName;
        mi = new MethodInst { Sym = m, Fn = fn, Key = key, Impl = orig.Impl, Clone = spec };
        _methods[key] = mi;
        _work.Enqueue(mi);
        return mi;
    }

    internal CloneSpec NewClone(IEnumerable<string> suppressed) { var c = new CloneSpec { Id = _clones.Count + 1, Suppressed = [.. suppressed] }; _clones.Add(c); return c; }

    internal Compilation AddInput(Compilation c, RuntimeGen rt)
    {
        _inputs.Add(c);
        FindRootsIn(c, rt);
        return c;
    }
}

/// <summary>What a plugin sees and changes; analyses are computed on first use.</summary>
internal sealed partial class PluginHost
{
    readonly Driver D;
    readonly RuntimeGen _rt;
    public PluginHost(Driver d, RuntimeGen rt) { D = d; _rt = rt; }

    public Compilation Main => D.Main;

    List<IMethodSymbol>? _reachable;
    public IReadOnlyList<IMethodSymbol> Reachable => _reachable ??= D.AllMethods
        .Where(m => m.Impl is MethodImpl.Body or MethodImpl.Synthesized && m.Sym.MethodKind is not MethodKind.AnonymousFunction && m.Clone == null)
        .Select(m => m.Sym).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToList();

    Dictionary<IMethodSymbol, IReadOnlyList<IMethodSymbol>>? _graph;
    public IReadOnlyDictionary<IMethodSymbol, IReadOnlyList<IMethodSymbol>> CallGraph
    {
        get
        {
            if (_graph != null) return _graph;
            _graph = new Dictionary<IMethodSymbol, IReadOnlyList<IMethodSymbol>>(SymbolEqualityComparer.Default);
            foreach (var m in D.AllMethods)
            {
                if (m.Impl is not (MethodImpl.Body or MethodImpl.Synthesized) || m.Clone != null) continue;
                var callees = m.Callees.Select(c => c.Sym).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToList();
                if (_graph.TryGetValue(m.Sym, out var existing)) callees = existing.Concat(callees).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToList();
                _graph[m.Sym] = callees;
            }
            return _graph;
        }
    }

    // ---------------- bodies ----------------
    /// <summary>Source method definitions among the reachable ones, each once.</summary>
    IEnumerable<IMethodSymbol> SourceDefinitions() => Reachable.Select(m => m.OriginalDefinition).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
        .Where(m => m.DeclaringSyntaxReferences.Any(r => D.IsInput(r.SyntaxTree)));

    readonly Dictionary<IMethodSymbol, IOperation?> _bodies = new(SymbolEqualityComparer.Default);
    IOperation? Body(IMethodSymbol m)
    {
        m = m.OriginalDefinition;
        if (_bodies.TryGetValue(m, out var b)) return b;
        b = null;
        foreach (var r in m.DeclaringSyntaxReferences)
        {
            if (!D.IsInput(r.SyntaxTree)) continue;
            var node = r.GetSyntax();
            SyntaxNode? target = node switch
            {
                BaseMethodDeclarationSyntax bm when bm.Body != null || bm.ExpressionBody != null => bm,
                AccessorDeclarationSyntax a when a.Body != null || a.ExpressionBody != null => a,
                ArrowExpressionClauseSyntax ar => ar,
                _ => null,
            };
            if (target == null) continue;
            b = D.ModelFor(target.SyntaxTree).GetOperation(target);
            if (b != null) break;
        }
        _bodies[m] = b;
        return b;
    }

    readonly Dictionary<IMethodSymbol, ControlFlowGraph?> _graphs = new(SymbolEqualityComparer.Default);
    public ControlFlowGraph? Graph(IMethodSymbol m)
    {
        m = m.OriginalDefinition;
        if (_graphs.TryGetValue(m, out var g)) return g;
        g = Body(m) switch
        {
            IMethodBodyOperation mb => ControlFlowGraph.Create(mb),
            IConstructorBodyOperation cb => ControlFlowGraph.Create(cb),
            IBlockOperation { Parent: null } bl => ControlFlowGraph.Create(bl),
            _ => null,
        };
        _graphs[m] = g;
        return g;
    }

    // ---------------- member accesses ----------------
    List<MemberAccess>? _accesses;
    public IReadOnlyList<MemberAccess> Accesses
    {
        get
        {
            if (_accesses != null) return _accesses;
            _accesses = [];
            foreach (var m in SourceDefinitions())
            {
                var body = Body(m);
                if (body == null) continue;
                foreach (var op in body.DescendantsAndSelf())
                {
                    ISymbol? member = op switch
                    {
                        IFieldReferenceOperation f => f.Field,
                        IPropertyReferenceOperation p => p.Property,
                        IEventReferenceOperation e => e.Event,
                        _ => null,
                    };
                    if (member == null) continue;
                    var kind = KindOf(op);
                    _accesses.Add(new MemberAccess(member.OriginalDefinition, m, op, kind, ConditionsOf(op, body)) { Instrumentable = kind == AccessKind.Read ? op is not IEventReferenceOperation : HookedWrite(op) });
                }
            }
            return _accesses;
        }
    }

    public IEnumerable<MemberAccess> AccessesOf(ISymbol member)
    {
        var def = member.OriginalDefinition;
        return Accesses.Where(a => SymbolEqualityComparer.Default.Equals(a.Member, def));
    }

    /// <summary>A write the write hooks run at (FunctionCompiler.WriteTarget): `=`, a compound assignment, `++`/`--`, `??=`.</summary>
    static bool HookedWrite(IOperation op)
    {
        if (op is IEventReferenceOperation) return false;
        var p = op.Parent;
        while (p is IConversionOperation { IsImplicit: true }) { op = p; p = p.Parent; }
        return p switch
        {
            ISimpleAssignmentOperation sa => ReferenceEquals(sa.Target, op),
            ICompoundAssignmentOperation ca => ReferenceEquals(ca.Target, op),
            ICoalesceAssignmentOperation cc => ReferenceEquals(cc.Target, op),
            IIncrementOrDecrementOperation inc => ReferenceEquals(inc.Target, op),
            _ => false,
        };
    }

    static AccessKind KindOf(IOperation op)
    {
        var p = op.Parent;
        while (p is IConversionOperation { IsImplicit: true }) { op = p; p = p.Parent; }
        return p switch
        {
            ISimpleAssignmentOperation sa when ReferenceEquals(sa.Target, op) => AccessKind.Write,
            ICompoundAssignmentOperation ca when ReferenceEquals(ca.Target, op) => AccessKind.ReadWrite,
            ICoalesceAssignmentOperation cc when ReferenceEquals(cc.Target, op) => AccessKind.ReadWrite,
            IIncrementOrDecrementOperation inc when ReferenceEquals(inc.Target, op) => AccessKind.ReadWrite,
            IEventAssignmentOperation => AccessKind.Write,
            IArgumentOperation { Parameter.RefKind: RefKind.Out } => AccessKind.Write,
            IArgumentOperation { Parameter.RefKind: RefKind.Ref } => AccessKind.ReadWrite,
            ITupleOperation t when t.Parent is IDeconstructionAssignmentOperation da && ReferenceEquals(da.Target, t) => AccessKind.Write,
            _ => AccessKind.Read,
        };
    }

    /// <summary>The branches an operation runs under, outermost first.</summary>
    static IReadOnlyList<BranchCondition> ConditionsOf(IOperation op, IOperation root)
    {
        var list = new List<BranchCondition>();
        IOperation child = op;
        for (var p = op.Parent; p != null && !ReferenceEquals(child, root); child = p, p = p.Parent)
        {
            switch (p)
            {
                case IConditionalOperation c when ReferenceEquals(c.WhenTrue, child): list.Add(new(c.Condition, true)); break;
                case IConditionalOperation c when ReferenceEquals(c.WhenFalse, child): list.Add(new(c.Condition, false)); break;
                case IWhileLoopOperation w when ReferenceEquals(w.Body, child) && w.Condition != null: list.Add(new(w.Condition, !w.ConditionIsUntil)); break;
                case IForLoopOperation f when ReferenceEquals(f.Body, child) && f.Condition != null: list.Add(new(f.Condition, true)); break;
                case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd } b when ReferenceEquals(b.RightOperand, child): list.Add(new(b.LeftOperand, true)); break;
                case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr } b when ReferenceEquals(b.RightOperand, child): list.Add(new(b.LeftOperand, false)); break;
                case ISwitchCaseOperation sc: foreach (var cl in sc.Clauses) list.Add(new(cl, true)); break;
                case ISwitchExpressionArmOperation arm when !ReferenceEquals(arm.Pattern, child): list.Add(new(arm.Pattern, true)); if (arm.Guard != null && !ReferenceEquals(arm.Guard, child)) list.Add(new(arm.Guard, true)); break;
                case IConditionalAccessOperation ca when ReferenceEquals(ca.WhenNotNull, child): list.Add(new(ca.Operation, true)); break;
                case ICatchClauseOperation cc when cc.Filter != null && !ReferenceEquals(cc.Filter, child): list.Add(new(cc.Filter, true)); break;
            }
        }
        list.Reverse();
        return list;
    }

    // ---------------- def-use ----------------
    readonly Dictionary<IMethodSymbol, DefUse?> _defUse = new(SymbolEqualityComparer.Default);
    public DefUse? DefUse(IMethodSymbol m)
    {
        m = m.OriginalDefinition;
        if (_defUse.TryGetValue(m, out var du)) return du;
        var g = Graph(m);
        du = g == null ? null : ComputeDefUse(g);
        _defUse[m] = du;
        return du;
    }

    static ISymbol? VariableOf(IOperation? o) => o switch
    {
        ILocalReferenceOperation l => l.Local,
        IParameterReferenceOperation p => p.Parameter.OriginalDefinition,
        IConversionOperation { IsImplicit: true } c => VariableOf(c.Operand),
        _ => null,
    };

    /// <summary>The variables an operation (not its children) writes.</summary>
    static IEnumerable<ISymbol> DefsOf(IOperation o)
    {
        switch (o)
        {
            case IAssignmentOperation a when a is not IDeconstructionAssignmentOperation && VariableOf(a.Target) is { } v: yield return v; break;
            case IDeconstructionAssignmentOperation da:
                foreach (var t in da.Target.DescendantsAndSelf()) if (VariableOf(t) is { } dv && t is ILocalReferenceOperation or IParameterReferenceOperation) yield return dv;
                break;
            case IIncrementOrDecrementOperation inc when VariableOf(inc.Target) is { } iv: yield return iv; break;
            case IArgumentOperation { Parameter.RefKind: RefKind.Out or RefKind.Ref } arg when VariableOf(arg.Value) is { } av: yield return av; break;
            case IDeclarationPatternOperation { DeclaredSymbol: { } ds }: yield return ds; break;
            case IRecursivePatternOperation { DeclaredSymbol: { } rs }: yield return rs; break;
        }
    }

    static bool IsPureWriteTarget(IOperation r)
    {
        var p = r.Parent;
        return p is ISimpleAssignmentOperation sa && ReferenceEquals(sa.Target, r) || p is IArgumentOperation { Parameter.RefKind: RefKind.Out };
    }

    static DefUse ComputeDefUse(ControlFlowGraph g)
    {
        // definitions, numbered
        var defOps = new List<(IOperation op, ISymbol v)>();
        var blockOps = new List<IOperation>[g.Blocks.Length];
        for (int i = 0; i < g.Blocks.Length; i++)
        {
            var b = g.Blocks[i];
            var roots = b.Operations.ToList();
            if (b.BranchValue != null) roots.Add(b.BranchValue);
            blockOps[i] = roots;
            foreach (var r in roots) foreach (var o in r.DescendantsAndSelf()) foreach (var v in DefsOf(o)) defOps.Add((o, v));
        }
        int nd = defOps.Count;
        var defIndex = new Dictionary<(IOperation, ISymbol), int>();
        for (int i = 0; i < nd; i++) defIndex[(defOps[i].op, defOps[i].v)] = i;
        var byVar = new Dictionary<ISymbol, List<int>>(SymbolEqualityComparer.Default);
        for (int i = 0; i < nd; i++) { if (!byVar.TryGetValue(defOps[i].v, out var l)) byVar[defOps[i].v] = l = []; l.Add(i); }

        var outSets = new System.Collections.BitArray[g.Blocks.Length];
        for (int i = 0; i < outSets.Length; i++) outSets[i] = new System.Collections.BitArray(nd);
        System.Collections.BitArray Transfer(int bi, System.Collections.BitArray input, Action<IOperation, System.Collections.BitArray>? onUse)
        {
            var cur = new System.Collections.BitArray(input);
            foreach (var root in blockOps[bi])
            {
                var all = root.DescendantsAndSelf().ToList();
                if (onUse != null)
                    foreach (var o in all)
                        if (o is ILocalReferenceOperation or IParameterReferenceOperation && !IsPureWriteTarget(o)) onUse(o, cur);
                foreach (var o in all)
                    foreach (var v in DefsOf(o))
                    {
                        foreach (var k in byVar[v]) cur[k] = false;
                        cur[defIndex[(o, v)]] = true;
                    }
            }
            return cur;
        }
        System.Collections.BitArray InOf(int bi)
        {
            var b = g.Blocks[bi];
            var acc = new System.Collections.BitArray(nd);
            if (b.Predecessors.Length == 0 && b.Kind != BasicBlockKind.Entry)
            {
                // a handler's first block: anything defined in the method may reach it
                for (int i = 0; i < nd; i++) acc[i] = true;
                return acc;
            }
            foreach (var p in b.Predecessors) acc.Or(outSets[p.Source.Ordinal]);
            return acc;
        }
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < g.Blocks.Length; i++)
            {
                var nout = Transfer(i, InOf(i), null);
                for (int k = 0; k < nd; k++) if (nout[k] != outSets[i][k]) { outSets[i] = nout; changed = true; break; }
            }
        }
        var reaching = new Dictionary<IOperation, IReadOnlyList<IOperation>>();
        var uses = new Dictionary<ISymbol, List<IOperation>>(SymbolEqualityComparer.Default);
        for (int i = 0; i < g.Blocks.Length; i++)
            Transfer(i, InOf(i), (use, cur) =>
            {
                var v = VariableOf(use)!;
                var list = new List<IOperation>();
                if (byVar.TryGetValue(v, out var ds)) foreach (var k in ds) if (cur[k]) list.Add(defOps[k].op);
                reaching[use] = list;
                if (!uses.TryGetValue(v, out var ul)) uses[v] = ul = [];
                ul.Add(use);
            });
        return new DefUse
        {
            Graph = g,
            ReachingDefinitions = reaching,
            Definitions = byVar.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<IOperation>)kv.Value.Select(k => defOps[k].op).ToList(), (IEqualityComparer<ISymbol>)SymbolEqualityComparer.Default),
            Uses = uses.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<IOperation>)kv.Value, (IEqualityComparer<ISymbol>)SymbolEqualityComparer.Default),
        };
    }

    // ---------------- update rules (closed forms) ----------------
    public IReadOnlyList<UpdateRule> UpdateRules(IMethodSymbol m)
    {
        m = m.OriginalDefinition;
        var rules = new List<UpdateRule>();
        var g = Graph(m);
        if (g == null) return rules;
        // loops: a branch back to an earlier block; the loop is the blocks between
        var loops = new Dictionary<int, int>();
        foreach (var b in g.Blocks)
            foreach (var br in new[] { b.FallThroughSuccessor, b.ConditionalSuccessor })
                if (br?.Destination is { } d && d.Ordinal <= b.Ordinal)
                    loops[d.Ordinal] = Math.Max(loops.GetValueOrDefault(d.Ordinal, d.Ordinal), b.Ordinal);
        foreach (var (head, last) in loops)
        {
            var ops = new List<IOperation>();
            for (int i = head; i <= last; i++) { ops.AddRange(g.Blocks[i].Operations.SelectMany(o => o.DescendantsAndSelf())); if (g.Blocks[i].BranchValue != null) ops.AddRange(g.Blocks[i].BranchValue!.DescendantsAndSelf()); }
            AddRules(rules, m, ops, g.Blocks[head]);
        }
        // per call: updates of the object's or type's own state outside loops and branches
        var body = Body(m);
        if (body != null)
        {
            var inLoops = new HashSet<IOperation>();
            foreach (var l in body.Descendants().OfType<ILoopOperation>()) foreach (var o in l.Descendants()) inLoops.Add(o);
            var top = body.Descendants().Where(o => !inLoops.Contains(o) && ConditionsOf(o, body).Count == 0 && o is not IAnonymousFunctionOperation).ToList();
            var all = body.Descendants().ToList();
            AddRules(rules, m, all, null, onlyMembers: true, candidates: top);
        }
        return rules;
    }

    static ISymbol? TargetOf(IOperation? t) => t switch
    {
        ILocalReferenceOperation l => l.Local,
        IParameterReferenceOperation p => p.Parameter.OriginalDefinition,
        IFieldReferenceOperation f when f.Instance is null or IInstanceReferenceOperation => f.Field.OriginalDefinition,
        IPropertyReferenceOperation p when p.Instance is null or IInstanceReferenceOperation => p.Property.OriginalDefinition,
        IConversionOperation { IsImplicit: true } c => TargetOf(c.Operand),
        _ => null,
    };

    static IOperation Strip(IOperation o) { while (o is IConversionOperation { IsImplicit: true } c) o = c.Operand; return o; }

    void AddRules(List<UpdateRule> rules, IMethodSymbol m, List<IOperation> region, BasicBlock? loop, bool onlyMembers = false, List<IOperation>? candidates = null)
    {
        // what the region writes
        var written = new Dictionary<ISymbol, List<IOperation>>(SymbolEqualityComparer.Default);
        foreach (var o in region)
        {
            IOperation? target = o switch
            {
                IAssignmentOperation a when a is not IDeconstructionAssignmentOperation => a.Target,
                IIncrementOrDecrementOperation inc => inc.Target,
                IArgumentOperation { Parameter.RefKind: RefKind.Out or RefKind.Ref } arg => arg.Value,
                _ => null,
            };
            if (target == null) continue;
            var s = TargetOf(target) ?? (target is IMemberReferenceOperation mr ? mr.Member.OriginalDefinition : null);
            if (s == null) continue;
            if (!written.TryGetValue(s, out var l)) written[s] = l = [];
            l.Add(o);
        }
        bool Invariant(IOperation e, ISymbol self)
        {
            foreach (var x in e.DescendantsAndSelf())
            {
                if (x is IInvocationOperation or IObjectCreationOperation or IAwaitOperation) return false;
                ISymbol? s = x switch
                {
                    ILocalReferenceOperation l => l.Local,
                    IParameterReferenceOperation p => p.Parameter.OriginalDefinition,
                    IMemberReferenceOperation mr => mr.Member.OriginalDefinition,
                    _ => null,
                };
                if (s == null) continue;
                if (SymbolEqualityComparer.Default.Equals(s, self) || written.ContainsKey(s)) return false;
            }
            return true;
        }
        foreach (var (sym, defs) in written)
        {
            if (defs.Count != 1) continue;
            if (onlyMembers && sym is not (IFieldSymbol or IPropertySymbol)) continue;
            var d = defs[0];
            if (candidates != null && !candidates.Contains(d)) continue;
            UpdateRule? r = d switch
            {
                ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add } ca when Invariant(ca.Value, sym) => new UpdateRule(sym, m, d, UpdateKind.Add, null, ca.Value, loop),
                ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Subtract } ca when Invariant(ca.Value, sym) => new UpdateRule(sym, m, d, UpdateKind.Add, null, ca.Value, loop) { StepSign = -1 },
                ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Multiply } ca when Invariant(ca.Value, sym) => new UpdateRule(sym, m, d, UpdateKind.Multiply, ca.Value, null, loop),
                IIncrementOrDecrementOperation inc => new UpdateRule(sym, m, d, UpdateKind.Add, null, null, loop) { StepSign = inc.Kind == OperationKind.Increment ? 1 : -1 },
                ISimpleAssignmentOperation sa => FromExpression(sym, m, d, Strip(sa.Value), loop, Invariant),
                _ => null,
            };
            if (r != null) rules.Add(r);
        }
    }

    static UpdateRule? FromExpression(ISymbol sym, IMethodSymbol m, IOperation d, IOperation v, BasicBlock? loop, Func<IOperation, ISymbol, bool> inv)
    {
        bool IsSelf(IOperation o) => SymbolEqualityComparer.Default.Equals(TargetOf(Strip(o)), sym);
        if (v is not IBinaryOperation b) return null;
        var l = Strip(b.LeftOperand); var r = Strip(b.RightOperand);
        switch (b.OperatorKind)
        {
            case BinaryOperatorKind.Add when IsSelf(l) && inv(r, sym): return new UpdateRule(sym, m, d, UpdateKind.Add, null, r, loop);
            case BinaryOperatorKind.Add when IsSelf(r) && inv(l, sym): return new UpdateRule(sym, m, d, UpdateKind.Add, null, l, loop);
            case BinaryOperatorKind.Subtract when IsSelf(l) && inv(r, sym): return new UpdateRule(sym, m, d, UpdateKind.Add, null, r, loop) { StepSign = -1 };
            case BinaryOperatorKind.Multiply when IsSelf(l) && inv(r, sym): return new UpdateRule(sym, m, d, UpdateKind.Multiply, r, null, loop);
            case BinaryOperatorKind.Multiply when IsSelf(r) && inv(l, sym): return new UpdateRule(sym, m, d, UpdateKind.Multiply, l, null, loop);
            case BinaryOperatorKind.Add:
                {
                    // a * x + b, x * a + b, b + a * x
                    (IOperation mul, IOperation add) = l is IBinaryOperation { OperatorKind: BinaryOperatorKind.Multiply } ? (l, r) : (r, l);
                    if (Strip(mul) is IBinaryOperation { OperatorKind: BinaryOperatorKind.Multiply } mb && inv(add, sym))
                    {
                        var ml = Strip(mb.LeftOperand); var mr = Strip(mb.RightOperand);
                        if (IsSelf(ml) && inv(mr, sym)) return new UpdateRule(sym, m, d, UpdateKind.Affine, mr, add, loop);
                        if (IsSelf(mr) && inv(ml, sym)) return new UpdateRule(sym, m, d, UpdateKind.Affine, ml, add, loop);
                    }
                    return null;
                }
        }
        return null;
    }

    // ---------------- field targets and aliasing ----------------
    public IReadOnlyCollection<ITypeSymbol> PointsTo(IFieldSymbol field)
    {
        var result = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var a in AccessesOf(field))
        {
            if (a.Kind == AccessKind.Read) continue;
            var op = a.Operation;
            while (op.Parent is IConversionOperation { IsImplicit: true }) op = op.Parent;
            if (op.Parent is not IAssignmentOperation asg || !ReferenceEquals(asg.Target, op)) continue;
            var v = Strip(asg.Value);
            if (v.Type == null || v.Type.IsValueType || v.ConstantValue is { HasValue: true, Value: null }) continue;
            result.Add(v.Type);
        }
        foreach (var t in result.ToList())
            foreach (var c in D.ConstructedTypes())
                if (IsSubtype(c, t)) result.Add(c);
        return result;
    }

    static bool IsSubtype(ITypeSymbol a, ITypeSymbol b)
    {
        if (SymbolEqualityComparer.Default.Equals(a.OriginalDefinition, b.OriginalDefinition)) return true;
        for (var x = a.BaseType; x != null; x = x.BaseType) if (SymbolEqualityComparer.Default.Equals(x.OriginalDefinition, b.OriginalDefinition)) return true;
        return a.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, b.OriginalDefinition));
    }

    public bool MayAlias(IOperation a, IOperation b)
    {
        if (ReferenceEquals(a, b)) return true;
        var ta = Strip(a).Type; var tb = Strip(b).Type;
        if (ta == null || tb == null || ta.IsValueType || tb.IsValueType) return false;
        if (Strip(a) is IObjectCreationOperation or IArrayCreationOperation && Strip(b) is IObjectCreationOperation or IArrayCreationOperation) return false;
        if (ta.SpecialType == SpecialType.System_Object || tb.SpecialType == SpecialType.System_Object) return true;
        if (IsSubtype(ta, tb) || IsSubtype(tb, ta)) return true;
        // unrelated types: only an object of a type that is both
        return D.ConstructedTypes().Any(c => IsSubtype(c, ta) && IsSubtype(c, tb));
    }

    // ---------------- changes ----------------
    int _sources;

    /// <summary>Generated code is part of the program it is generated for: it names the program's internal types and
    /// its private members as the program's own code does (a gate reads a private field, a solver writes one). Roslyn
    /// binds a compilation that way when its options say so (BinderFlags.IgnoreAccessibility, which its own
    /// expression compilers use); the options keep the flag in a property with no public setter. Where it cannot be
    /// set, generated code reaches private fields through [UnsafeAccessor] and internal types not at all.</summary>
    static CSharpCompilationOptions IgnoringAccessibility(CSharpCompilationOptions options)
    {
        const uint ignoreAccessibility = 1u << 22;
        try
        {
            var prop = typeof(CSharpCompilationOptions).GetProperty("TopLevelBinderFlags", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (prop == null || !prop.PropertyType.IsEnum) return options;
            // (a copy: the program's own options are not touched)
            var copy = options.WithAllowUnsafe(!options.AllowUnsafe).WithAllowUnsafe(options.AllowUnsafe);
            if (ReferenceEquals(copy, options)) return options;
            prop.SetValue(copy, Enum.ToObject(prop.PropertyType, ignoreAccessibility));
            return copy;
        }
        catch (Exception) { return options; }
    }
    public Compilation AddSource(string code, string path)
    {
        var tree = CSharpSyntaxTree.ParseText(code, (CSharpParseOptions)D.Main.SyntaxTrees.First().Options, path);
        var comp = CSharpCompilation.Create("cs2wasm.plugin" + (++_sources), [tree], D.Main.References.Append(D.Main.ToMetadataReference()),
            IgnoringAccessibility(D.Main.Options.WithOutputKind(OutputKind.DynamicallyLinkedLibrary).WithMetadataImportOptions(MetadataImportOptions.All)));
        // (not "inconsistent accessibility", CS0050 to CS0059: generated code is part of the program, and a method of it
        // that takes one of the program's internal types is as visible as that type needs)
        foreach (var e in comp.GetDiagnostics())
            if (e.Severity == DiagnosticSeverity.Error && !(e.Id.Length == 6 && e.Id.StartsWith("CS005", StringComparison.Ordinal))) D.Diagnostics.Add(e);
        return D.AddInput(comp, _rt);
    }

    public void AddMemberHook(ISymbol member, IMethodSymbol hook, int tag, InstrumentPoint point, bool write)
    {
        var def = member.OriginalDefinition;
        var table = write ? D.WriteHooks : D.ReadHooks;
        if (!table.TryGetValue(def, out var list)) table[def] = list = [];
        list.Add(new MemberHook(hook, tag, point));
        foreach (var a in AccessesOf(def)) D.MarkInstrumented(a.Method);
    }

    public void AddSiteWriteHook(IOperation write, IMethodSymbol hook, int tag, InstrumentPoint point)
    {
        if (write is not (ISimpleAssignmentOperation or ICompoundAssignmentOperation or IIncrementOrDecrementOperation or ICoalesceAssignmentOperation))
        { D.Report(Diags.Plugin, write.Syntax.GetLocation(), $"InstrumentWrite: '{write.Syntax}' is not an assignment, a compound assignment, an increment or a ??="); return; }
        var key = Driver.SiteKey(write.Syntax);
        if (!D.SiteWriteHooks.TryGetValue(key, out var list)) D.SiteWriteHooks[key] = list = [];
        list.Add(new MemberHook(hook, tag, point));
        if (D.ModelFor(write.Syntax.SyntaxTree).GetEnclosingSymbol(write.Syntax.SpanStart) is { } enc) D.MarkInstrumented(enc);
    }

    public void AddValueHook(IOperation expression, IMethodSymbol hook, int tag)
    {
        var loc = expression.Syntax.GetLocation();
        var t = expression.Type;
        if (t == null || t.SpecialType == SpecialType.System_Void) { D.Report(Diags.Plugin, loc, $"InterceptValue: '{expression.Syntax}' has no value"); return; }
        bool shape = hook.IsStatic && !hook.IsGenericMethod && hook.Parameters.Length is 1 or 2 && hook.Parameters[0].RefKind == RefKind.None
            && (hook.Parameters.Length == 1 || hook.Parameters[1].Type.SpecialType == SpecialType.System_Int32)
            && SameType(hook.ReturnType, hook.Parameters[0].Type) && SameType(hook.Parameters[0].Type, t);
        // (the hook is of another compilation than the expression: the same type is another symbol there)
        static bool SameType(ITypeSymbol a, ITypeSymbol b) =>
            a.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == b.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!shape) { D.Report(Diags.Plugin, loc, $"InterceptValue: the hook for '{expression.Syntax}' must be a static method {t} {hook.Name}({t} value) or ({t} value, int tag)"); return; }
        var key = (Driver.SiteKey(expression.Syntax), expression.Kind);
        if (!D.ValueHooks.TryGetValue(key, out var list)) D.ValueHooks[key] = list = [];
        list.Add(new ValueHook { Hook = hook, Tag = tag, Location = loc, Text = expression.Syntax.ToString() });
        if (D.ModelFor(expression.Syntax.SyntaxTree).GetEnclosingSymbol(expression.Syntax.SpanStart) is { } enc) D.MarkInstrumented(enc);
    }

    public void AddStatementHook(IOperation statement, IMethodSymbol hook, int tag)
    {
        var key = Driver.SiteKey(statement.Syntax);
        if (!D.StatementHooks.TryGetValue(key, out var list)) D.StatementHooks[key] = list = [];
        list.Add((hook, tag));
        var model = D.ModelFor(statement.Syntax.SyntaxTree);
        if (model.GetEnclosingSymbol(statement.Syntax.SpanStart) is { } enc) D.MarkInstrumented(enc);
    }

    public void Clone(IMethodSymbol method, IEnumerable<IMethodSymbol> suppressed, string exportName)
    {
        var spec = D.NewClone(suppressed.Select(s => Types.MethodKey(D.CanonicalMethod(s))));
        var mi = D.CloneMethod(method, spec);
        _rt.Export(exportName, mi);
    }
}

internal sealed partial class Driver
{
    static readonly HashSet<string> s_blocking = ["Task.Wait", "Task.WaitAll", "Task.WaitAny", "Task.get_Result", "TaskAwaiter.GetResult",
        "ValueTaskAwaiter.GetResult", "Thread.Join", "Thread.Sleep", "SemaphoreSlim.Wait", "ManualResetEventSlim.Wait", "CountdownEvent.Wait", "Monitor.Wait"];

    static readonly HashSet<string> s_threadStarters = ["Thread..ctor", "Task.Run", "ThreadPool.QueueUserWorkItem", "ThreadPool.UnsafeQueueUserWorkItem",
        "Parallel.For", "Parallel.ForEach", "Parallel.Invoke", "TaskFactory.StartNew", "Task.Factory.StartNew"];

    /// <summary>Section 4.5: a `lock` on the engine thread whose object worker threads lock too (a field, static or
    /// not, also locked in a delegate handed to Thread, Task.Run, the thread pool or Parallel, or in what such a
    /// delegate calls) may wait for a worker: a build notice at the engine side's lock.</summary>
    void ReportContendedLocks(IEnumerable<MethodInst> exports)
    {
        static ISymbol? Target(ILockOperation l)
        {
            var v = l.LockedValue;
            while (v is IConversionOperation c) v = c.Operand;
            return v switch { IFieldReferenceOperation f => f.Field.OriginalDefinition, _ => null };
        }
        // worker code: the delegates handed to thread starters, and the methods they call directly
        var workerLocks = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var workerMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        void LocksIn(IOperation body)
        {
            foreach (var op in body.DescendantsAndSelf())
            {
                if (op is ILockOperation lk && Target(lk) is { } t) workerLocks.Add(t);
                if (op is IInvocationOperation inv && inv.TargetMethod.Locations.FirstOrDefault()?.SourceTree is { } st && IsInput(st)) workerMethods.Add(inv.TargetMethod.OriginalDefinition);
            }
        }
        foreach (var comp in Inputs)
            foreach (var tree in comp.SyntaxTrees)
            {
                var model = ModelFor(tree);
                foreach (var node in tree.GetRoot().DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
                {
                    if (model.GetOperation(node) is not IAnonymousFunctionOperation af) continue;
                    var p = af.Parent;
                    while (p is IDelegateCreationOperation or IConversionOperation) p = p.Parent;
                    if (p is IArgumentOperation { Parent: var call }
                        && (call is IInvocationOperation { TargetMethod: var tm } && s_threadStarters.Contains(tm.ContainingType.Name + "." + tm.Name)
                            || call is IObjectCreationOperation { Constructor: { } ctor } && s_threadStarters.Contains(ctor.ContainingType.Name + "..ctor")))
                        LocksIn(af.Body);
                }
            }
        foreach (var m in workerMethods.ToList())
            foreach (var r in m.DeclaringSyntaxReferences)
                if (ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax()) is { } body)
                    foreach (var lk in body.DescendantsAndSelf().OfType<ILockOperation>())
                        if (Target(lk) is { } t) workerLocks.Add(t);
        if (workerLocks.Count == 0) return;
        // the engine side: what the exports reach, not the delegates they hand out
        var seen = new HashSet<MethodInst>();
        var work = new Queue<MethodInst>(exports);
        var reported = new HashSet<(string, int)>();
        while (work.Count > 0)
        {
            var mi = work.Dequeue();
            if (!seen.Add(mi) || mi.Sym.MethodKind == MethodKind.AnonymousFunction || mi.Key.EndsWith(" MoveNext")) continue;
            foreach (var c in mi.Callees) work.Enqueue(c);
            foreach (var r in mi.Sym.OriginalDefinition.DeclaringSyntaxReferences)
            {
                if (!IsInput(r.SyntaxTree) || ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax()) is not { } body) continue;
                var nested = body.Descendants().Where(o => o is IAnonymousFunctionOperation or ILocalFunctionOperation).SelectMany(o => o.DescendantsAndSelf()).ToHashSet();
                foreach (var lk in body.DescendantsAndSelf().OfType<ILockOperation>())
                {
                    if (nested.Contains(lk) || Target(lk) is not { } t || !workerLocks.Contains(t)) continue;
                    if (!reported.Add((lk.Syntax.SyntaxTree.FilePath, lk.Syntax.SpanStart))) continue;
                    Report(Diags.BlockingWait, lk.Syntax.GetLocation(), "lock (" + t.Name + ")");
                }
            }
        }
    }

    static string? BlockingName(IMethodSymbol m)
    {
        var name = m.ContainingType?.Name + "." + m.Name;
        return s_blocking.Contains(name) ? m.ContainingType!.Name + "." + (m.Name == "get_Result" ? "Result" : m.Name) : null;
    }

    /// <summary>Section 4.5: a blocking wait the engine thread can reach (an export and what it calls directly, not
    /// lambdas handed to other threads) is a build notice naming it.</summary>
    void ReportEngineThreadBlocking(IEnumerable<MethodInst> exports)
    {
        ReportContendedLocks(exports);
        var seen = new HashSet<MethodInst>();
        var work = new Queue<MethodInst>(exports);
        var reported = new HashSet<(string, int)>();
        while (work.Count > 0)
        {
            var mi = work.Dequeue();
            if (!seen.Add(mi) || mi.Sym.MethodKind == MethodKind.AnonymousFunction || mi.Key.EndsWith(" MoveNext")) continue;
            foreach (var c in mi.Callees) work.Enqueue(c);
            if (!mi.Callees.Any(c => BlockingName(c.Sym) != null)) continue;
            var def = mi.Sym.OriginalDefinition;
            foreach (var r in def.DeclaringSyntaxReferences)
            {
                if (!IsInput(r.SyntaxTree)) continue;
                var node = r.GetSyntax();
                var model = ModelFor(node.SyntaxTree);
                foreach (var sn in node.DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
                {
                    if (sn is not (InvocationExpressionSyntax or MemberAccessExpressionSyntax)) continue;
                    var sym = model.GetSymbolInfo(sn).Symbol;
                    var m = sym as IMethodSymbol ?? (sym as IPropertySymbol)?.GetMethod;
                    if (m == null || BlockingName(m) is not { } bn) continue;
                    if (sym is IMethodSymbol && sn is MemberAccessExpressionSyntax) continue; // the invocation reports it
                    if (!reported.Add((sn.SyntaxTree.FilePath, sn.SpanStart))) continue;
                    Report(Diags.BlockingWait, sn.GetLocation(), bn);
                }
            }
        }
    }
}
