using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Il2Wasm.Compiler;

// Stores that go down a structure (section 4.3; docs/AUDIT.md 7.E, the typed-edge proof for a field of its own type).
// A store `x.F = v` closes a cycle only if v reaches x. It does not when
//   - v was read out of x: `x.F = y.G` or `x.F = y` where y came from x by field reads (`y = x; y = y.F; ...`), and,
//     for y itself, the code has just found `x != y`. What x reached once it never is reached by (below);
//   - v is an object the method has just made and nothing else knows, whose constructor put into it only what was
//     read out of x (`x.F = new C(x)`, the constructor storing `from.F`; `n = new C(.., x.F); ...; x.F = n`).
// Why reads made earlier still count: order the objects of a component so that every reference made so far goes from
// a higher object to a lower one. A store of the first kind goes down (x reached v through such references); an
// object nothing refers to yet can be put anywhere above what it refers to, so a new object filled from below x goes
// just below x, a constructor's `this` on top, an isolated structure (FreshStores.cs) below its container. While every
// store of a component is one of those, no reference goes up and no cycle forms; such a store is linked as an
// isolated value's is (Arenas.Site.LinkIso): it does not make its site general, and where the site is cyclic through
// some other store it links as any store.
// StringBuilder's chunks are the case in the library: `m_ChunkPrevious = new StringBuilder(this)` (ExpandByABlock),
// `chunk.m_ChunkPrevious = newChunk` (MakeRoom), `endChunk.m_ChunkPrevious = chunk` after `endChunk != chunk` and
// `= chunk.m_ChunkPrevious` (Remove), `m_ChunkPrevious = chunk.m_ChunkPrevious` with chunk from FindChunkForIndex
// (Length's setter). A program's own list or tree whose links are rewired the same way is another.
// What is tracked: `this`, parameters, locals declared by a declarator and assigned only by plain assignments, never
// captured and never passed by reference; the facts flow over Roslyn's control flow graph (no method with a try). A
// `ref`/`out` parameter of a class type T counts as a local on a condition the whole program decides: no call passes
// anything but a local of its own by reference for a `ref T` (Arenas.RefWild; else the site is general after all).
internal sealed partial class Driver
{
    static readonly bool s_noChain = Environment.GetEnvironmentVariable("IL2WASM_NO_CHAIN_STORES") == "1";
    static readonly bool s_chainDebug = Environment.GetEnvironmentVariable("IL2WASM_CHAIN_DEBUG") == "1";
    internal static bool ChainDebug => s_chainDebug;
    internal static bool NoChainStores => s_noChain;
    /// <summary>IL2WASM_CHAIN_BREAK=1 (to see a test fail): a value in a plain local counts as read out of anything.</summary>
    static readonly bool s_chainBreak = Environment.GetEnvironmentVariable("IL2WASM_CHAIN_BREAK") == "1";

    /// <summary>A proven store: the types of the by-reference parameters the proof stands on.</summary>
    internal sealed record ChainProof(ITypeSymbol[] RefParamTypes);

    readonly Dictionary<ISymbol, Dictionary<SyntaxNode, ChainProof>> _chainStores = new(SymbolEqualityComparer.Default);
    readonly Dictionary<ISymbol, bool> _returnsBelow = new(SymbolEqualityComparer.Default);
    readonly Dictionary<ISymbol, byte[]?> _ctorHolds = new(SymbolEqualityComparer.Default);

