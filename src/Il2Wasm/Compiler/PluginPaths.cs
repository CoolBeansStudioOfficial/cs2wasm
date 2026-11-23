using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Il2Wasm.Compiler;

// Plugin interface version 5 (audit A4.8): conditions with early returns and across calls, and the alias query from
// where references come from (allocations, through def-use) and which new objects the method owns alone (the
// compiler's escape analysis).
internal sealed partial class PluginHost
{
    // ---------------- conditions ----------------
    /// <summary>Whether control never leaves a statement at its end (it returns, throws, breaks, continues or jumps).</summary>
    static bool AlwaysJumps(IOperation? s) => s switch
    {
        IReturnOperation { Kind: not OperationKind.YieldReturn } => true,
        IBranchOperation => true,
        IThrowOperation => true,
        IExpressionStatementOperation { Operation: IThrowOperation } => true,
        IBlockOperation b => b.Operations.Length > 0 && AlwaysJumps(b.Operations[^1]),
        IConditionalOperation { Type: null, WhenFalse: { } f } c => AlwaysJumps(c.WhenTrue) && AlwaysJumps(f),
        _ => false,
    };

    /// <summary>What an earlier statement of the same list says about the later ones: `if (c) return;` (or a branch
    /// that always leaves) runs them only when c is false.</summary>
    static BranchCondition? Guard(IOperation s) => s switch
    {
        IConditionalOperation { Type: null } c when AlwaysJumps(c.WhenTrue) && !AlwaysJumps(c.WhenFalse) => new(c.Condition, false),
        IConditionalOperation { Type: null, WhenFalse: { } f } c when AlwaysJumps(f) && !AlwaysJumps(c.WhenTrue) => new(c.Condition, true),
        _ => null,
    };

    /// <summary>The conditions before `child` in a statement list, nearest first, up to a label (a jump may land there,
    /// past the guards before it).</summary>
    static void Guards(IReadOnlyList<IOperation> list, IOperation child, List<BranchCondition> into)
    {
        if (child is ILabeledOperation) return;
        int i = -1;
        for (int k = 0; k < list.Count; k++) if (ReferenceEquals(list[k], child)) { i = k; break; }
        for (int k = i - 1; k >= 0; k--)
        {
            if (list[k] is ILabeledOperation) return;
            if (Guard(list[k]) is { } g) into.Add(g);
        }
    }

