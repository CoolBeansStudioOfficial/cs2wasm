#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Types;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

/// <summary>An IR body to a WASM function of the existing back end: the regions and blocks structured into WASM's
/// blocks, loops and ifs (Ramsey's "Beyond Relooper" over each region's graph, with a dispatch loop where a region's
/// flow is irreducible), try regions as try_table, and every operation lowered with naive counting
/// (Ir/Ownership.cs): each value in a local of its own, zero when it holds no count.</summary>
sealed class FunctionLowering
{
    readonly IlCompiler C;
    readonly Body _body;
    readonly Ownership.Info _own;
    readonly WasmFunction _fn;
    readonly Emitter E;
    readonly Dictionary<Node, int> _local = new();
    readonly int[] _varLocal;
    readonly Dictionary<Region, (int obj, int exn)> _handlerLocals = new();
    readonly Dictionary<Region, int> _leaveLocal = new();
    readonly Dictionary<Region, Label> _filterEnd = new();
    readonly Dictionary<Region, int> _filterResult = new();
    readonly Dictionary<Region, Label> _finallyEnd = new();

    public FunctionLowering(IlCompiler c, Body body, Ownership.Info own, WasmFunction fn)
    {
        C = c; _body = body; _own = own; _fn = fn; E = new Emitter(fn);
        foreach (var a in body.Args) _local[a] = (int)a.I;
        _varLocal = body.Vars.Select(v => v.InFrame ? -1 : E.AddLocal(ValOf(v.Kind))).ToArray();
        // the frame: the variables whose address is taken, on the shadow stack
        foreach (var v in body.Vars.Where(v => v.InFrame))
        {
            int size = Math.Max(4, Kinds.SizeOf(v.Type)), align = Math.Min(8, size);
            _frameSize = (_frameSize + align - 1) / align * align;
            v.Offset = _frameSize;
            _frameSize += size;
        }
        _frameSize = (_frameSize + 15) & ~15;
        if (_frameSize > 0) _fb = E.AddLocal(ValType.I32);
    }

    readonly int _frameSize, _fb = -1;

    /// <summary>A variable's value on the stack.</summary>
    void VarGet(int i)
    {
        var v = _body.Vars[i];
        if (!v.InFrame) { E.Get(_varLocal[i]); return; }
        E.Get(_fb); Load(v.Type, v.Offset);
    }

    /// <summary>The value on the stack into a variable (through a scratch local when it is in the frame).</summary>
    void VarSet(int i)
    {
        var v = _body.Vars[i];
        if (!v.InFrame) { E.Set(_varLocal[i]); return; }
        int t = Temp(ValOf(v.Kind));
        E.Set(t); E.Get(_fb); E.Get(t); Store(v.Type, v.Offset);
    }

    /// <summary>The frame given back (before a return, and when an exception leaves).</summary>
    void PopFrame() { if (_fb >= 0) { E.Get(_fb); E.I32(_frameSize); E.Op(WOp.I32Add); E.GSet(C.StackPointer); } }

    public static ValType ValOf(VK k) => k switch { VK.I64 => ValType.I64, VK.F32 => ValType.F32, VK.F64 => ValType.F64, _ => ValType.I32 };

    int L(Node n)
    {
        if (_local.TryGetValue(n, out int l)) return l;
        l = E.AddLocal(ValOf(n.Kind));
        _local[n] = l;
        return l;
    }

    void Get(Node n)
    {
        if (n.Op == Opc.Const) { EmitConst(n); return; }
        E.Get(L(n));
    }

    void EmitConst(Node n)
    {
        switch (n.Kind)
        {
            case VK.I64: E.I64(n.I); break;
            case VK.F32: E.F32((float)n.D); break;
            case VK.F64: E.F64(n.D); break;
            default: E.I32((int)n.I); break;
        }
    }

    void Set(Node n) => E.Set(L(n));

    // ------------------------------------------------------------------ the function