    /// <summary>The proof for a field store of the method, or null.</summary>
    internal ChainProof? ChainSafeStore(IMethodSymbol method, ISimpleAssignmentOperation asg)
    {
        if (s_noChain || CompilingInstrumented) return null;
        method = method.OriginalDefinition;
        if (method.IsAsync || method.IsIterator || method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction) return null;
        for (var p = asg.Parent; p != null; p = p.Parent) if (p is IAnonymousFunctionOperation or ILocalFunctionOperation) return null;
        if (!_chainStores.TryGetValue(method, out var proofs))
        {
            _chainStores[method] = proofs = [];
            foreach (var r in method.DeclaringSyntaxReferences)
                if (ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax()) is { } body)
                    new ChainFlow(this, method, body).Run(proofs, null);
        }
        var found = proofs.GetValueOrDefault(asg.Syntax);
        if (s_chainDebug) Console.Error.WriteLine($"chain store {(found != null ? "proven" : "not proven")}: {asg.Syntax} in {method.ToDisplayString()}");
        return found;
    }

    /// <summary>An instance method that returns its receiver or what it read out of it (a walk down a chain).</summary>
    bool ReturnsBelowReceiver(IMethodSymbol m)
    {
        m = m.OriginalDefinition;
        if (m.IsStatic || m.IsVirtual || m.IsAbstract || m.IsOverride || m.IsAsync || m.IsIterator || m.IsExtern || m.MethodKind != MethodKind.Ordinary) return false;
        if (m.ContainingType is not { TypeKind: TypeKind.Class } || !m.ReturnType.IsReferenceType || m.DeclaringSyntaxReferences.Length != 1) return false;
        if (HooksSeeAny([m])) return false;
        if (_returnsBelow.TryGetValue(m, out var known)) return known;
        _returnsBelow[m] = false;
        var r = m.DeclaringSyntaxReferences[0];
        bool ok = false;
        if (ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax()) is { } body)
        {
            var returns = new List<bool>();
            new ChainFlow(this, m, body).Run(null, returns);
            ok = returns.Count > 0 && returns.All(x => x);
        }
        return _returnsBelow[m] = ok;
    }

    /// <summary>A type whose objects hold no reference: a number, a string, an array of numbers.</summary>
    bool ChainLeaf(ITypeSymbol t) =>
        t.SpecialType == SpecialType.System_String || PlainValueType(t) || t is IArrayTypeSymbol { ElementType: var e } && (e.SpecialType == SpecialType.System_String || PlainValueType(e));

    /// <summary>What a constructor leaves in the object it makes, per parameter: 0 nothing of it, 1 only what was
    /// read out of it (`from.F`), 2 the parameter itself. Null: something else may be in the object (another
    /// constructor runs, a field has an initializer that holds references, a store of anything but null, a
    /// parameter or a parameter's field), or `this` gets out.</summary>
    byte[]? CtorHolds(IMethodSymbol ctor)
    {
        ctor = ctor.OriginalDefinition;
        if (_ctorHolds.TryGetValue(ctor, out var known)) return known;
        _ctorHolds[ctor] = null;
        return _ctorHolds[ctor] = Compute();

        byte[]? Compute()
        {
            var t = ctor.ContainingType;
            if (t is not { TypeKind: TypeKind.Class, BaseType.SpecialType: SpecialType.System_Object } || !CtorsKeepThis(t) || HooksSeeAny(t.InstanceConstructors)) return null;
            if (ctor.DeclaringSyntaxReferences.Length != 1) return null;
            // (the fields' own initializers: none that holds a reference to anything)
            foreach (var m in t.GetMembers())
            {
                ITypeSymbol? ft = m is IFieldSymbol { IsStatic: false } f ? f.Type : m is IPropertySymbol { IsStatic: false } p && AutoPropertyField(p) != null ? p.Type : null;
                if (ft == null || ChainLeaf(ft)) continue;
                foreach (var sr in m.DeclaringSyntaxReferences)
                    if (MemberInitializer(sr) is { } init && init.ConstantValue is not { HasValue: true, Value: null }) return null;
            }
            var r = ctor.DeclaringSyntaxReferences[0];
            if (ModelFor(r.SyntaxTree).GetOperation(r.GetSyntax()) is not IConstructorBodyOperation body) return null;
            if (body.Initializer != null && body.Initializer.Descendants().OfType<IInvocationOperation>().Any(i => i.TargetMethod.ContainingType.SpecialType != SpecialType.System_Object)) return null;
            var held = new byte[ctor.Parameters.Length];
            foreach (var op in body.Descendants())
            {
                if (op is IAnonymousFunctionOperation or ILocalFunctionOperation) return null;
                // (a parameter is what the caller passed all the way)
                if (op is IParameterReferenceOperation pr && (ChainFlow.IsWrite(pr) || ChainFlow.IsRefUse(pr))) return null;
                ITypeSymbol? slotType = op switch
                {
                    IFieldReferenceOperation { Instance: IInstanceReferenceOperation } fr => fr.Field.Type,
                    IPropertyReferenceOperation { Instance: IInstanceReferenceOperation } pp => pp.Property.Type,
                    _ => null,
                };
                if (slotType == null || ChainLeaf(slotType)) continue;
                if (ChainFlow.IsRefUse(op)) return null;
                if (!ChainFlow.IsWrite(op)) continue;
                if (op.Parent is not ISimpleAssignmentOperation { IsRef: false } sa || !slotType.IsReferenceType) return null;
                if (sa.Value.ConstantValue is { HasValue: true, Value: null }) continue;
                var v = ChainFlow.Strip(sa.Value);
                int loads = 0;
                while (v is IFieldReferenceOperation { Instance: { } inst, Field.IsStatic: false }) { loads++; v = ChainFlow.Strip(inst); }
                if (v is not IParameterReferenceOperation { Parameter: { RefKind: RefKind.None } vp } || !SymbolEqualityComparer.Default.Equals(vp.ContainingSymbol.OriginalDefinition, ctor)) return null;
                held[vp.Ordinal] = Math.Max(held[vp.Ordinal], (byte)(loads > 0 ? 1 : 2));
            }
            return held;
        }
    }

    /// <summary>The facts of one method body, over its control flow graph.</summary>
    sealed class ChainFlow
    {
        readonly Driver D;
        readonly IMethodSymbol _method;
        readonly IOperation _body;
        readonly HashSet<ISymbol> _untracked = new(SymbolEqualityComparer.Default);
        static readonly object s_this = new(), s_none = new();

        public ChainFlow(Driver d, IMethodSymbol method, IOperation body) { D = d; _method = method; _body = body; }

        sealed class St
        {
            public readonly HashSet<(object, object)> Reach = [];    // (a, b): b's object is a's or one a's reached
            public readonly HashSet<(object, object)> Distinct = []; // both orders
            public readonly Dictionary<object, object> Fresh = [];   // a new object nothing else knows: what it was filled from (s_none: nothing)
            // (a fact about two variables says nothing when either is null: every use of one reads a field of the
            // container, and a null value stored closes nothing. So where two paths meet, a fact of one path stands
            // if on the other path one of its variables is null: `end = null; while (..) { if (end == null) end = cur; cur = cur.F; }`)
            public readonly HashSet<object> Null = [];
            public St Clone() { var s = new St(); s.Reach.UnionWith(Reach); s.Distinct.UnionWith(Distinct); s.Null.UnionWith(Null); foreach (var kv in Fresh) s.Fresh[kv.Key] = kv.Value; return s; }
            public void Meet(St o)
            {
                static void Pairs(HashSet<(object, object)> a, HashSet<object> an, HashSet<(object, object)> b, HashSet<object> bn)
                {
                    var keep = b.Where(p => !a.Contains(p) && (an.Contains(p.Item1) || an.Contains(p.Item2))).ToList();
                    a.RemoveWhere(p => !b.Contains(p) && !bn.Contains(p.Item1) && !bn.Contains(p.Item2));
                    a.UnionWith(keep);
                }
                Pairs(Reach, Null, o.Reach, o.Null); Pairs(Distinct, Null, o.Distinct, o.Null);
                Null.IntersectWith(o.Null);
                foreach (var k in Fresh.Keys.ToList()) if (!o.Fresh.TryGetValue(k, out var r) || !Equals(r, Fresh[k])) Fresh.Remove(k);
            }
            public bool Same(St o) => Reach.SetEquals(o.Reach) && Distinct.SetEquals(o.Distinct) && Null.SetEquals(o.Null) && Fresh.Count == o.Fresh.Count && Fresh.All(kv => o.Fresh.TryGetValue(kv.Key, out var r) && Equals(r, kv.Value));
            public void Kill(object v)
            {
                Null.Remove(v);
                Reach.RemoveWhere(p => Equals(p.Item1, v) || Equals(p.Item2, v));
                Distinct.RemoveWhere(p => Equals(p.Item1, v) || Equals(p.Item2, v));
                Fresh.Remove(v);
                foreach (var k in Fresh.Where(kv => Equals(kv.Value, v)).Select(kv => kv.Key).ToList()) Fresh.Remove(k);
            }
            public bool ReachEq(object a, object b) => Equals(a, b) || Reach.Contains((a, b));
        }

        internal static IOperation Strip(IOperation v)
        {
            while (true)
            {
                if (v is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsReference)) v = c.Operand;
                else if (v is IParenthesizedOperation p) v = p.Operand;
                else return v;
            }
        }
        static IOperation Top(IOperation op)
        {
            while (op.Parent is IParenthesizedOperation || op.Parent is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsReference)) op = op.Parent;
            return op;
        }
        /// <summary>The operation is what an assignment, an increment or a deconstruction writes.</summary>
        internal static bool IsWrite(IOperation op)
        {
            op = Top(op);
            return op.Parent switch
            {
                IAssignmentOperation a => a.Target == op,
                IIncrementOrDecrementOperation => true,
                ITupleOperation or IDeclarationExpressionOperation => true,
                _ => false,
            };
        }
        /// <summary>The operation is used as a place, not for its value: passed by reference, its address taken.</summary>
        internal static bool IsRefUse(IOperation op)
        {
            op = Top(op);
            return op.Parent switch
            {
                IArgumentOperation a => a.Parameter == null || a.Parameter.RefKind != RefKind.None,
                IAddressOfOperation => true,
                ISimpleAssignmentOperation { IsRef: true } => true,
                IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol.RefKind: not RefKind.None } } => true,
                IReturnOperation r => r.SemanticModel?.GetEnclosingSymbol(r.Syntax.SpanStart) is IMethodSymbol { RefKind: not RefKind.None },
                IConditionalOperation { IsRef: true } => true,
                ITupleOperation or IDeclarationExpressionOperation or IDeconstructionAssignmentOperation => true,
                _ => false,
            };
        }

        bool TrackedSymbol(ISymbol s)
        {
            if (_untracked.Contains(s)) return false;
            return s switch
            {
                ILocalSymbol l => l.RefKind == RefKind.None && !l.IsFixed && !l.IsUsing && l.Type.IsReferenceType && l.DeclaringSyntaxReferences.Length == 1 && l.DeclaringSyntaxReferences[0].GetSyntax() is VariableDeclaratorSyntax,
                IParameterSymbol p => p.Type.IsReferenceType && SymbolEqualityComparer.Default.Equals(p.ContainingSymbol.OriginalDefinition, _method),
                _ => false,
            };
        }

        /// <summary>The variable an expression names: `this`, a tracked local or parameter, a flow capture.</summary>
        object? VarOf(IOperation e)
        {
            switch (Strip(e))
            {
                case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } when _method.ContainingType.TypeKind == TypeKind.Class && !_method.IsStatic: return s_this;
                case ILocalReferenceOperation lr when TrackedSymbol(lr.Local): return lr.Local;
                case IParameterReferenceOperation pr when TrackedSymbol(pr.Parameter): return pr.Parameter;
                case IFlowCaptureReferenceOperation fc when fc.Type?.IsReferenceType == true: return fc.Id;
                default: return null;
            }
        }
        /// <summary>`u.F.G`: the variable and the number of field reads.</summary>
        (object? u, int loads) ChainOf(IOperation e)
        {
            int loads = 0;
            var v = Strip(e);
            while (v is IFieldReferenceOperation { Instance: { } inst, Field.IsStatic: false } fr && fr.Type?.IsReferenceType == true) { loads++; v = Strip(inst); }
            return (VarOf(v), loads);
        }

        void FindUntracked(IOperation op, bool nested)
        {
            if (op is IAnonymousFunctionOperation or ILocalFunctionOperation) nested = true;
            ISymbol? s = op switch { ILocalReferenceOperation lr => lr.Local, IParameterReferenceOperation pr => pr.Parameter, _ => null };
            if (s != null)
            {
                bool plainWrite = Top(op).Parent is ISimpleAssignmentOperation { IsRef: false } sa && sa.Target == Top(op);
                bool declared = op is ILocalReferenceOperation { IsDeclaration: true };
                if (nested || IsRefUse(op) || IsWrite(op) && !plainWrite || declared) _untracked.Add(s);
            }
            foreach (var c in op.ChildOperations) FindUntracked(c, nested);
        }

        public void Run(Dictionary<SyntaxNode, ChainProof>? proofs, List<bool>? returns)
        {
            ControlFlowGraph cfg;
            try
            {
                cfg = _body switch
                {
                    IMethodBodyOperation mb => ControlFlowGraph.Create(mb),
                    IConstructorBodyOperation cb => ControlFlowGraph.Create(cb),
                    IBlockOperation { Parent: null } b => ControlFlowGraph.Create(b),
                    _ => null!,
                };
            }
            catch (ArgumentException) { return; }
            if (cfg == null) return;
            // (a handler is entered from anywhere in its try: no edge says so)
            bool Handlers(ControlFlowRegion r) => r.Kind is not (ControlFlowRegionKind.Root or ControlFlowRegionKind.LocalLifetime) || r.NestedRegions.Any(Handlers);
            if (Handlers(cfg.Root)) return;
            FindUntracked(_body, false);
            var blocks = cfg.Blocks;
            var outs = new St?[blocks.Length];
            var work = new Queue<int>();
            work.Enqueue(0);
            var queued = new bool[blocks.Length];
            queued[0] = true;
            int steps = 0;
            St? In(BasicBlock b)
            {
                if (b.Kind == BasicBlockKind.Entry) return new St();
                St? s = null;
                foreach (var e in b.Predecessors)
                {
                    if (outs[e.Source.Ordinal] is not { } o) continue;
                    var t = o.Clone();
                    Refine(e, t);
                    if (s == null) s = t; else s.Meet(t);
                }
                return s;
            }
            while (work.Count > 0)
            {
                if (++steps > 20000) return;
                var b = blocks[work.Dequeue()];
                queued[b.Ordinal] = false;
                if (In(b) is not { } s) continue;
                Transfer(b, s, null, null);
                if (outs[b.Ordinal] is { } old && old.Same(s)) continue;
                outs[b.Ordinal] = s;
                foreach (var succ in new[] { b.FallThroughSuccessor?.Destination, b.ConditionalSuccessor?.Destination })
                    if (succ != null && !queued[succ.Ordinal]) { queued[succ.Ordinal] = true; work.Enqueue(succ.Ordinal); }
            }
            // (the facts are settled: what they say at each store and each return)
            foreach (var b in blocks)
                if (In(b) is { } s) Transfer(b, s, proofs, returns);
        }

        /// <summary>What a branch's condition says on the edge taken: `a != b`.</summary>
        void Refine(ControlFlowBranch e, St s)
        {
            var src = e.Source;
            if (src.ConditionKind == ControlFlowConditionKind.None || src.BranchValue == null) return;
            bool whenTrue = e == src.ConditionalSuccessor ? src.ConditionKind == ControlFlowConditionKind.WhenTrue : src.ConditionKind == ControlFlowConditionKind.WhenFalse;
            if (Strip(src.BranchValue) is IIsNullOperation isNull) { if (whenTrue && VarOf(isNull.Operand) is { } nv) s.Null.Add(nv); return; }
            if (Strip(src.BranchValue) is not IBinaryOperation { OperatorMethod: null, OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals } bin) return;
            bool equalOnEdge = (bin.OperatorKind == BinaryOperatorKind.Equals) == whenTrue;
            static bool IsNull(IOperation o) => o.ConstantValue is { HasValue: true, Value: null };
            if (IsNull(bin.LeftOperand) || IsNull(bin.RightOperand))
            {
                if (equalOnEdge && VarOf(IsNull(bin.LeftOperand) ? bin.RightOperand : bin.LeftOperand) is { } zv) s.Null.Add(zv);
                return;
            }
            if (VarOf(bin.LeftOperand) is not { } a || VarOf(bin.RightOperand) is not { } b || Equals(a, b)) return;
            if (equalOnEdge) return;
            s.Distinct.Add((a, b)); s.Distinct.Add((b, a));
        }

        void Transfer(BasicBlock b, St s, Dictionary<SyntaxNode, ChainProof>? proofs, List<bool>? returns)
        {
            foreach (var op in b.Operations) Visit(op, s, proofs);
            if (b.BranchValue != null) Visit(b.BranchValue, s, proofs);
            if (returns != null && b.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return && b.BranchValue != null)
            {
                // (a by-reference parameter's facts stand on what the callers pass: not for a summary)
                bool Own(object? v) => v != null && v is not IParameterSymbol { RefKind: not RefKind.None };
                var (u, _) = ChainOf(b.BranchValue);
                returns.Add(Own(u) && s.ReachEq(s_this, u!));
            }
        }

        void Visit(IOperation op, St s, Dictionary<SyntaxNode, ChainProof>? proofs)
        {
            if (op is IFlowAnonymousFunctionOperation or IAnonymousFunctionOperation or ILocalFunctionOperation) return;
            foreach (var c in op.ChildOperations) Visit(c, s, proofs);
            switch (op)
            {
                case ILocalReferenceOperation or IParameterReferenceOperation or IFlowCaptureReferenceOperation:
                    // (a new object's local used for anything but its fields: someone else may know the object now)
                    if (VarOf(op) is { } w && s.Fresh.ContainsKey(w) && !FreshUseStays(op)) s.Fresh.Remove(w);
                    break;
                case ISimpleAssignmentOperation { IsRef: false } sa:
                    if (Strip(sa.Target) is IFieldReferenceOperation { Instance: { } inst, Field.IsStatic: false } fr)
                    {
                        if (proofs != null && Decide(inst, sa.Value, s) is { } proof) proofs[sa.Syntax] = proof;
                        // (the value is known to the container now; a new object given a reference holds more than
                        // its constructor put there)
                        if (VarOf(sa.Value) is { } pv) s.Fresh.Remove(pv);
                        if (VarOf(inst) is { } cv && s.Fresh.ContainsKey(cv) && !D.ChainLeaf(fr.Field.Type)) s.Fresh.Remove(cv);
                    }
                    else if (Strip(sa.Target) is ILocalReferenceOperation or IParameterReferenceOperation)
                    {
                        ISymbol sym = Strip(sa.Target) is ILocalReferenceOperation lr ? lr.Local : ((IParameterReferenceOperation)Strip(sa.Target)).Parameter;
                        Assign(sym, TrackedSymbol(sym), sa.Value, s);
                    }
                    break;
                case IFlowCaptureOperation fc:
                    Assign(fc.Id, fc.Value.Type?.IsReferenceType == true, fc.Value, s);
                    break;
            }
        }

        /// <summary>A use of a new object's local that lets nobody else know the object: a field of it read, a field
        /// that holds no reference written, the value of the store that is then judged, a comparison.</summary>
        static bool FreshUseStays(IOperation r)
        {
            var top = Top(r);
            switch (top.Parent)
            {
                case IFieldReferenceOperation f when f.Instance == top: return true;
                case ISimpleAssignmentOperation { IsRef: false } sa: return sa.Target == top || sa.Value == top && Strip(sa.Target) is IFieldReferenceOperation { Field.IsStatic: false };
                case IBinaryOperation { OperatorMethod: null, OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals }: return true;
                case IIsNullOperation: return true;
                default: return false;
            }
        }

        void Assign(object v, bool tracked, IOperation value, St s)
        {
            var upper = new List<object>(); var lower = new List<object>(); var distinct = new List<object>();
            object? fresh = null;
            bool isNull = value.ConstantValue is { HasValue: true, Value: null };
            var stripped = Strip(value);
            var (u, loads) = ChainOf(value);
            if (tracked)
            {
                if (u != null && loads == 0)
                {
                    if (Equals(u, v)) return;
                    isNull = s.Null.Contains(u);
                    foreach (var p in s.Reach) { if (Equals(p.Item2, u)) upper.Add(p.Item1); if (Equals(p.Item1, u)) lower.Add(p.Item2); }
                    upper.Add(u); lower.Add(u);
                    foreach (var p in s.Distinct) if (Equals(p.Item1, u)) distinct.Add(p.Item2);
                }
                else if (u != null)
                {
                    foreach (var p in s.Reach) if (Equals(p.Item2, u)) upper.Add(p.Item1);
                    upper.Add(u);
                }
                else if (stripped is IInvocationOperation { Instance: { } recv } inv && VarOf(recv) is { } ru && D.ReturnsBelowReceiver(inv.TargetMethod))
                {
                    foreach (var p in s.Reach) if (Equals(p.Item2, ru)) upper.Add(p.Item1);
                    upper.Add(ru);
                }
                else if (stripped is IObjectCreationOperation oc) fresh = FreshRoot(oc);
            }
            s.Kill(v);
            if (!tracked) return;
            foreach (var c in upper) if (!Equals(c, v)) s.Reach.Add((c, v));
            foreach (var c in lower) if (!Equals(c, v)) s.Reach.Add((v, c));
            foreach (var c in distinct) if (!Equals(c, v)) { s.Distinct.Add((v, c)); s.Distinct.Add((c, v)); }
            if (fresh != null && !Equals(fresh, v)) s.Fresh[v] = fresh;
            if (isNull) s.Null.Add(v);
        }

        /// <summary>`new C(args)`: the variable everything in the new object was read out of, s_none when it holds
        /// nothing, null when that is not known.</summary>
        object? FreshRoot(IObjectCreationOperation oc)
        {
            if (oc.Constructor == null || oc.Initializer != null || oc.Type is not INamedTypeSymbol { TypeKind: TypeKind.Class }) return null;
            if (D.CtorHolds(oc.Constructor) is not { } held) return null;
            object? root = null;
            foreach (var a in oc.Arguments)
            {
                if (a.Parameter == null || a.Parameter.Ordinal >= held.Length) return null;
                int h = held[a.Parameter.Ordinal];
                if (h == 0) continue;
                var (u, loads) = ChainOf(a.Value);
                if (u == null || h == 2 && loads == 0) return null;
                if (root != null && !Equals(root, u)) return null;
                root = u;
            }
            return root ?? s_none;
        }

        ChainProof? Decide(IOperation instance, IOperation value, St s)
        {
            if (VarOf(instance) is not { } x) return null;
            var involved = new List<object> { x };
            bool ok = false;
            var stripped = Strip(value);
            var (u, loads) = ChainOf(value);
            object? root = null;
            if (stripped is IObjectCreationOperation oc) root = FreshRoot(oc);
            else if (u != null && loads == 0 && s.Fresh.TryGetValue(u, out var fr)) { root = fr; involved.Add(u); }
            if (root != null)
            {
                ok = ReferenceEquals(root, s_none) || s.ReachEq(x, root);
                if (!ReferenceEquals(root, s_none)) involved.Add(root);
            }
            else if (u != null && loads == 0)
            {
                ok = s.Reach.Contains((x, u)) && s.Distinct.Contains((x, u));
                // (the test's deliberate break: any plain local's value)
                if (s_chainBreak && u is ILocalSymbol) ok = true;
                involved.Add(u);
            }
            else if (u != null)
            {
                ok = s.ReachEq(x, u);
                involved.Add(u);
            }
            if (!ok) return null;
            return new ChainProof([.. involved.OfType<IParameterSymbol>().Where(p => p.RefKind != RefKind.None).Select(p => p.Type)]);
        }
    }
}
