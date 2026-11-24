#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Memory;
using Il2Wasm.IL.Types;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

/// <summary>An IR body to a WASM function of the existing back end: the regions and blocks structured into WASM's
/// blocks, loops and ifs (Ramsey's "Beyond Relooper" over each region's graph, with a dispatch loop where a region's
/// flow is irreducible), try regions as try_table, and every operation lowered with naive counting
/// (Ir/Ownership.cs): each value in a local of its own, zero when it holds no count.</summary>
sealed partial class FunctionLowering
{
    readonly IlCompiler C;
    readonly Body _body;
    readonly MethodPlan _own;
    readonly WasmFunction _fn;
    readonly Emitter E;
    readonly Dictionary<Node, int> _local = new();
    readonly int[] _varLocal;
    readonly Dictionary<Region, (int obj, int exn)> _handlerLocals = new();
    readonly Dictionary<Region, int> _leaveLocal = new();
    readonly Dictionary<Region, Label> _filterEnd = new();
    readonly Dictionary<Region, int> _filterResult = new();
    readonly Dictionary<Region, Label> _finallyEnd = new();

    public FunctionLowering(IlCompiler c, Body body, MethodPlan own, WasmFunction fn)
    {
        C = c; _body = body; _own = own; _fn = fn; E = new Emitter(fn);
        SelfTest(); // (IL2WASM_LOWER_SELFTEST: FunctionLowering.SelfTest.cs; nothing by default)
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
        // struct values: a copy each in the frame (and one per struct argument of a call, the callee's to change)
        foreach (var b in body.Blocks)
            foreach (var n in b.Phis.Concat(b.Code))
            {
                if (n.Kind == VK.Struct) _slot[n] = Reserve(n.Type!);
                // (a primitive made by its constructor, new IntPtr(v): made in a frame slot, then read)
                else if (n.Op == Opc.NewObj && n.Kind is VK.I32 or VK.I64 or VK.F32 or VK.F64 or VK.Ptr) _slot[n] = Reserve(((MethodDesc)n.X!).OwningType);
                if (n.Op is Opc.Call or Opc.NewObj or Opc.CallI)
                    for (int i = 0; i < n.A.Length; i++) if (n.A[i].Kind == VK.Struct) _argSlot[(n, i)] = Reserve(n.A[i].Type!);
                // (MEMORY-IR 2.3: an object the plan keeps in the frame, Virtual or FrameOwned, gets a slot of its own)
                if (!own.Allocs.IsEmpty && own.Allocs[n] != AllocKind.Heap) ReserveFrameObject(n);
            }
        _frameSize = (_frameSize + 15) & ~15;
        if (body.UsesLocAlloc && _frameSize == 0) _frameSize = 16;
        if (_frameSize > 0) _fb = E.AddLocal(ValType.I32);
    }

    readonly Dictionary<Node, int> _slot = new();
    readonly Dictionary<(Node, int), int> _argSlot = new();

    int Reserve(TypeDesc t)
    {
        var dt = (DefType)t;
        int size = Math.Max(4, dt.InstanceFieldSize.AsInt), align = Math.Max(4, Math.Min(8, dt.InstanceFieldAlignment.AsInt));
        _frameSize = (_frameSize + align - 1) / align * align;
        int at = _frameSize;
        _frameSize += size;
        return at;
    }

    static int SizeOfStruct(TypeDesc t) => ((DefType)t).InstanceFieldSize.AsInt;

    void FrameAddr(int off) { E.Get(_fb); if (off != 0) { E.I32(off); E.Op(WOp.I32Add); } }

    /// <summary>A variable's storage address (a struct variable: in the frame, or the caller's copy of an argument).</summary>
    void VarAddr(int i)
    {
        var v = _body.Vars[i];
        if (v.ArgIndex >= 0) E.Get(v.ArgIndex); else FrameAddr(v.Offset);
    }

    /// <summary>A struct's counts: the references in it, each retained or released.</summary>
    void StructCounts(TypeDesc t, bool retain) { if (Kinds.Counted(t)) E.Call(retain ? C.Rt.RetainStruct(t) : C.Rt.ReleaseStruct(t)); else E.Op(WOp.Drop); }

    /// <summary>The struct at the address on the stack copied into n's own copy, with counts of its own.</summary>
    void LoadStruct(Node n)
    {
        var t = n.Type!;
        int src = E.AddLocal(ValType.I32);
        E.Set(src);
        Get(n); E.Get(src); E.I32(SizeOfStruct(t)); E.Emit(WOp.MemoryCopy);
        if (Kinds.Counted(t) && LoadRetains(n)) { Rc(n, LoadCategory(n)); Get(n); StructCounts(t, true); }
    }

    /// <summary>A struct value stored at the address in the local: the old contents released, the new copied in (its
    /// counts moved when it is the value's last use, made again otherwise).</summary>
    void StoreStruct(int addr, int offset, Node v, TypeDesc t, bool moved, Node site)
    {
        int dst = E.AddLocal(ValType.I32);
        E.Get(addr); if (offset != 0) { E.I32(offset); E.Op(WOp.I32Add); }
        E.Set(dst);
        if (Kinds.Counted(t) && OldReleased(site)) { Rc(site, RcCategory.StoreOld); E.Get(dst); StructCounts(t, false); }
        E.Get(dst); Get(v); E.I32(SizeOfStruct(t)); E.Emit(WOp.MemoryCopy);
        if (Kinds.Counted(t)) { if (moved) Clear(v); else { Rc(site, RcCategory.StoreRetain); E.Get(dst); StructCounts(t, true); } }
    }

    int _frameSize, _fb = -1;
    static readonly string? s_trace = Environment.GetEnvironmentVariable("IL2WASM_IL_TRACE") is { Length: > 0 } t ? t : null;