    public void Run()
    {
        bool hasRefs = _own.RefValues.Count > 0 || _body.Vars.Any(v => v.Kind == VK.Ref) || _fb >= 0;
        if (_fb >= 0)
        {
            E.GGet(C.StackPointer); E.I32(_frameSize); E.Op(WOp.I32Sub); E.Tee(_fb); E.GSet(C.StackPointer);
            E.Get(_fb); E.I32(0); E.I32(_frameSize); E.Emit(WOp.MemoryFill);
        }
        // (pre-create the locals of the reference values: the cleanup below names them all)
        foreach (var v in _own.RefValues) if (v.Op != Opc.Const) L(v);
        // (the shape the back end's passes know: block $ok, block $c (exnref), try_table, the body, br $ok)
        Label? cleanup = null, ok = null;
        int exn = -1;
        if (hasRefs)
        {
            exn = E.AddLocal(ValType.ExnRef);
            ok = E.Block();
            cleanup = E.Block(ValType.ExnRef);
            E.TryTable(null, (3, null, cleanup));
        }
        var root = new Level(_body.Root, _body.Blocks[0]);
        EmitLevel(root, null);
        if (hasRefs)
        {
            E.End();
            E.Br(ok!);
            E.End();
            // an exception leaves the function: every count it holds is released
            E.Set(exn);
            foreach (var v in _own.RefValues) if (_local.TryGetValue(v, out int l)) { E.Get(l); E.Call(C.Rt.Release); }
            for (int i = 0; i < _body.Vars.Count; i++) if (_body.Vars[i].Kind == VK.Ref) { VarGet(i); E.Call(C.Rt.Release); }
            PopFrame();
            E.Get(exn); E.Op(WOp.ThrowRef);
            E.End();
        }
        E.Op(WOp.Unreachable);
    }

    // ------------------------------------------------------------------ structuring

    /// <summary>What a branch can target while a level is emitted: a loop's header, a block's follower.</summary>
    sealed class Ctx
    {
        public readonly Dictionary<SNode, Label> Loops = new(), Follows = new();
        public required Level Level;
        /// <summary>A branch out of the level: (leave local, its label, the exits' order), or null at the root.</summary>
        public (int local, Label label, List<Block> exits)? Exit;
        /// <summary>The dispatch loop of an irreducible level: its label local, its loop, each node's number.</summary>
        public (int local, Label top, Dictionary<SNode, int> index)? Dispatch;
    }

    void EmitLevel(Level lv, (int, Label, List<Block>)? exit)
    {
        var ctx = new Ctx { Level = lv, Exit = exit };
        if (lv.Irreducible) EmitDispatch(lv, ctx);
        else DoTree(lv.Entry, ctx);
    }

    void DoTree(SNode x, Ctx ctx)
    {
        var merges = x.DomChildren.Where(c => c.Merge).ToList(); // highest reverse postorder first
        if (x.LoopHeader)
        {
            var l = E.Loop();
            ctx.Loops[x] = l;
            NodeWithin(x, merges, 0, ctx);
            ctx.Loops.Remove(x);
            E.End();
        }
        else NodeWithin(x, merges, 0, ctx);
    }

    void NodeWithin(SNode x, List<SNode> ys, int i, Ctx ctx)
    {
        if (i == ys.Count) { EmitNode(x, ctx); return; }
        var y = ys[i];
        var b = E.Block();
        ctx.Follows[y] = b;
        NodeWithin(x, ys, i + 1, ctx);
        ctx.Follows.Remove(y);
        E.End();
        DoTree(y, ctx);
    }

    void DoBranch(SNode from, SNode to, Ctx ctx)
    {
        if (to is XNode x)
        {
            var (local, label, exits) = ctx.Exit ?? throw new InvalidOperationException("a branch out of the method's body");
            E.I32(exits.IndexOf(x.Target)); E.Set(local); E.Br(label);
            return;
        }
        if (ctx.Dispatch != null) { E.I32(ctx.Dispatch.Value.index[to]); E.Set(ctx.Dispatch.Value.local); E.Br(ctx.Dispatch.Value.top); return; }
        if (to.Rpo <= from.Rpo) { E.Br(ctx.Loops[to]); return; }
        if (to.Merge) { E.Br(ctx.Follows[to]); return; }
        DoTree(to, ctx);
    }

    /// <summary>The fallback for irreducible flow: every node of the level in a loop that dispatches on a label local.</summary>
    void EmitDispatch(Level lv, Ctx ctx)
    {
        int lbl = E.AddLocal(ValType.I32);
        var index = new Dictionary<SNode, int>();
        for (int i = 0; i < lv.Nodes.Count; i++) index[lv.Nodes[i]] = i;
        E.I32(0); E.Set(lbl);
        var top = E.Loop();
        ctx.Dispatch = (lbl, top, index);
        var blocks = new Label[lv.Nodes.Count];
        for (int i = lv.Nodes.Count - 1; i >= 0; i--) blocks[i] = E.Block();
        E.Get(lbl); E.BrTable(blocks, blocks[^1]);
        for (int i = 0; i < lv.Nodes.Count; i++)
        {
            E.End();
            EmitNode(lv.Nodes[i], ctx);
            E.Op(WOp.Unreachable);
        }
        E.End();
        ctx.Dispatch = null;
    }

    void EmitNode(SNode x, Ctx ctx)
    {
        if (x is TNode t) { EmitTry(t, ctx); return; }
        var b = ((BNode)x).B;
        foreach (var n in b.Code)
        {
            if (n.IsTerminator) { EmitTerminator(x, b, n, ctx); return; }
            EmitOp(n);
        }
        throw new InvalidOperationException($"{b} has no terminator");
    }