    /// <summary>The branches an operation runs under, outermost first, early returns included: up to `root`, or with
    /// `stopAtFunctions` up to the lambda or local function it is in (its conditions are where the function runs,
    /// not where it is made).</summary>
    static IReadOnlyList<BranchCondition> ConditionsWithin(IOperation op, IOperation? root, bool stopAtFunctions)
    {
        var list = new List<BranchCondition>();
        IOperation child = op;
        for (var p = op.Parent; p != null && !ReferenceEquals(child, root); child = p, p = p.Parent)
        {
            if (stopAtFunctions && p is IAnonymousFunctionOperation or ILocalFunctionOperation) break;
            switch (p)
            {
                case IBlockOperation b: Guards(b.Operations, child, list); break;
                case IConditionalOperation c when ReferenceEquals(c.WhenTrue, child): list.Add(new(c.Condition, true)); break;
                case IConditionalOperation c when ReferenceEquals(c.WhenFalse, child): list.Add(new(c.Condition, false)); break;
                case IWhileLoopOperation w when ReferenceEquals(w.Body, child) && w.Condition != null: list.Add(new(w.Condition, !w.ConditionIsUntil)); break;
                case IForLoopOperation f when ReferenceEquals(f.Body, child) && f.Condition != null: list.Add(new(f.Condition, true)); break;
                case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd } b when ReferenceEquals(b.RightOperand, child): list.Add(new(b.LeftOperand, true)); break;
                case IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr } b when ReferenceEquals(b.RightOperand, child): list.Add(new(b.LeftOperand, false)); break;
                case ISwitchCaseOperation sc:
                    Guards(sc.Body, child, list);
                    foreach (var cl in sc.Clauses) list.Add(new(cl, true));
                    break;
                case ISwitchExpressionArmOperation arm when !ReferenceEquals(arm.Pattern, child): list.Add(new(arm.Pattern, true)); if (arm.Guard != null && !ReferenceEquals(arm.Guard, child)) list.Add(new(arm.Guard, true)); break;
                case IConditionalAccessOperation ca when ReferenceEquals(ca.WhenNotNull, child): list.Add(new(ca.Operation, true)); break;
                case ICatchClauseOperation cc when cc.Filter != null && !ReferenceEquals(cc.Filter, child): list.Add(new(cc.Filter, true)); break;
            }
        }
        list.Reverse();
        return list;
    }

    // ---------------- calls ----------------
    Dictionary<IMethodSymbol, List<CallSite>>? _sites;
    HashSet<IMethodSymbol>? _delegateTargets;
    Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>? _dispatchBases;

    /// <summary>The lambda or local function an operation is in, else null.</summary>
    static IMethodSymbol? InnerFunction(IOperation op)
    {
        for (var p = op.Parent; p != null; p = p.Parent)
            switch (p)
            {
                case IAnonymousFunctionOperation af: return af.Symbol;
                case ILocalFunctionOperation lf: return lf.Symbol;
            }
        return null;
    }

    /// <summary>The function (method, accessor, lambda, local function) an operation runs in, as a definition.</summary>
    IMethodSymbol? FunctionOf(IOperation op)
    {
        if (InnerFunction(op) is { } f) return f;
        var model = op.SemanticModel ?? D.ModelFor(op.Syntax.SyntaxTree);
        var s = model.GetEnclosingSymbol(op.Syntax.SpanStart);
        while (s is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } m && m.ContainingSymbol != null) s = m.ContainingSymbol;
        return (s as IMethodSymbol)?.OriginalDefinition;
    }

    void BuildCalls()
    {
        if (_sites != null) return;
        _sites = new(SymbolEqualityComparer.Default);
        _delegateTargets = new(SymbolEqualityComparer.Default);
        _dispatchBases = new(SymbolEqualityComparer.Default);
        void Add(IMethodSymbol caller, IMethodSymbol? callee, IOperation op)
        {
            if (callee == null) return;
            var def = callee.OriginalDefinition;
            if (!_sites.TryGetValue(def, out var l)) _sites[def] = l = [];
            l.Add(new CallSite(caller, def, op, ConditionsWithin(op, null, stopAtFunctions: true)));
        }
        foreach (var m in SourceDefinitions())
        {
            var body = Body(m);
            if (body == null) continue;
            foreach (var op in body.DescendantsAndSelf())
            {
                var caller = InnerFunction(op) ?? m;
                switch (op)
                {
                    case IInvocationOperation inv: Add(caller, inv.TargetMethod, op); break;
                    case IObjectCreationOperation oc: Add(caller, oc.Constructor, op); break;
                    case IPropertyReferenceOperation pr:
                        {
                            var kind = KindOf(pr);
                            if (kind != AccessKind.Write) Add(caller, pr.Property.GetMethod, op);
                            if (kind != AccessKind.Read) Add(caller, pr.Property.SetMethod, op);
                            break;
                        }
                    case IMethodReferenceOperation mr: _delegateTargets.Add(mr.Method.OriginalDefinition); break;
                    case IAnonymousFunctionOperation af: _delegateTargets.Add(af.Symbol); break;
                }
            }
            // what a call of a virtual or interface method may run
            for (var b = m.OverriddenMethod; b != null; b = b.OverriddenMethod) Base(m, b);
            foreach (var x in m.ExplicitInterfaceImplementations) Base(m, x);
        }
        foreach (var t in KnownTypes().OfType<INamedTypeSymbol>())
            foreach (var i in t.AllInterfaces)
                foreach (var im in i.GetMembers().OfType<IMethodSymbol>())
                    if (t.FindImplementationForInterfaceMember(im) is IMethodSymbol impl) Base(impl.OriginalDefinition, im);
        void Base(IMethodSymbol impl, IMethodSymbol b)
        {
            if (!_dispatchBases.TryGetValue(impl.OriginalDefinition, out var s)) _dispatchBases[impl.OriginalDefinition] = s = new(SymbolEqualityComparer.Default);
            s.Add(b.OriginalDefinition);
        }
    }

    IEnumerable<IMethodSymbol> DispatchBases(IMethodSymbol m)
    {
        BuildCalls();
        return _dispatchBases!.TryGetValue(m.OriginalDefinition, out var s) ? s : [];
    }

    /// <summary>The calls of a method in reachable source code, a virtual or interface method's calls included.</summary>
    public IReadOnlyList<CallSite> CallersOf(IMethodSymbol method)
    {
        BuildCalls();
        var def = method.OriginalDefinition;
        var r = new List<CallSite>();
        if (_sites!.TryGetValue(def, out var own)) r.AddRange(own);
        foreach (var b in DispatchBases(def))
            if (_sites.TryGetValue(b, out var l)) r.AddRange(l);
        return r;
    }

    static bool InSource(ISymbol s) => s.Locations.Any(l => l.IsInSource);

    /// <summary>Every way control reaches the operation: paths of calls with their conditions.</summary>
    public IReadOnlyList<ConditionPath> PathConditions(IOperation op, int maxDepth, int maxPaths)
    {
        BuildCalls();
        var paths = new List<ConditionPath>();
        var start = FunctionOf(op);
        if (start == null) return paths;
        var own = ConditionsWithin(op, null, stopAtFunctions: true);
        var onPath = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { start };
        void Walk(IMethodSymbol f, List<CallSite> calls, List<BranchCondition> conds)
        {
            if (paths.Count >= maxPaths) return;
            ConditionPath P(IMethodSymbol root, PathStart s) => new(root, s, calls.ToList(), conds.ToList());
            var sites = CallersOf(f);
            bool isDelegate = f.MethodKind == MethodKind.AnonymousFunction || _delegateTargets!.Contains(f.OriginalDefinition);
            if (isDelegate) paths.Add(P(f, PathStart.Delegate));
            // (a method the library or the engine may call too: an override of one of its methods, an export, a root)
            bool entry = sites.Count == 0 && !isDelegate || DispatchBases(f).Any(b => !InSource(b));
            if (entry) paths.Add(P(f, PathStart.Entry));
            if (sites.Count == 0) return;
            if (calls.Count >= maxDepth) { paths.Add(P(f, PathStart.Cut)); return; }
            foreach (var s in sites)
            {
                if (paths.Count >= maxPaths) { paths.Add(P(f, PathStart.Cut)); return; }
                var nc = new List<CallSite>(calls.Count + 1) { s }; nc.AddRange(calls);
                var nk = new List<BranchCondition>(s.Conditions); nk.AddRange(conds);
                if (!onPath.Add(s.Caller)) { paths.Add(new ConditionPath(s.Caller, PathStart.Recursion, nc, nk)); continue; }
                Walk(s.Caller, nc, nk);
                onPath.Remove(s.Caller);
            }
        }
        Walk(start, [], [.. own]);
        return paths;
    }

    // ---------------- aliasing ----------------
    enum OriginKind { Allocation, Parameter, Outside, Null }
    /// <summary>Where a reference may come from: a `new` (Site), a parameter's incoming value (Symbol; null: this),
    /// anything else (a field, an element, a call's result), or null.</summary>
    readonly record struct Origin(OriginKind Kind, IOperation? Site = null, ISymbol? Symbol = null);

    readonly Dictionary<IMethodSymbol, Dictionary<(string, int, int), List<IOperation>>> _usesBySyntax = new(SymbolEqualityComparer.Default);

    static IOperation StripRef(IOperation o)
    {
        while (o is IConversionOperation { OperatorMethod: null } c && (c.IsImplicit || c.Conversion.IsReference || c.Conversion.IsIdentity)) o = c.Operand;
        return o;
    }

    /// <summary>The origins of a reference-typed value (an operation of the program, a plugin's or the def-use
    /// graph's), or null when it cannot be followed (in a lambda, no graph).</summary>
    List<Origin>? Origins(IOperation v)
    {
        var m = FunctionOf(v);
        if (m == null || m.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction) return null;
        var du = DefUse(m);
        if (du == null) return null;
        if (!_usesBySyntax.TryGetValue(m, out var bySyntax))
        {
            bySyntax = [];
            foreach (var u in du.ReachingDefinitions.Keys)
            {
                if (u.Syntax == null) continue;
                var k = Driver.SiteKey(u.Syntax);
                if (!bySyntax.TryGetValue(k, out var l)) bySyntax[k] = l = [];
                l.Add(u);
            }
            _usesBySyntax[m] = bySyntax;
        }
        var result = new List<Origin>();
        var seen = new HashSet<IOperation>(ReferenceEqualityComparer.Instance);
        bool ok = true;
        void Defs(IOperation use, ISymbol variable)
        {
            IEnumerable<IOperation> defs;
            if (du.ReachingDefinitions.TryGetValue(use, out var direct)) defs = direct;
            else if (use.Syntax != null && bySyntax.TryGetValue(Driver.SiteKey(use.Syntax), out var uses)) defs = uses.SelectMany(u => du.ReachingDefinitions[u]);
            else { ok = false; return; }
            // (a parameter assigned somewhere: the incoming value may still reach)
            if (variable is IParameterSymbol ps) { result.Add(new(OriginKind.Parameter, Symbol: ps)); if (!du.Definitions.ContainsKey(variable)) return; }
            bool any = false;
            foreach (var d in defs)
            {
                any = true;
                if (!seen.Add(d)) continue;
                if (d is ISimpleAssignmentOperation sa) Walk(sa.Value);
                else result.Add(new(OriginKind.Outside));
            }
            if (!any && variable is ILocalSymbol) result.Add(new(OriginKind.Null));
        }
        void Walk(IOperation o)
        {
            o = StripRef(o);
            switch (o)
            {
                case IObjectCreationOperation or IArrayCreationOperation or IAnonymousObjectCreationOperation or ITypeParameterObjectCreationOperation or IDelegateCreationOperation:
                    result.Add(new(OriginKind.Allocation, Site: o)); break;
                case ILiteralOperation { ConstantValue: { HasValue: true, Value: null } } or IDefaultValueOperation:
                    result.Add(new(OriginKind.Null)); break;
                case IConditionalOperation { WhenFalse: { } wf } c when c.Type != null: Walk(c.WhenTrue); Walk(wf); break;
                case ICoalesceOperation co: Walk(co.Value); Walk(co.WhenNull); break;
                case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }: result.Add(new(OriginKind.Parameter)); break;
                case ILocalReferenceOperation l: Defs(o, l.Local); break;
                case IParameterReferenceOperation p: Defs(o, p.Parameter.OriginalDefinition); break;
                case IFlowCaptureReferenceOperation fc:
                    {
                        bool found = false;
                        foreach (var b in du.Graph.Blocks)
                            foreach (var x in b.Operations)
                                if (x is IFlowCaptureOperation cap && cap.Id.Equals(fc.Id) && seen.Add(cap)) { found = true; Walk(cap.Value); }
                        if (!found && !seen.Any(s => s is IFlowCaptureOperation c2 && c2.Id.Equals(fc.Id))) result.Add(new(OriginKind.Outside));
                        break;
                    }
                default: result.Add(new(OriginKind.Outside)); break;
            }
        }
        Walk(v);
        return ok ? result : null;
    }

    /// <summary>The `new` expressions whose objects a reference can name (an operation of each, to compare by syntax),
    /// or null when it can name an object made elsewhere (a field's, a parameter's, a call's).</summary>
    public IReadOnlyCollection<IOperation>? AllocationSites(IOperation reference)
    {
        var o = Origins(reference);
        if (o == null || o.Any(x => x.Kind is OriginKind.Parameter or OriginKind.Outside)) return null;
        return o.Where(x => x.Kind == OriginKind.Allocation).Select(x => x.Site!).GroupBy(s => Driver.SiteKey(s.Syntax)).Select(g => g.First()).ToList();
    }

    /// <summary>The program's own operation (the compiler's semantic model) at a site.</summary>
    IOperation? HostOp(IOperation op, IMethodSymbol m)
    {
        var body = Body(m);
        if (body == null || op.Syntax == null) return null;
        var key = Driver.SiteKey(op.Syntax);
        return body.DescendantsAndSelf().FirstOrDefault(x => x.Syntax != null && x.Kind == op.Kind && Driver.SiteKey(x.Syntax) == key)
            ?? body.DescendantsAndSelf().FirstOrDefault(x => x.Syntax != null && x is IObjectCreationOperation or IArrayCreationOperation or IAnonymousObjectCreationOperation or IDelegateCreationOperation && Driver.SiteKey(x.Syntax) == key);
    }

    /// <summary>Whether the object a `new` makes stays its method's own (section 4's single owner): no reference to it
    /// outlives the call or is kept by what the method calls (it is not stored, captured, returned or handed to code
    /// that keeps or returns it, by the compiler's escape analysis), so no reference from outside the method names it.</summary>
    public bool IsOwned(IOperation creation)
    {
        var m = FunctionOf(creation);
        if (m == null || m.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction || HostOp(creation, m) is not { } c) return false;
        if (StripRef(c) is not (IObjectCreationOperation or IArrayCreationOperation or IAnonymousObjectCreationOperation or IDelegateCreationOperation)) return false;
        var exact = c.Type as INamedTypeSymbol;
        var p = c.Parent;
        while (p is IConversionOperation { IsImplicit: true }) p = p.Parent;
        ILocalSymbol? local = p switch
        {
            IVariableInitializerOperation { Parent: IVariableDeclaratorOperation d } => d.Symbol,
            ISimpleAssignmentOperation { Target: ILocalReferenceOperation lr, IsRef: false } => lr.Local,
            _ => null,
        };
        if (local == null) return D.UseEscape(c, exact, alias: false) == Driver.Esc.No;
        if (local.RefKind != RefKind.None) return false;
        foreach (var r in Body(m)!.Descendants().OfType<ILocalReferenceOperation>())
        {
            if (!SymbolEqualityComparer.Default.Equals(r.Local, local)) continue;
            if (InnerFunction(r) != null) return false; // captured
            if (r.Parent is ISimpleAssignmentOperation { IsRef: false } a && ReferenceEquals(a.Target, r)) continue;
            if (D.UseEscape(r, exact, alias: false) != Driver.Esc.No) return false;
        }
        return true;
    }

    bool TypesMayAlias(IOperation a, IOperation b)
    {
        var ta = Strip(a).Type; var tb = Strip(b).Type;
        if (ta == null || tb == null || ta.IsValueType || tb.IsValueType) return false;
        if (ta.SpecialType == SpecialType.System_Object || tb.SpecialType == SpecialType.System_Object) return true;
        if (IsSubtype(ta, tb) || IsSubtype(tb, ta)) return true;
        if (KnownTypes().Any(c => IsSubtype(c, ta) && IsSubtype(c, tb))) return true;
        // (before the compile: a library type may be both, when both are the library's)
        return BeforeCompile && !InSource(ta) && !InSource(tb);
    }

    /// <summary>Whether two references can name the same object (false only when it is certain): by where each comes
    /// from (two `new`s, a `new` the method owns alone and anything from outside it, a `new` and a parameter's
    /// incoming value), then by type.</summary>
    public bool MayAlias(IOperation a, IOperation b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (Strip(a).Type is { IsValueType: true } || Strip(b).Type is { IsValueType: true }) return false;
        if (!TypesMayAlias(a, b)) return false;
        var oa = Origins(a); var ob = Origins(b);
        if (oa == null || ob == null) return true;
        var ma = FunctionOf(a); var mb = FunctionOf(b);
        bool same = SymbolEqualityComparer.Default.Equals(ma, mb);
        var owned = new Dictionary<(string, int, int), bool>();
        bool Owned(IOperation site) { var k = Driver.SiteKey(site.Syntax); if (!owned.TryGetValue(k, out var r)) owned[k] = r = IsOwned(site); return r; }
        bool Pair(Origin x, Origin y)
        {
            if (x.Kind == OriginKind.Null || y.Kind == OriginKind.Null) return false;
            if (x.Kind == OriginKind.Allocation && y.Kind == OriginKind.Allocation)
                return same && Driver.SiteKey(x.Site!.Syntax) == Driver.SiteKey(y.Site!.Syntax);
            if (y.Kind == OriginKind.Allocation) (x, y) = (y, x);
            if (x.Kind == OriginKind.Allocation && same)
            {
                // (a parameter's incoming value was made before the call; nothing outside names an owned object)
                if (y.Kind == OriginKind.Parameter) return false;
                if (Owned(x.Site!)) return false;
            }
            return true;
        }
        foreach (var x in oa)
            foreach (var y in ob)
                if (Pair(x, y)) return true;
        return false;
    }
}