    /// <summary>A variable's value on the stack.</summary>
    void VarGet(int i)
    {
        var v = _body.Vars[i];
        if (v.Kind == VK.Struct) { VarAddr(i); return; }
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
        if (n.Kind == VK.Struct) { FrameAddr(_slot[n]); return; }
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
        // (IL2WASM_IL_TRACE=name: a call of the host's il2wasm_trace.enter(the method's number, its first three i32
        // arguments) on entry to each method whose name contains it; host/run.mjs logs any import it does not know)
        if (s_trace != null && _body.Method.ToString()!.Contains(s_trace))
        {
            var ps = _fn.Type.Params;
            for (int i = 0; i < 3; i++) { if (i < ps.Length && ps[i] == ValType.I32) E.Get(i); else E.I32(0); }
            E.Call(C.TraceImport);
        }
        bool hasRefs = _own.CountedValues.Count > 0 || _body.Vars.Any(v => v.Kind == VK.Ref) || _fb >= 0;
        if (_fb >= 0)
        {
            E.GGet(C.StackPointer); E.I32(_frameSize); E.Op(WOp.I32Sub); E.Tee(_fb); E.GSet(C.StackPointer);
            E.Get(_fb); E.I32(0); E.I32(_frameSize); E.Emit(WOp.MemoryFill);
        }
        // (pre-create the locals of the reference values: the cleanup below names them all)
        foreach (var v in _own.CountedValues) if (v.Op != Opc.Const && v.Kind != VK.Struct) L(v);
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
            foreach (var v in _own.CountedValues)
            {
                // (a borrowed value holds no count; a frame object's children go below)
                if (!Holds(v) || _frameObj.ContainsKey(v)) continue;
                if (v.Kind == VK.Struct) { Rc(v, RcCategory.Unwind); Get(v); StructCounts(v.Type!, false); }
                else if (_local.TryGetValue(v, out int l)) { Rc(v, RcCategory.Unwind); E.Get(l); E.Call(C.Rt.Release); }
            }
            foreach (var fo in _frameObjOrder) FrameDrop(fo);
            for (int i = 0; i < _body.Vars.Count; i++)
            {
                var vr = _body.Vars[i];
                if (vr.Kind == VK.Ref) { VarGet(i); E.Call(C.Rt.Release); }
                else if (vr.Kind == VK.Struct && vr.ArgIndex < 0 && Kinds.Counted(vr.Type)) { VarAddr(i); StructCounts(vr.Type, false); }
            }
            PopFrame();
            E.Get(exn); E.Op(WOp.ThrowRef);
            E.End();
        }
        E.Op(WOp.Unreachable);
        if (Stats) CountStats();
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
            // il-runtime: each operation's file and line, from the PDB (DebugInfo.cs)
            if (n.ILOffset >= 0) E.CurrentPos = C.PositionOf(_body.Method, n.ILOffset);
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
            {
                // (a borrowed-return entry, 4.4: the result is the caller's borrow, it takes no count)
                bool borrowed = _own.Entry.Kind == VariantKind.BorrowReturn;
                if (borrowed && n.A.Length > 0 && (n.Moves & 1) != 0)
                    throw new PlanVerifyException($"{_body.Method}{_own.Entry.Suffix}: the borrowed-return entry returns an owned value (#{n.A[0].Id})");
                if (n.A.Length > 0 && n.A[0].Kind == VK.Struct)
                {
                    int rb = E.AddLocal(ValType.I32);
                    E.Get(_fn.Type.Params.Length - 1); E.Set(rb);
                    E.Get(rb); Get(n.A[0]); E.I32(SizeOfStruct(n.A[0].Type!)); E.Emit(WOp.MemoryCopy);
                    if (Kinds.Counted(n.A[0].Type!) && !borrowed) { if ((n.Moves & 1) != 0) Clear(n.A[0]); else { Rc(n, RcCategory.Result); E.Get(rb); StructCounts(n.A[0].Type!, true); } }
                }
                else if (n.A.Length > 0) { Get(n.A[0]); if (n.A[0].Kind == VK.Ref && (n.Moves & 1) == 0 && !borrowed) { Rc(n, RcCategory.Result); E.Call(C.Rt.Retain); } if ((n.Moves & 1) != 0) Clear(n.A[0]); }
            }
                ReleaseVars();
                PopFrame();
                E.Op(WOp.Return);
                return;
            case Opc.Throw:
                // il-runtime: throw null is a NullReferenceException; the trace and the first pass (Filters.cs)
                Get(n.A[0]); E.Call(C.Rt.NonNull); E.Call(C.ThrowHook);
                Get(n.A[0]);
                if ((n.Moves & 1) != 0) Clear(n.A[0]); else { Rc(n, RcCategory.Generic); E.Call(C.Rt.Retain); }
                E.Emit(WOp.Throw, 0, 0, C.ExceptionTag);
                return;
            case Opc.Rethrow:
            {
                var h = Enclosing(b.Region, RegionKind.Catch) ?? throw new InvalidProgramException("rethrow outside a catch");
                // il-runtime: a rethrow's first pass (Filters.cs)
                E.Get(_handlerLocals[h].obj); E.Call(C.FirstPass);
                E.I32(0); E.Set(_handlerLocals[h].obj); // (its count goes on with the exception)
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
        // (structs: copied one by one, before the numbers and references)
        foreach (var phi in s.Phis.Where(p => p.Kind == VK.Struct))
        {
            var v = phi.A[pi];
            Get(phi); Get(v); E.I32(SizeOfStruct(phi.Type!)); E.Emit(WOp.MemoryCopy);
            if (Kinds.Counted(phi.Type!)) { if (_own.PhiMoves.Contains((phi, pi))) Clear(v); else if (Holds(phi)) { Rc(phi, RcCategory.Phi); Get(phi); StructCounts(phi.Type!, true); } }
        }
        foreach (var phi in s.Phis.Where(p => p.Kind != VK.Struct))
        {
            var v = phi.A[pi];
            Get(v);
            // (a borrowed phi, its inputs borrows of one path: no count)
            if (v.Kind == VK.Ref && !_own.PhiMoves.Contains((phi, pi)) && Holds(phi)) { Rc(phi, RcCategory.Phi); E.Call(C.Rt.Retain); }
        }
        foreach (var phi in s.Phis) if (phi.Kind != VK.Struct && _own.PhiMoves.Contains((phi, pi))) Clear(phi.A[pi]);
        for (int i = s.Phis.Count - 1; i >= 0; i--) if (s.Phis[i].Kind != VK.Struct) Set(s.Phis[i]);
    }