    SNode Succ(Ctx ctx, Block target) => ctx.Level.Map(target);

    void EmitTerminator(SNode x, Block b, Node n, Ctx ctx)
    {
        PhiCopies(b);
        switch (n.Op)
        {
            case Opc.Br: case Opc.Leave:
                DoBranch(x, Succ(ctx, b.Succs[0]), ctx); return;
            case Opc.BrIf:
                Get(n.A[0]);
                E.If();
                DoBranch(x, Succ(ctx, b.Succs[0]), ctx);
                E.Else();
                DoBranch(x, Succ(ctx, b.Succs[1]), ctx);
                E.End();
                return;
            case Opc.Switch:
            {
                var targets = b.Succs.Select(s => Succ(ctx, s)).ToList();
                var distinct = targets.Distinct().ToList();
                var labels = new Label[distinct.Count];
                for (int i = distinct.Count - 1; i >= 0; i--) labels[i] = E.Block();
                Get(n.A[0]);
                E.BrTable(targets.Take(targets.Count - 1).Select(t => labels[distinct.IndexOf(t)]).ToArray(), labels[distinct.IndexOf(targets[^1])]);
                for (int i = 0; i < distinct.Count; i++) { E.End(); DoBranch(x, distinct[i], ctx); }
                return;
            }
            case Opc.Ret:
                if (n.A.Length > 0) { Get(n.A[0]); if (n.A[0].Kind == VK.Ref && (n.Moves & 1) == 0) E.Call(C.Rt.Retain); if ((n.Moves & 1) != 0) Clear(n.A[0]); }
                ReleaseVars();
                PopFrame();
                E.Op(WOp.Return);
                return;
            case Opc.Throw:
                Get(n.A[0]);
                if ((n.Moves & 1) != 0) Clear(n.A[0]);
                E.Emit(WOp.Throw, 0, 0, C.ExceptionTag);
                return;
            case Opc.Rethrow:
            {
                var h = Enclosing(b.Region, RegionKind.Catch) ?? throw new InvalidProgramException("rethrow outside a catch");
                E.Get(_handlerLocals[h].exn); E.Op(WOp.ThrowRef);
                return;
            }
            case Opc.EndFinally:
            {
                var r = EnclosingAny(b.Region, RegionKind.Finally, RegionKind.Fault) ?? throw new InvalidProgramException("endfinally outside a finally");
                E.Br(_finallyEnd[r]);
                return;
            }
            case Opc.EndFilter:
            {
                var r = Enclosing(b.Region, RegionKind.Filter) ?? throw new InvalidProgramException("endfilter outside a filter");
                Get(n.A[0]); E.Set(_filterResult[r]); E.Br(_filterEnd[r]);
                return;
            }
            case Opc.Unreachable: E.Op(WOp.Unreachable); return;
        }
        throw new NotSupportedException("terminator " + n.Op);
    }

    static Region? Enclosing(Region r, RegionKind k) { for (var x = r; x != null; x = x.Parent) if (x.Kind == k) return x; return null; }
    static Region? EnclosingAny(Region r, RegionKind a, RegionKind b) { for (var x = r; x != null; x = x.Parent) if (x.Kind == a || x.Kind == b) return x; return null; }

    /// <summary>The phis of the one successor get their values (a parallel copy: all are read before any is set).</summary>
    void PhiCopies(Block b)
    {
        if (b.Succs.Count == 0 || b.Succs.Distinct().Count() != 1) return;
        var s = b.Succs[0];
        if (s.Phis.Count == 0) return;
        int pi = s.Preds.IndexOf(b);
        foreach (var phi in s.Phis)
        {
            var v = phi.A[pi];
            Get(v);
            if (v.Kind == VK.Ref && !_own.PhiMoves.Contains((phi, pi))) E.Call(C.Rt.Retain);
        }
        foreach (var phi in s.Phis) if (_own.PhiMoves.Contains((phi, pi))) Clear(phi.A[pi]);
        for (int i = s.Phis.Count - 1; i >= 0; i--) Set(s.Phis[i]);
    }

    void Clear(Node v) { if (v.Kind == VK.Ref && v.Op is not (Opc.Const or Opc.LdNull or Opc.LdStr)) { E.I32(0); E.Set(L(v)); } }

    void ReleaseVars()
    {
        for (int i = 0; i < _body.Vars.Count; i++)
            if (_body.Vars[i].Kind == VK.Ref) { VarGet(i); E.Call(C.Rt.Release); E.I32(0); VarSet(i); }
    }

    // ------------------------------------------------------------------ try regions

    void EmitTry(TNode t, Ctx ctx)
    {
        var tr = t.Try;
        int lv = E.AddLocal(ValType.I32);
        _leaveLocal[tr] = lv;
        var exits = t.Exits;
        var fin = tr.Handlers.FirstOrDefault(h => h.Kind is RegionKind.Finally or RegionKind.Fault);
        if (fin != null)
        {
            if (tr.Handlers.Count != 1) throw new InvalidProgramException("a try with a finally and other handlers");
            int exn = E.AddLocal(ValType.ExnRef);
            var after = E.Block();
            var caught = E.Block(ValType.ExnRef);
            E.TryTable(null, (3, null, caught));
            EmitLevel(new Level(tr, tr.Entry), (lv, after, exits));
            E.End();
            E.Br(after);
            E.End();
            E.Set(exn); E.I32(-1); E.Set(lv);
            RestoreFrame();
            E.End();
            // the finally (a fault only when unwinding)
            if (fin.Kind == RegionKind.Fault) { E.Get(lv); E.I32(-1); E.Op(WOp.I32Eq); E.If(); }
            var fend = E.Block();
            _finallyEnd[fin] = fend;
            EmitLevel(new Level(fin, fin.Entry), null);
            E.End();
            if (fin.Kind == RegionKind.Fault) E.End();
            E.Get(lv); E.I32(-1); E.Op(WOp.I32Eq); E.If(); E.Get(exn); E.Op(WOp.ThrowRef); E.End();
        }
        else
        {
            int obj = E.AddLocal(ValType.I32), exn = E.AddLocal(ValType.ExnRef);
            var done = E.Block();
            var caught = E.Block(new FuncType([], [ValType.I32, ValType.ExnRef]));
            E.TryTable(null, (1, C.ExceptionTag, caught));
            EmitLevel(new Level(tr, tr.Entry), (lv, done, exits));
            E.End();
            E.Br(done);
            E.End();
            E.Set(exn); E.Set(obj);
            RestoreFrame();
            foreach (var h in tr.Handlers)
            {
                _handlerLocals[h] = (obj, exn);
                bool open = false;
                if (h.Filter != null)
                {
                    // (one pass: the filter runs here, after the inner finally blocks have run)
                    _handlerLocals[h.Filter] = (obj, exn);
                    int fr = E.AddLocal(ValType.I32);
                    _filterResult[h.Filter] = fr;
                    var fe = E.Block();
                    _filterEnd[h.Filter] = fe;
                    EmitLevel(new Level(h.Filter, h.Filter.Entry), null);
                    E.End();
                    E.Get(fr); E.If(); open = true;
                }
                else if (h.CatchType != null && !h.CatchType.IsObject)
                {
                    E.Get(obj); E.Call(C.IsInstFn(h.CatchType)); E.If(); open = true;
                }
                EmitLevel(new Level(h, h.Entry), (lv, done, exits));
                if (open) E.End();
                else break;
            }
            E.Get(exn); E.Op(WOp.ThrowRef);
            E.End();
        }
        // where the leaves go
        if (exits.Count == 0) { E.Op(WOp.Unreachable); return; }
        if (exits.Count == 1) { DoBranch(t, Succ(ctx, exits[0]), ctx); return; }
        var targets = exits.Select(x => Succ(ctx, x)).ToList();
        var labels = new Label[exits.Count];
        for (int i = exits.Count - 1; i >= 0; i--) labels[i] = E.Block();
        E.Get(lv);
        E.BrTable(labels.Take(labels.Length - 1).ToArray(), labels[^1]);
        for (int i = 0; i < exits.Count; i++) { E.End(); DoBranch(t, targets[i], ctx); }
    }

    /// <summary>After a catch, the stack pointer is this frame's again (the callees' frames the exception left are gone).</summary>
    void RestoreFrame() { if (_fb >= 0) { E.Get(_fb); E.GSet(C.StackPointer); } }

    // ------------------------------------------------------------------ operations