    void Clear(Node v)
    {
        if (v.Kind == VK.Struct) { Get(v); E.I32(0); E.I32(SizeOfStruct(v.Type!)); E.Emit(WOp.MemoryFill); return; }
        if (v.Kind == VK.Ref && v.Op is not (Opc.Const or Opc.LdNull or Opc.LdStr)) { E.I32(0); E.Set(L(v)); }
    }

    void ReleaseVars()
    {
        for (int i = 0; i < _body.Vars.Count; i++)
        {
            var vr = _body.Vars[i];
            if (vr.Kind == VK.Ref) { VarGet(i); E.Call(C.Rt.Release); E.I32(0); VarSet(i); }
            else if (vr.Kind == VK.Struct && vr.ArgIndex < 0 && Kinds.Counted(vr.Type)) { VarAddr(i); StructCounts(vr.Type, false); VarAddr(i); E.I32(0); E.I32(SizeOfStruct(vr.Type)); E.Emit(WOp.MemoryFill); }
        }
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
            // il-runtime: the try's record for the first pass (Filters.cs)
            E.I32(0); E.Set(obj); // il-runtime: no exception held yet
            int rec = EhEnter(tr);
            var done = E.Block();
            var caught = E.Block(new FuncType([], [ValType.I32, ValType.ExnRef]));
            E.TryTable(null, (1, C.ExceptionTag, caught));
            EmitLevel(new Level(tr, tr.Entry), (lv, done, exits));
            E.End();
            E.Br(done);
            E.End();
            E.Set(exn); E.Set(obj);
            RestoreFrame();
            // il-runtime: a try with a filter takes the first pass's decision (the clause, 1-based), and its record goes
            int clause = EhCaught(tr, rec, obj);
            for (int hi = 0; hi < tr.Handlers.Count; hi++)
            {
                var h = tr.Handlers[hi];
                _handlerLocals[h] = (obj, exn);
                bool open = false;
                if (clause >= 0)
                {
                    E.Get(clause); E.I32(hi + 1); E.Op(WOp.I32Eq); E.If(); open = true;
                }
                else if (h.Filter != null)
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
                // il-runtime: the catch took it: the exception's trace ends at this frame (DebugInfo.cs)
                E.Get(obj); E.Call(C.OnCatchFn);
                // il-runtime: an exception out of the handler lets the one in flight go (a rethrow hands it on)
                {
                    int hx = E.AddLocal(ValType.ExnRef);
                    var hdone = E.Block();
                    var hcaught = E.Block(ValType.ExnRef);
                    E.TryTable(null, (3, null, hcaught));
                    EmitLevel(new Level(h, h.Entry), (lv, done, exits));
                    E.End();
                    E.Br(hdone);
                    E.End();
                    E.Set(hx); E.Get(obj); E.Call(C.Rt.Release); E.I32(0); E.Set(obj); E.Get(hx); E.Op(WOp.ThrowRef);
                    E.End();
                }
                if (open) E.End();
                else break;
            }
            E.Get(exn); E.Op(WOp.ThrowRef);
            E.End();
            EhLeave(rec);
            // il-runtime: the handler left: the exception in flight goes
            E.Get(obj); E.Call(C.Rt.Release); E.I32(0); E.Set(obj);
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
            case Opc.Retain or Opc.Release when Kind(n) == CountKind.Elided: return;
            case Opc.Retain when n.A[0].Kind == VK.Struct: Rc(n, RcCategory.Naive); Get(n.A[0]); StructCounts(n.A[0].Type!, true); return;
            case Opc.Release when n.A[0].Kind == VK.Struct: Rc(n, RcCategory.Naive); Get(n.A[0]); StructCounts(n.A[0].Type!, false); Clear(n.A[0]); return;
            case Opc.Retain: Rc(n, RcCategory.Naive); Get(n.A[0]); E.Call(C.Rt.Retain); E.Op(WOp.Drop); return;
            case Opc.Release:
                if (n.A[0].Op is Opc.LdStr or Opc.LdNull) return;
                // (a frame object's last use: its children go and its slot is emptied, 2.3)
                if (_frameObj.ContainsKey(n.A[0])) { Rc(n, RcCategory.Naive); FrameDrop(n.A[0]); E.I32(0); E.Set(L(n.A[0])); return; }
                Rc(n, RcCategory.Naive); Get(n.A[0]); E.Call(C.Rt.Release); E.I32(0); E.Set(L(n.A[0])); return;
            case Opc.CatchVal:
            {
                var h = _body.Regions.First(r => r.Entry == n.Block && (r.Kind is RegionKind.Catch or RegionKind.Filter) && _handlerLocals.ContainsKey(r));
                E.Get(_handlerLocals[h].obj);
                // (a filter's value is its own count: the exception in flight keeps one)
                // (the value is a count of its own: the exception in flight keeps one, which the try's obj local holds
                // until the handler leaves, rethrows or throws; il-runtime: a rethrow used the object after the handler
                // let it go)
                if (LoadRetains(n)) { Rc(n, RcCategory.Generic); E.Call(C.Rt.Retain); }
                Set(n);
                return;
            }
            case Opc.LocA: VarAddr((int)n.I); Set(n); return;
            case Opc.MkRefAny:
            {
                // TypedReference { RuntimeTypeHandle type; ref byte _value; IntPtr _type }: the type as its id
                var (th, val, tp) = TypedRefFields(n.Type!);
                int tid = C.TypeId((TypeDesc)n.X!);
                Get(n); E.I32(0); E.I32(SizeOfStruct(n.Type!)); E.Emit(WOp.MemoryFill);
                Get(n); Get(n.A[0]); E.Store(WOp.I32Store, val);
                Get(n); E.Tid(tid); E.Store(WOp.I32Store, tp);
                if (th >= 0) { Get(n); E.Tid(tid); E.Store(WOp.I32Store, th); }
                return;
            }
            case Opc.RefAnyVal:
            {
                // the address, when the reference is of exactly that type (InvalidCastException otherwise)
                var (_, val, tp) = TypedRefFields(n.A[0].Type!);
                Get(n.A[0]); E.Load(WOp.I32Load, tp); E.Tid(C.TypeId((TypeDesc)n.X!)); E.Op(WOp.I32Ne);
                E.If(); E.Call(C.ThrowNew("InvalidCastException")); E.End();
                Get(n.A[0]); E.Load(WOp.I32Load, val); Set(n);
                return;
            }
            case Opc.RefAnyType:
            {
                var (_, _, tp) = TypedRefFields(n.A[0].Type!);
                Get(n); E.I32(0); E.I32(SizeOfStruct(n.Type!)); E.Emit(WOp.MemoryFill);
                Get(n); Get(n.A[0]); E.Load(WOp.I32Load, tp); E.Store(WOp.I32Store);
                return;
            }
            case Opc.InitBlk: Get(n.A[0]); Get(n.A[1]); Get(n.A[2]); E.Emit(WOp.MemoryFill); return;
            case Opc.CpBlk: Get(n.A[0]); Get(n.A[1]); Get(n.A[2]); E.Emit(WOp.MemoryCopy); return;
            case Opc.LocAlloc:
            {
                // below the frame on the shadow stack, zeroed (C# sets localsinit); given back with the frame
                int sz = Temp(ValType.I32);
                Get(n.A[0]); E.I32(15); E.Op(WOp.I32Add); E.I32(-16); E.Op(WOp.I32And); E.Set(sz);
                E.GGet(C.StackPointer); E.Get(sz); E.Op(WOp.I32Sub); E.GSet(C.StackPointer);
                E.GGet(C.StackPointer); E.I32(0); E.Get(sz); E.Emit(WOp.MemoryFill);
                E.GGet(C.StackPointer); Set(n);
                return;
            }
            case Opc.TypeOf:
                // the type's one RuntimeType (CoreLib's, by its id: immortal)
                C.NoteTypeOf((TypeDesc)n.X!);
                E.Tid(C.TypeId((TypeDesc)n.X!)); E.Call(C.FunctionFor(C.CoreMethod("System", "Type", "FromId"))); Set(n);
                return;
            case Opc.LdToken:
            {
                // a handle: its one field the type's id (a field's or method's: not used yet, 0)
                Get(n); E.I32(0); E.I32(SizeOfStruct(n.Type!)); E.Emit(WOp.MemoryFill);
                Get(n);
                if (n.X is TypeDesc tt) E.Tid(C.TypeId(tt)); else E.I32(0);
                E.Store(WOp.I32Store);
                return;
            }
            case Opc.InitArray:
            {
                var data = C.FieldData((FieldDesc)n.X!);
                Get(n.A[0]); E.Call(C.Rt.NonNull); E.I32(n.A[0].Type is ArrayType iat ? IlCompiler.ArrayDataOffset(iat) : 16); E.Op(WOp.I32Add);
                E.I32Addr(data); E.I32(data.Bytes.Length); E.Emit(WOp.MemoryCopy);
                return;
            }
            case Opc.CreateSpan:
            {
                // ReadOnlySpan<T> over the field's data: its reference and its length
                var data = C.FieldData((FieldDesc)n.X!);
                var st = (DefType)n.Type!;
                var refField = st.GetFields().First(f => !f.IsStatic && f.FieldType.IsByRef);
                var lenField = st.GetFields().First(f => !f.IsStatic && f.FieldType.IsWellKnownType(WellKnownType.Int32));
                Get(n); E.I32Addr(data); E.Store(WOp.I32Store, IlContext.FieldOffset(refField));
                Get(n); E.I32(data.Bytes.Length / Math.Max(1, (int)n.I)); E.Store(WOp.I32Store, IlContext.FieldOffset(lenField));
                return;
            }
            case Opc.LdFtn: E.I32Fn(C.Module, C.FunctionFor((MethodDesc)n.X!)); Set(n); return;
            case Opc.LdVirtFtn:
            {
                var vm = (MethodDesc)n.X!;
                // (ldvirtftn on null: NullReferenceException)
                Get(n.A[0]); E.Call(C.Rt.NonNull); E.Op(WOp.Drop);
                E.I32Fn(C.Module, vm.IsVirtual && !vm.IsFinal ? C.DispatchFor(vm) : C.FunctionFor(vm)); Set(n);
                return;
            }
            case Opc.CallI:
            {
                var sig = (MethodSignature)n.X!;
                var ps = new List<ValType>();
                if (!sig.IsStatic) ps.Add(ValType.I32);
                for (int i = 0; i < sig.Length; i++) ps.Add(ValOf(Kinds.Of(sig[i])));
                bool sret = !sig.ReturnType.IsVoid && Kinds.Of(sig.ReturnType) == VK.Struct;
                if (sret) ps.Add(ValType.I32);
                var ft = new FuncType(ps.ToArray(), sig.ReturnType.IsVoid || sret ? [] : [ValOf(Kinds.Of(sig.ReturnType))]);
                var fp = n.A[^1];
                // (struct arguments: copies of their own, as a call's; the result's buffer; then the function)
                PushCallArgs(n, n.A.Length - 1);
                if (sret) Get(n);
                Get(fp);
                E.Emit(WOp.CallIndirect, 0, 0, ft);
                if (n.HasValue && n.Kind != VK.Struct) Set(n);
                AfterCall(n);
                return;
            }
            case Opc.VarLd when n.Kind == VK.Struct: VarAddr((int)n.I); LoadStruct(n); return;
            case Opc.VarSt when _body.Vars[(int)n.I].Kind == VK.Struct:
            {
                int a = E.AddLocal(ValType.I32);
                VarAddr((int)n.I); E.Set(a);
                StoreStruct(a, 0, n.A[0], _body.Vars[(int)n.I].Type, (n.Moves & 1) != 0, n);
                return;
            }
            case Opc.VarLd:
                VarGet((int)n.I);
                if (n.Kind == VK.Ref && LoadRetains(n)) { Rc(n, LoadCategory(n)); E.Call(C.Rt.Retain); }
                Set(n);
                return;
            case Opc.VarSt:
            {
                int vi = (int)n.I;
                if (n.A[0].Kind == VK.Ref)
                {
                    bool oldGoes = OldReleased(n);
                    int old = oldGoes ? E.AddLocal(ValType.I32) : -1;
                    if (oldGoes) { VarGet(vi); E.Set(old); }
                    Get(n.A[0]); if ((n.Moves & 1) == 0) { Rc(n, RcCategory.StoreRetain); E.Call(C.Rt.Retain); } VarSet(vi);
                    if (oldGoes) { Rc(n, RcCategory.StoreOld); E.Get(old); E.Call(C.Rt.Release); }
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
                if (_frameObj.ContainsKey(n)) { AllocFrame(n); return; }
                Get(n.A[0]); E.I32(Kinds.SizeOf(at.ElementType)); E.Tid(C.TypeId(at)); E.Call(C.Rt.AllocArray);
                Set(n);
                return;
            }
            case Opc.LdLen: Get(n.A[0]); E.Call(C.Rt.NonNull); E.Load(WOp.I32Load, 12); Set(n); return;
            case Opc.LdElem:
            {
                var et = (TypeDesc)n.X!;
                Get(n.A[0]); Get(n.A[1]); E.Call(C.Rt.ElemAddr(Kinds.SizeOf(et)));
                if (n.Kind == VK.Struct) { LoadStruct(n); return; }
                Load(et, 0);
                if (n.Kind == VK.Ref && LoadRetains(n)) { Rc(n, LoadCategory(n)); E.Call(C.Rt.Retain); }
                Set(n);
                return;
            }
            case Opc.LdElemA:
                if (n.Ovf)
                {
                    // (the exact-type check of ldelema of a reference type: string[] seen as object[] has no object&)
                    var at = ((TypeDesc)n.X!).MakeArrayType();
                    C.TypeId(at);
                    Get(n.A[0]); E.Call(C.Rt.NonNull); E.Load(WOp.I32Load, 4); E.Tid(C.TypeId(at)); E.Op(WOp.I32Ne);
                    E.If(); E.Call(C.FunctionFor(C.CoreMethod("System", "ThrowHelper", "ThrowArrayTypeMismatchException"))); E.End();
                }
                Get(n.A[0]); Get(n.A[1]); E.Call(C.Rt.ElemAddr(Kinds.SizeOf((TypeDesc)n.X!))); Set(n);
                return;
            case Opc.StElem:
            {
                var et = (TypeDesc)n.X!;
                int addr = Temp(ValType.I32);
                Get(n.A[0]); Get(n.A[1]); E.Call(C.Rt.ElemAddr(Kinds.SizeOf(et))); E.Set(addr);
                // a reference stored into an array whose element type it may not be (string[] as object[]): the
                // covariance check (ArrayTypeMismatchException), unless the array's element type has no subtypes
                // that an array of it could be, or the value is null
                if (n.Ovf && n.A[2].Kind == VK.Ref && n.A[2].Op != Opc.LdNull
                    && !(n.A[0].Type is ArrayType { ElementType: var se } && (se.IsValueType || se is MetadataType { IsSealed: true } && !se.IsInterface)))
                {
                    Get(n.A[0]); Get(n.A[2]); E.Call(C.FunctionFor(C.CoreMethod("Il2Wasm.Runtime", "Rt", "StoreCheck")));
                }
                StoreAt(addr, 0, n.A[2], et, (n.Moves & 4) != 0, n);
                MovedOut(n);
                return;
            }
            case Opc.LdFld:
            {
                var f = (FieldDesc)n.X!;
                Get(n.A[0]);
                if (n.A[0].Kind == VK.Ref) E.Call(C.Rt.NonNull);
                if (n.Kind == VK.Struct) { E.I32(IlContext.FieldOffset(f)); E.Op(WOp.I32Add); LoadStruct(n); return; }
                Load(f.FieldType, IlContext.FieldOffset(f));
                if (n.Kind == VK.Ref && LoadRetains(n)) { Rc(n, LoadCategory(n)); E.Call(C.Rt.Retain); }
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
                StoreAt(addr, IlContext.FieldOffset(f), n.A[1], f.FieldType, (n.Moves & 2) != 0, n);
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
                if (n.Kind == VK.Struct) { LoadStruct(n); return; }
                Load(f.FieldType, 0);
                if (n.Kind == VK.Ref && LoadRetains(n)) { Rc(n, LoadCategory(n)); E.Call(C.Rt.Retain); }
                Set(n);
                return;
            }
            case Opc.StSFld:
            {
                var f = (FieldDesc)n.X!;
                C.CctorCheck(E, f.OwningType);
                int addr = Temp(ValType.I32);
                E.I32Addr(C.StaticAddress(f)); E.Set(addr);
                StoreAt(addr, 0, n.A[0], f.FieldType, (n.Moves & 1) != 0, n);
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
            case Opc.LdObj: Get(n.A[0]); LoadStruct(n); return;
            case Opc.InitObj:
            {
                var t = (TypeDesc)n.X!;
                int a = E.AddLocal(ValType.I32);
                Get(n.A[0]); E.Set(a);
                if (Kinds.Of(t) == VK.Struct)
                {
                    if (Kinds.Counted(t)) { E.Get(a); StructCounts(t, false); }
                    E.Get(a); E.I32(0); E.I32(SizeOfStruct(t)); E.Emit(WOp.MemoryFill);
                }
                else if (Kinds.Of(t) == VK.Ref) { int old = E.AddLocal(ValType.I32); E.Get(a); E.Load(WOp.I32Load); E.Set(old); E.Get(a); E.I32(0); E.Store(WOp.I32Store); E.Get(old); E.Call(C.Rt.Release); }
                else { E.Get(a); ZeroOf(t); Store(t, 0); }
                return;
            }
            case Opc.Unbox:
                Get(n.A[0]); E.Call(C.UnboxFn((TypeDesc)n.X!)); E.I32(IlContext.ObjectHeader); E.Op(WOp.I32Add); Set(n); return;
            case Opc.LdInd:
                Get(n.A[0]);
                Load(n.Type!, 0);
                if (n.Kind == VK.Ref && LoadRetains(n)) { Rc(n, LoadCategory(n)); E.Call(C.Rt.Retain); }
                Set(n);
                return;
            case Opc.StInd:
            {
                int addr = Temp(ValType.I32);
                Get(n.A[0]); E.Set(addr);
                StoreAt(addr, 0, n.A[1], n.Type!, (n.Moves & 2) != 0, n);
                MovedOut(n);
                return;
            }
            // (a cast to Nullable<T> is one to T: a Nullable's box is its value's)
            case Opc.CastClass:
                Get(n.A[0]); E.Call(C.CastFn(NonNullable((TypeDesc)n.X!))); if (LoadRetains(n)) { Rc(n, RcCategory.Generic); E.Call(C.Rt.Retain); } Set(n); return;
            case Opc.IsInst:
                Get(n.A[0]); E.Call(C.IsInstFn(NonNullable((TypeDesc)n.X!))); if (LoadRetains(n)) { Rc(n, RcCategory.Generic); E.Call(C.Rt.Retain); } Set(n); return;
            case Opc.Box when ((TypeDesc)n.X!).IsNullable:
            {
                // a Nullable<T> boxes as its value (a T's box), or as null when it has none (ECMA-335 III.4.1)
                var t = (TypeDesc)n.X!;
                var vt = t.Instantiation[0];
                C.Constructed(vt);
                var (hv, vf) = NullableFields(t);
                int es = IlCompiler.SizeOfAny(vt);
                Get(n.A[0]); E.Load(WOp.I32Load8U, hv);
                E.If();
                E.I32(IlContext.ObjectHeader + Math.Max(4, es)); E.Tid(C.TypeId(vt)); E.Call(C.Rt.AllocObject); Set(n);
                Get(n); E.I32(IlContext.ObjectHeader); E.Op(WOp.I32Add); Get(n.A[0]); if (vf != 0) { E.I32(vf); E.Op(WOp.I32Add); } E.I32(es); E.Emit(WOp.MemoryCopy);
                if (Kinds.Counted(vt) && (n.Moves & 1) == 0) { Get(n); E.I32(IlContext.ObjectHeader); E.Op(WOp.I32Add); StructCounts(vt, true); }
                E.Else();
                E.I32(0); Set(n);
                E.End();
                if ((n.Moves & 1) != 0 && Kinds.Counted(t)) Clear(n.A[0]);
                return;
            }
            case Opc.UnboxAny when ((TypeDesc)n.X!).IsNullable:
            {
                // to a Nullable<T>: none from null, else the T's box's value (InvalidCastException for any other)
                var t = (TypeDesc)n.X!;
                var vt = t.Instantiation[0];
                var (hv, vf) = NullableFields(t);
                int es = IlCompiler.SizeOfAny(vt);
                int o = E.AddLocal(ValType.I32);
                Get(n.A[0]); E.Set(o);
                Get(n); E.I32(0); E.I32(SizeOfStruct(t)); E.Emit(WOp.MemoryFill);
                E.Get(o);
                E.If();
                E.Get(o); E.Call(C.UnboxFn(vt)); E.Op(WOp.Drop);
                Get(n); if (vf != 0) { E.I32(vf); E.Op(WOp.I32Add); } E.Get(o); E.I32(IlContext.ObjectHeader); E.Op(WOp.I32Add); E.I32(es); E.Emit(WOp.MemoryCopy);
                Get(n); E.I32(1); E.Store(WOp.I32Store8, hv);
                if (Kinds.Counted(vt)) { Get(n); if (vf != 0) { E.I32(vf); E.Op(WOp.I32Add); } StructCounts(vt, true); }
                E.End();
                return;
            }
            case Opc.Box:
            {
                var t = (TypeDesc)n.X!;
                C.Constructed(t);
                if (_frameObj.ContainsKey(n)) AllocFrame(n);
                else { E.I32(IlContext.ObjectHeader + Math.Max(4, Kinds.SizeOf(t))); E.Tid(C.TypeId(t)); E.Call(C.Rt.AllocObject); Set(n); }
                if (n.A[0].Kind == VK.Struct)
                {
                    int a = E.AddLocal(ValType.I32);
                    E.Get(L(n)); E.Set(a);
                    E.Get(a); E.I32(IlContext.ObjectHeader); E.Op(WOp.I32Add); Get(n.A[0]); E.I32(SizeOfStruct(t)); E.Emit(WOp.MemoryCopy);
                    if (Kinds.Counted(t)) { if ((n.Moves & 1) != 0) Clear(n.A[0]); else { E.Get(a); E.I32(IlContext.ObjectHeader); E.Op(WOp.I32Add); StructCounts(t, true); } }
                    return;
                }
                E.Get(L(n)); Get(n.A[0]); Store(t, IlContext.ObjectHeader);
                return;
            }
            case Opc.UnboxAny:
            {
                var t = (TypeDesc)n.X!;
                Get(n.A[0]); E.Call(C.UnboxFn(t));
                if (n.Kind == VK.Struct) { E.I32(IlContext.ObjectHeader); E.Op(WOp.I32Add); LoadStruct(n); return; }
                Load(t, IlContext.ObjectHeader);
                Set(n);
                return;
            }
        }
        throw new NotSupportedException($"{_body.Method}: IR {n.Op} is not lowered yet");
    }

    /// <summary>TypedReference's fields: its RuntimeTypeHandle (-1 if none), its address and its type.</summary>
    static (int handle, int value, int type) TypedRefFields(TypeDesc t)
    {
        var fs = t.GetFields().Where(f => !f.IsStatic).ToList();
        var h = fs.FirstOrDefault(f => f.GetName() == "type");
        return (h != null ? IlContext.FieldOffset(h) : -1, IlContext.FieldOffset(fs.First(f => f.GetName() == "_value")), IlContext.FieldOffset(fs.First(f => f.GetName() == "_type")));
    }

    static TypeDesc NonNullable(TypeDesc t) => t.IsNullable ? t.Instantiation[0] : t;

    /// <summary>A Nullable&lt;T&gt;'s hasValue and value offsets.</summary>
    static (int hasValue, int value) NullableFields(TypeDesc t)
    {
        var fs = t.GetFields().Where(f => !f.IsStatic).ToList();
        return (IlContext.FieldOffset(fs.First(f => f.GetName() == "hasValue")), IlContext.FieldOffset(fs.First(f => f.GetName() == "value")));
    }

    void ZeroOf(TypeDesc t)
    {
        switch (Kinds.Of(t)) { case VK.I64: E.I64(0); break; case VK.F32: E.F32(0); break; case VK.F64: E.F64(0); break; default: E.I32(0); break; }
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
    void StoreAt(int addr, int offset, Node v, TypeDesc t, bool moved, Node site)
    {
        if (v.Kind == VK.Struct) { StoreStruct(addr, offset, v, t, moved, site); return; }
        if (v.Kind == VK.Ref)
        {
            // (an initializing store, 5.3: the old value is known null, it is neither read nor released)
            bool oldGoes = OldReleased(site);
            int old = oldGoes ? E.AddLocal(ValType.I32) : -1;
            if (oldGoes) { E.Get(addr); E.Load(WOp.I32Load, offset); E.Set(old); }
            E.Get(addr); Get(v); if (!moved) { Rc(site, RcCategory.StoreRetain); E.Call(C.Rt.Retain); } E.Store(WOp.I32Store, offset);
            if (oldGoes) { Rc(site, RcCategory.StoreOld); E.Get(old); E.Call(C.Rt.Release); }
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
        if (TryIntrinsicCall(m, n)) return;
        var kind = (CallKind)n.Sub;
        if (IlCompiler.IsDelegateInvoke(m) && m.GetTypicalMethodDefinition() is Internal.TypeSystem.Ecma.EcmaMethod { IsRuntimeImplemented: true })
        {
            EntryOnly(n);
            PushCallArgs(n);
            if (n.Kind == VK.Struct) Get(n);
            E.Call(C.DelegateInvoke(m));
            if (n.HasValue && n.Kind != VK.Struct) Set(n);
            AfterCall(n);
            return;
        }
        if (kind == CallKind.Direct && n.Ovf && n.A.Length > 0 && n.A[0].Kind == VK.Ref) { Get(n.A[0]); E.Call(C.Rt.NonNull); E.Op(WOp.Drop); }
        if (kind == CallKind.Direct) C.CctorCheckForCall(E, m);
        if (kind != CallKind.Direct) EntryOnly(n);
        PushCallArgs(n);
        if (n.Kind == VK.Struct) Get(n);
        E.Call(kind == CallKind.Direct ? Direct(n, m) : C.DispatchFor(m));
        if (n.HasValue && n.Kind != VK.Struct) Set(n);
        AfterCall(n);
    }

    /// <summary>A call's arguments; a struct argument is a copy of its own (the callee may change it), counted.</summary>
    void PushCallArgs(Node n, int count = -1)
    {
        int end = count < 0 ? n.A.Length : count;
        for (int i = 0; i < end; i++)
        {
            var a = n.A[i];
            // (4.3: a count handed over moves into the call; KeepAlive is one more count, the callee's)
            if (a.Kind != VK.Struct)
            {
                Get(a);
                if (a.Kind == VK.Ref && Conv(n, i) == ArgConv.KeepAlive && !ArgMoves(n, i)) { Rc(n, RcCategory.OwnedConvention); E.Call(C.Rt.Retain); }
                continue;
            }
            int off = _argSlot[(n, i)];
            FrameAddr(off); Get(a); E.I32(SizeOfStruct(a.Type!)); E.Emit(WOp.MemoryCopy);
            if (Kinds.Counted(a.Type!) && !ArgMoves(n, i)) { FrameAddr(off); StructCounts(a.Type!, true); }
            FrameAddr(off);
        }
        // (the moved operands' locals cleared once all are pushed: an operand can be passed twice)
        if (n.Moves != 0 || !_own.Args.IsEmpty)
            for (int i = 0; i < end; i++)
                if (ArgMoves(n, i) && (n.A[i].Kind == VK.Ref || n.A[i].Kind == VK.Struct && Kinds.Counted(n.A[i].Type!))) Clear(n.A[i]);
    }

    /// <summary>The argument copies' counts released after the call.</summary>
    void AfterCall(Node n)
    {
        for (int i = 0; i < n.A.Length; i++)
        {
            var a = n.A[i];
            if (a.Kind != VK.Struct || !Kinds.Counted(a.Type!)) continue;
            // (the callee's: handed over or kept alive for it)
            if (ArgMoves(n, i) || Conv(n, i) == ArgConv.KeepAlive) continue;
            int off = _argSlot[(n, i)];
            FrameAddr(off); StructCounts(a.Type!, false);
            FrameAddr(off); E.I32(0); E.I32(SizeOfStruct(a.Type!)); E.Emit(WOp.MemoryFill);
        }
    }

    public void PushArgs(Node n) { foreach (var a in n.A) Get(a); }
    public void SetResult(Node n) { if (n.HasValue && n.Kind != VK.Struct) Set(n); }

    void NewObj(Node n)
    {
        var ctor = (MethodDesc)n.X!;
        if (ctor.OwningType is ArrayType mat)
        {
            EntryOnly(n);
            PushCallArgs(n);
            E.Call(C.MdArrayCtor(mat, ctor.Signature.Length));
            Set(n);
            return;
        }
        var t = (DefType)ctor.OwningType;
        if (t.IsString)
        {
            // string's constructors are its static Ctor methods (as NativeAOT maps them)
            var sctor = t.GetMethods().FirstOrDefault(x => x.GetName() == "Ctor" && x.Signature.IsStatic && x.Signature.Length == ctor.Signature.Length
                && Enumerable.Range(0, x.Signature.Length).All(i => x.Signature[i] == ctor.Signature[i]))
                ?? throw new NotSupportedException($"string has no Ctor for {ctor}");
            PushCallArgs(n);
            E.Call(Direct(n, sctor));
            Set(n);
            AfterCall(n);
            return;
        }
        if (t.IsDelegate && ctor.Signature.Length == 2 && n.A[1].Op is Opc.LdFtn or Opc.LdVirtFtn)
        {
            // a delegate: its target and the function it calls with it (an instance method's target must not be
            // null: ArgumentException, as the CLR's delegate constructor throws; ldvirtftn already threw for it)
            var fm0 = (MethodDesc)n.A[1].X!;
            if (!fm0.Signature.IsStatic && n.A[0].Kind == VK.Ref && n.A[1].Op == Opc.LdFtn)
            {
                Get(n.A[0]); E.Op(WOp.I32Eqz); E.If(); E.Call(C.DelegateNullThis()); E.End();
            }
            C.Constructed(t);
            E.I32(IlContext.InstanceSize(t)); E.Tid(C.TypeId(t)); E.Call(C.Rt.AllocObject);
            Set(n);
            var target = n.A[0];
            int tOff = IlContext.FieldOffset(IlCompiler.DelegateFieldOf(t, "_target")), fOff = IlContext.FieldOffset(IlCompiler.DelegateFieldOf(t, "_fn"));
            EntryOnly(n);
            // (the target is stored: its count moves in at its last use, else it is counted again)
            bool tMoves = target.Kind == VK.Ref && ArgMoves(n, 0);
            E.Get(L(n)); Get(target); if (target.Kind == VK.Ref && !tMoves) { Rc(n, RcCategory.StoreRetain); E.Call(C.Rt.Retain); } E.Store(WOp.I32Store, tOff);
            if (tMoves) Clear(target);
            var fm = (MethodDesc)n.A[1].X!;
            E.Get(L(n)); E.I32Fn(C.Module, C.DelegateTargetFn(fm, n.A[1].Op == Opc.LdVirtFtn, t)); E.Store(WOp.I32Store, fOff);
            return;
        }
        if (t.IsValueType && n.Kind != VK.Struct)
        {
            // a primitive (IntPtr's constructors): made in a frame slot, then read
            C.CctorCheckForCall(E, ctor);
            int at = _slot[n];
            FrameAddr(at); E.I32(0); E.I32(8); E.Emit(WOp.MemoryFill);
            FrameAddr(at);
            PushCallArgs(n);
            E.Call(Direct(n, ctor));
            AfterCall(n);
            FrameAddr(at);
            E.Load(n.Kind switch { VK.I64 => WOp.I64Load, VK.F32 => WOp.F32Load, VK.F64 => WOp.F64Load, _ => WOp.I32Load });
            Set(n);
            return;
        }
        if (t.IsValueType)
        {
            // a struct: made in its own copy, which the constructor gets the address of
            C.CctorCheckForCall(E, ctor);
            Get(n); E.I32(0); E.I32(SizeOfStruct(t)); E.Emit(WOp.MemoryFill);
            Get(n);
            PushCallArgs(n);
            E.Call(Direct(n, ctor));
            AfterCall(n);
            return;
        }
        C.Constructed(t);
        C.CctorCheckForCall(E, ctor);
        // (2.3: an object that does not outlive the frame is made in it)
        if (_frameObj.ContainsKey(n)) AllocFrame(n);
        else { E.I32(IlContext.InstanceSize(t)); E.Tid(C.TypeId(t)); E.Call(C.Rt.AllocObject); Set(n); }
        E.Get(L(n));
        PushCallArgs(n);
        E.Call(Direct(n, ctor));
        AfterCall(n);
    }
}