    void EmitOp(Node n)
    {
        switch (n.Op)
        {
            case Opc.Arg: case Opc.Phi: return;
            case Opc.Const: return; // (materialised where used)
            case Opc.LdNull: E.I32(0); Set(n); return;
            case Opc.LdStr: E.I32Addr(C.StringLiteral((string)n.X!)); Set(n); return;
            case Opc.Retain: Get(n.A[0]); E.Call(C.Rt.Retain); E.Op(WOp.Drop); return;
            case Opc.Release:
                if (n.A[0].Op is Opc.LdStr or Opc.LdNull) return;
                Get(n.A[0]); E.Call(C.Rt.Release); E.I32(0); E.Set(L(n.A[0])); return;
            case Opc.CatchVal:
            {
                var h = _body.Regions.First(r => r.Entry == n.Block && (r.Kind is RegionKind.Catch or RegionKind.Filter) && _handlerLocals.ContainsKey(r));
                E.Get(_handlerLocals[h].obj);
                // (a filter's value is its own count: the exception in flight keeps one)
                if (h.Kind == RegionKind.Filter) E.Call(C.Rt.Retain);
                Set(n);
                return;
            }
            case Opc.LocA: E.Get(_fb); E.I32(_body.Vars[(int)n.I].Offset); E.Op(WOp.I32Add); Set(n); return;
            case Opc.VarLd:
                VarGet((int)n.I);
                if (n.Kind == VK.Ref) E.Call(C.Rt.Retain);
                Set(n);
                return;
            case Opc.VarSt:
            {
                int vi = (int)n.I;
                if (n.A[0].Kind == VK.Ref)
                {
                    int old = E.AddLocal(ValType.I32);
                    VarGet(vi); E.Set(old);
                    Get(n.A[0]); if ((n.Moves & 1) == 0) E.Call(C.Rt.Retain); VarSet(vi);
                    E.Get(old); E.Call(C.Rt.Release);
                    MovedOut(n);
                }
                else { Get(n.A[0]); VarSet(vi); }
                return;
            }
            case Opc.Bin: Binary(n); Set(n); return;
            case Opc.Un:
                if ((UnOp)n.Sub == UnOp.Neg)
                {
                    switch (n.Kind)
                    {
                        case VK.I64: E.I64(0); Get(n.A[0]); E.Op(WOp.I64Sub); break;
                        case VK.F32: Get(n.A[0]); E.Op(WOp.F32Neg); break;
                        case VK.F64: Get(n.A[0]); E.Op(WOp.F64Neg); break;
                        default: E.I32(0); Get(n.A[0]); E.Op(WOp.I32Sub); break;
                    }
                }
                else if (n.Kind == VK.I64) { Get(n.A[0]); E.I64(-1); E.Op(WOp.I64Xor); }
                else { Get(n.A[0]); E.I32(-1); E.Op(WOp.I32Xor); }
                Set(n);
                return;
            case Opc.Cmp: Compare(n); Set(n); return;
            case Opc.Conv: Convert(n); Set(n); return;
            case Opc.Call: Call(n); return;
            case Opc.NewObj: NewObj(n); return;
            case Opc.NewArr:
            {
                var at = (ArrayType)n.Type!;
                C.Constructed(at);
                Get(n.A[0]); E.I32(Kinds.SizeOf(at.ElementType)); E.Tid(C.TypeId(at)); E.Call(C.Rt.AllocArray);
                Set(n);
                return;
            }
            case Opc.LdLen: Get(n.A[0]); E.Call(C.Rt.NonNull); E.Load(WOp.I32Load, 12); Set(n); return;
            case Opc.LdElem:
            {
                var et = (TypeDesc)n.X!;
                Get(n.A[0]); Get(n.A[1]); E.Call(C.Rt.ElemAddr(Kinds.SizeOf(et)));
                Load(et, 0);
                if (n.Kind == VK.Ref) E.Call(C.Rt.Retain);
                Set(n);
                return;
            }
            case Opc.LdElemA:
                Get(n.A[0]); Get(n.A[1]); E.Call(C.Rt.ElemAddr(Kinds.SizeOf((TypeDesc)n.X!))); Set(n);
                return;
            case Opc.StElem:
            {
                var et = (TypeDesc)n.X!;
                int addr = Temp(ValType.I32);
                Get(n.A[0]); Get(n.A[1]); E.Call(C.Rt.ElemAddr(Kinds.SizeOf(et))); E.Set(addr);
                StoreAt(addr, 0, n.A[2], et, (n.Moves & 4) != 0);
                MovedOut(n);
                return;
            }
            case Opc.LdFld:
            {
                var f = (FieldDesc)n.X!;
                Get(n.A[0]);
                if (n.A[0].Kind == VK.Ref) E.Call(C.Rt.NonNull);
                Load(f.FieldType, IlContext.FieldOffset(f));
                if (n.Kind == VK.Ref) E.Call(C.Rt.Retain);
                Set(n);
                return;
            }
            case Opc.StFld:
            {
                var f = (FieldDesc)n.X!;
                int addr = Temp(ValType.I32);
                Get(n.A[0]);
                if (n.A[0].Kind == VK.Ref) E.Call(C.Rt.NonNull);
                E.Set(addr);
                StoreAt(addr, IlContext.FieldOffset(f), n.A[1], f.FieldType, (n.Moves & 2) != 0);
                MovedOut(n);
                return;
            }
            case Opc.LdFldA:
            {
                var f = (FieldDesc)n.X!;
                Get(n.A[0]);
                if (n.A[0].Kind == VK.Ref) E.Call(C.Rt.NonNull);
                E.I32(IlContext.FieldOffset(f)); E.Op(WOp.I32Add);
                Set(n);
                return;
            }
            case Opc.LdSFld:
            {
                var f = (FieldDesc)n.X!;
                C.CctorCheck(E, f.OwningType);
                E.I32Addr(C.StaticAddress(f));
                Load(f.FieldType, 0);
                if (n.Kind == VK.Ref) E.Call(C.Rt.Retain);
                Set(n);
                return;
            }
            case Opc.StSFld:
            {
                var f = (FieldDesc)n.X!;
                C.CctorCheck(E, f.OwningType);
                int addr = Temp(ValType.I32);
                E.I32Addr(C.StaticAddress(f)); E.Set(addr);
                StoreAt(addr, 0, n.A[0], f.FieldType, (n.Moves & 1) != 0);
                MovedOut(n);
                return;
            }
            case Opc.LdSFldA:
            {
                var f = (FieldDesc)n.X!;
                C.CctorCheck(E, f.OwningType);
                E.I32Addr(C.StaticAddress(f));
                Set(n);
                return;
            }
            case Opc.LdInd:
                Get(n.A[0]);
                Load(n.Type!, 0);
                if (n.Kind == VK.Ref) E.Call(C.Rt.Retain);
                Set(n);
                return;
            case Opc.StInd:
            {
                int addr = Temp(ValType.I32);
                Get(n.A[0]); E.Set(addr);
                StoreAt(addr, 0, n.A[1], n.Type!, (n.Moves & 2) != 0);
                MovedOut(n);
                return;
            }
            case Opc.CastClass:
                Get(n.A[0]); E.Call(C.CastFn((TypeDesc)n.X!)); E.Call(C.Rt.Retain); Set(n); return;
            case Opc.IsInst:
                Get(n.A[0]); E.Call(C.IsInstFn((TypeDesc)n.X!)); E.Call(C.Rt.Retain); Set(n); return;
            case Opc.Box:
            {
                var t = (TypeDesc)n.X!;
                C.Constructed(t);
                E.I32(IlContext.ObjectHeader + Math.Max(4, Kinds.SizeOf(t))); E.Tid(C.TypeId(t)); E.Call(C.Rt.AllocObject);
                Set(n);
                E.Get(L(n)); Get(n.A[0]); Store(t, IlContext.ObjectHeader);
                return;
            }
            case Opc.UnboxAny:
            {
                var t = (TypeDesc)n.X!;
                Get(n.A[0]); E.Call(C.UnboxFn(t));
                Load(t, IlContext.ObjectHeader);
                Set(n);
                return;
            }
        }
        throw new NotSupportedException($"{_body.Method}: IR {n.Op} is not lowered yet");
    }

    readonly Dictionary<ValType, int> _temps = new();
    /// <summary>A scratch local, used only within one operation.</summary>
    int Temp(ValType t) => _temps.TryGetValue(t, out int l) ? l : _temps[t] = E.AddLocal(t);

    void MovedOut(Node n)
    {
        for (int i = 0; i < n.A.Length; i++) if ((n.Moves & (1u << i)) != 0) Clear(n.A[i]);
    }

    void Load(TypeDesc t, int offset)
    {
        switch (t.Category)
        {
            case TypeFlags.Boolean: case TypeFlags.Byte: E.Load(WOp.I32Load8U, offset); break;
            case TypeFlags.SByte: E.Load(WOp.I32Load8S, offset); break;
            case TypeFlags.Char: case TypeFlags.UInt16: E.Load(WOp.I32Load16U, offset); break;
            case TypeFlags.Int16: E.Load(WOp.I32Load16S, offset); break;
            case TypeFlags.Int64: case TypeFlags.UInt64: E.Load(WOp.I64Load, offset); break;
            case TypeFlags.Single: E.Load(WOp.F32Load, offset); break;
            case TypeFlags.Double: E.Load(WOp.F64Load, offset); break;
            case TypeFlags.Enum: Load(t.UnderlyingType, offset); break;
            default: E.Load(WOp.I32Load, offset); break;
        }
    }

    void Store(TypeDesc t, int offset)
    {
        switch (t.Category)
        {
            case TypeFlags.Boolean: case TypeFlags.Byte: case TypeFlags.SByte: E.Store(WOp.I32Store8, offset); break;
            case TypeFlags.Char: case TypeFlags.UInt16: case TypeFlags.Int16: E.Store(WOp.I32Store16, offset); break;
            case TypeFlags.Int64: case TypeFlags.UInt64: E.Store(WOp.I64Store, offset); break;
            case TypeFlags.Single: E.Store(WOp.F32Store, offset); break;
            case TypeFlags.Double: E.Store(WOp.F64Store, offset); break;
            case TypeFlags.Enum: Store(t.UnderlyingType, offset); break;
            default: E.Store(WOp.I32Store, offset); break;
        }
    }

    /// <summary>A store at addr+offset; a reference's old value is released after the new one is in place.</summary>
    void StoreAt(int addr, int offset, Node v, TypeDesc t, bool moved)
    {
        if (v.Kind == VK.Ref)
        {
            int old = E.AddLocal(ValType.I32);
            E.Get(addr); E.Load(WOp.I32Load, offset); E.Set(old);
            E.Get(addr); Get(v); if (!moved) E.Call(C.Rt.Retain); E.Store(WOp.I32Store, offset);
            E.Get(old); E.Call(C.Rt.Release);
        }
        else { E.Get(addr); Get(v); Store(t, offset); }
    }

    void Binary(Node n)
    {
        var op = (BinOp)n.Sub;
        var a = n.A[0]; var b = n.A[1];
        bool i64 = n.Kind == VK.I64;
        if (n.Kind is VK.F32 or VK.F64)
        {
            if (op == BinOp.Rem) { Get(a); Get(b); E.Call(C.Rt.FRem(n.Kind == VK.F64)); return; }
            bool d = n.Kind == VK.F64;
            Get(a); Get(b);
            E.Op(op switch
            {
                BinOp.Add => d ? WOp.F64Add : WOp.F32Add, BinOp.Sub => d ? WOp.F64Sub : WOp.F32Sub,
                BinOp.Mul => d ? WOp.F64Mul : WOp.F32Mul, BinOp.Div => d ? WOp.F64Div : WOp.F32Div,
                _ => throw new NotSupportedException("float " + op),
            });
            return;
        }
        if (n.Ovf || op is BinOp.Div or BinOp.DivUn or BinOp.Rem or BinOp.RemUn)
        {
            Get(a); Get(b);
            E.Call(C.Rt.Checked(op, i64, n.Ovf, n.Unsigned));
            return;
        }
        Get(a);
        Get(b);
        if (i64 && op is BinOp.Shl or BinOp.Shr or BinOp.ShrUn) E.Op(WOp.I64ExtendI32U);
        E.Op(op switch
        {
            BinOp.Add => i64 ? WOp.I64Add : WOp.I32Add, BinOp.Sub => i64 ? WOp.I64Sub : WOp.I32Sub,
            BinOp.Mul => i64 ? WOp.I64Mul : WOp.I32Mul, BinOp.And => i64 ? WOp.I64And : WOp.I32And,
            BinOp.Or => i64 ? WOp.I64Or : WOp.I32Or, BinOp.Xor => i64 ? WOp.I64Xor : WOp.I32Xor,
            BinOp.Shl => i64 ? WOp.I64Shl : WOp.I32Shl, BinOp.Shr => i64 ? WOp.I64ShrS : WOp.I32ShrS,
            BinOp.ShrUn => i64 ? WOp.I64ShrU : WOp.I32ShrU,
            _ => throw new NotSupportedException(op.ToString()),
        });
    }

    void Compare(Node n)
    {
        var op = (CmpOp)n.Sub;
        var k = n.A[0].Kind;
        Get(n.A[0]); Get(n.A[1]);
        if (k is VK.F32 or VK.F64)
        {
            bool d = k == VK.F64;
            // the unordered forms are true when either is NaN: the negation of the opposite ordered test
            (WOp o, bool not) = op switch
            {
                CmpOp.Eq => (d ? WOp.F64Eq : WOp.F32Eq, false), CmpOp.Ne => (d ? WOp.F64Ne : WOp.F32Ne, false),
                CmpOp.Gt => (d ? WOp.F64Gt : WOp.F32Gt, false), CmpOp.GtUn => (d ? WOp.F64Le : WOp.F32Le, true),
                CmpOp.Ge => (d ? WOp.F64Ge : WOp.F32Ge, false), CmpOp.GeUn => (d ? WOp.F64Lt : WOp.F32Lt, true),
                CmpOp.Lt => (d ? WOp.F64Lt : WOp.F32Lt, false), CmpOp.LtUn => (d ? WOp.F64Ge : WOp.F32Ge, true),
                CmpOp.Le => (d ? WOp.F64Le : WOp.F32Le, false), _ => (d ? WOp.F64Gt : WOp.F32Gt, true),
            };
            E.Op(o);
            if (not) E.Op(WOp.I32Eqz);
            return;
        }
        bool l = k == VK.I64;
        E.Op(op switch
        {
            CmpOp.Eq => l ? WOp.I64Eq : WOp.I32Eq, CmpOp.Ne => l ? WOp.I64Ne : WOp.I32Ne,
            CmpOp.Gt => l ? WOp.I64GtS : WOp.I32GtS, CmpOp.GtUn => l ? WOp.I64GtU : WOp.I32GtU,
            CmpOp.Ge => l ? WOp.I64GeS : WOp.I32GeS, CmpOp.GeUn => l ? WOp.I64GeU : WOp.I32GeU,
            CmpOp.Lt => l ? WOp.I64LtS : WOp.I32LtS, CmpOp.LtUn => l ? WOp.I64LtU : WOp.I32LtU,
            CmpOp.Le => l ? WOp.I64LeS : WOp.I32LeS, _ => l ? WOp.I64LeU : WOp.I32LeU,
        });
    }

    void Convert(Node n)
    {
        var src = n.A[0];
        var to = (VK)n.Sub;
        bool fromUn = n.I == 1;
        var sk = src.Kind is VK.Ptr or VK.Ref ? VK.I32 : src.Kind;
        if (to == VK.Ptr) to = VK.I32;
        Get(src);
        if (n.Ovf) { E.Call(C.Rt.ConvOvf(sk, to, n.Small, n.Unsigned, fromUn)); return; }
        switch (to)
        {
            case VK.I32:
                if (sk == VK.I64) E.Op(WOp.I32WrapI64);
                else if (sk == VK.F32) E.Op(n.Unsigned && n.Small == 4 ? WOp.I32TruncSatF32U : WOp.I32TruncSatF32S);
                else if (sk == VK.F64) E.Op(n.Unsigned && n.Small == 4 ? WOp.I32TruncSatF64U : WOp.I32TruncSatF64S);
                if (n.Small == 1) { if (n.Unsigned) { E.I32(0xFF); E.Op(WOp.I32And); } else E.Op(WOp.I32Extend8S); }
                else if (n.Small == 2) { if (n.Unsigned) { E.I32(0xFFFF); E.Op(WOp.I32And); } else E.Op(WOp.I32Extend16S); }
                return;
            case VK.I64:
                if (sk == VK.I32) E.Op(n.Unsigned || fromUn || IsUnsignedSource(src) ? WOp.I64ExtendI32U : WOp.I64ExtendI32S);
                else if (sk == VK.F32) E.Op(n.Unsigned ? WOp.I64TruncSatF32U : WOp.I64TruncSatF32S);
                else if (sk == VK.F64) E.Op(n.Unsigned ? WOp.I64TruncSatF64U : WOp.I64TruncSatF64S);
                return;
            case VK.F32:
                if (sk == VK.I32) E.Op(fromUn ? WOp.F32ConvertI32U : WOp.F32ConvertI32S);
                else if (sk == VK.I64) E.Op(fromUn ? WOp.F32ConvertI64U : WOp.F32ConvertI64S);
                else if (sk == VK.F64) E.Op(WOp.F32DemoteF64);
                return;
            case VK.F64:
                if (sk == VK.I32) E.Op(fromUn ? WOp.F64ConvertI32U : WOp.F64ConvertI32S);
                else if (sk == VK.I64) E.Op(fromUn ? WOp.F64ConvertI64U : WOp.F64ConvertI64S);
                else if (sk == VK.F32) E.Op(WOp.F64PromoteF32);
                return;
        }
        throw new NotSupportedException("conversion to " + to);
    }

    /// <summary>ECMA's conv.u8 zero-extends; conv.i8 sign-extends an int32 (an unsigned native int is zero-extended too).</summary>
    static bool IsUnsignedSource(Node src) => src.Kind == VK.Ptr && src.Type?.Category == TypeFlags.UIntPtr;

    void Call(Node n)
    {
        var m = (MethodDesc)n.X!;
        if (C.TryIntrinsic(E, m, n, this)) return;
        var kind = (CallKind)n.Sub;
        if (kind == CallKind.Direct && n.Ovf && n.A.Length > 0 && n.A[0].Kind == VK.Ref) { Get(n.A[0]); E.Call(C.Rt.NonNull); E.Op(WOp.Drop); }
        if (kind == CallKind.Direct && m.Signature.IsStatic) C.CctorCheckForCall(E, m);
        foreach (var a in n.A) Get(a);
        E.Call(kind == CallKind.Direct ? C.FunctionFor(m) : C.DispatchFor(m));
        if (n.HasValue) Set(n);
    }

    public void PushArgs(Node n) { foreach (var a in n.A) Get(a); }
    public void SetResult(Node n) { if (n.HasValue) Set(n); }

    void NewObj(Node n)
    {
        var ctor = (MethodDesc)n.X!;
        var t = (DefType)ctor.OwningType;
        C.Constructed(t);
        C.CctorCheckForCall(E, ctor);
        E.I32(IlContext.InstanceSize(t)); E.Tid(C.TypeId(t)); E.Call(C.Rt.AllocObject);
        Set(n);
        E.Get(L(n));
        foreach (var a in n.A) Get(a);
        E.Call(C.FunctionFor(ctor));
    }
}
