#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Internal.IL;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Ir;

/// <summary>IL to the SSA IR: basic blocks from the branch targets and region boundaries, exception regions as a tree,
/// the evaluation stack simulated with its types checked (ECMA-335 III.1.5), and SSA built as Braun et al.'s
/// construction does (locals, arguments and the stack slots at block boundaries are its variables). IL locals and
/// arguments written inside a protected region or a finally, fault or filter are variables of their own (VarLd and
/// VarSt): an exception can leave the region between any two instructions, so their value at the handler is the one
/// last written, which no SSA value names. Every other variable keeps its try-entry value in the handlers.</summary>
public sealed partial class Importer
{
    readonly MethodDesc _method;
    readonly MethodIL _il;
    readonly byte[] _code;
    readonly Body _body = new();
    readonly TypeSystemContext _ctx;
    readonly int _nArgs, _nLocals;
    readonly TypeDesc[] _argTypes;
    readonly LocalVariableDefinition[] _locals;
    readonly SortedDictionary<int, Block> _blockAt = new();
    readonly Dictionary<Block, int> _blockEnd = new();
    /// <summary>The IL variables (by number) that are VarLd/VarSt variables, with their variable index.</summary>
    readonly Dictionary<int, int> _slotOf = new();
    // SSA construction
    readonly Dictionary<Block, Dictionary<int, Node>> _cur = new();
    readonly Dictionary<Block, List<(int v, Node phi)>> _incomplete = new();
    readonly HashSet<Block> _sealed = [], _filled = [];
    readonly Dictionary<Block, List<(VK k, StackType s, TypeDesc? t)>> _entryStack = new();
    readonly Dictionary<Block, int> _handlerOf = new();
    Block _entry = null!;

    public static Body Import(MethodDesc method, MethodIL il) => new Importer(method, il).Run();

    Importer(MethodDesc method, MethodIL il)
    {
        _method = method; _il = il; _code = il.GetILBytes(); _ctx = method.Context;
        _body.Method = method;
        var sig = method.Signature;
        bool hasThis = !sig.IsStatic;
        _nArgs = sig.Length + (hasThis ? 1 : 0);
        _argTypes = new TypeDesc[_nArgs];
        if (hasThis) _argTypes[0] = method.OwningType.IsValueType ? method.OwningType.MakeByRefType() : method.OwningType;
        for (int i = 0; i < sig.Length; i++) _argTypes[i + (hasThis ? 1 : 0)] = sig[i];
        _locals = il.GetLocals();
        _nLocals = _locals.Length;
    }

    TypeDesc VarType(int v) => v < _nArgs ? _argTypes[v] : _locals[v - _nArgs].Type;

    Node NewNode(Opc op, VK kind, Block b, params Node[] a) => new() { Op = op, Kind = kind, A = a, Block = b, Id = _body.NextId() };

    Body Run()
    {
        FindBlocks();
        BuildRegions();
        FindSlots();
        LinkBlocks();
        Fill();
        RemoveTrivialPhis();
        _body.Blocks.RemoveAll(b => !_filled.Contains(b));
        foreach (var b in _body.Blocks) b.Region.Blocks.Add(b);
        for (int i = 0; i < _body.Blocks.Count; i++) _body.Blocks[i].Id = i;
        return _body;
    }

    // ---------------------------------------------------------------- blocks

    static bool IsBranch(ILOpcode op) => op is >= ILOpcode.br_s and <= ILOpcode.blt_un or ILOpcode.leave or ILOpcode.leave_s;
    static bool EndsBlock(ILOpcode op) => IsBranch(op) || op is ILOpcode.switch_ or ILOpcode.ret or ILOpcode.throw_ or ILOpcode.rethrow
        or ILOpcode.endfinally or ILOpcode.endfilter or ILOpcode.jmp;

    Block BlockAt(int off)
    {
        if (!_blockAt.TryGetValue(off, out var b)) { b = new Block { ILStart = off }; _blockAt[off] = b; }
        return b;
    }

    void FindBlocks()
    {
        BlockAt(0);
        foreach (var r in _il.GetExceptionRegions())
        {
            BlockAt(r.TryOffset); BlockAt(r.TryOffset + r.TryLength); BlockAt(r.HandlerOffset); BlockAt(r.HandlerOffset + r.HandlerLength);
            if (r.Kind == ILExceptionRegionKind.Filter) BlockAt(r.FilterOffset);
        }
        var rd = new ILReader(_code);
        while (rd.HasNext)
        {
            var op = rd.ReadILOpcode();
            if (IsBranch(op)) BlockAt(rd.ReadBranchDestination(op));
            else if (op == ILOpcode.switch_)
            {
                uint n = rd.ReadILUInt32();
                int baseOff = rd.Offset + (int)n * 4;
                for (uint i = 0; i < n; i++) BlockAt(baseOff + (int)rd.ReadILUInt32());
            }
            else rd.Skip(op);
            if (EndsBlock(op) && rd.HasNext) BlockAt(rd.Offset);
        }
        _blockAt.Remove(_code.Length);
        var starts = _blockAt.Keys.ToList();
        for (int i = 0; i < starts.Count; i++) _blockEnd[_blockAt[starts[i]]] = i + 1 < starts.Count ? starts[i + 1] : _code.Length;
        _entry = new Block { ILStart = -1 };
        _body.Blocks.Add(_entry);
        _body.Blocks.AddRange(_blockAt.Values);
    }

    // ---------------------------------------------------------------- regions

    void BuildRegions()
    {
        _body.Root = new Region { Kind = RegionKind.Root, Start = 0, End = _code.Length };
        var all = new List<Region>();
        var tries = new Dictionary<(int, int), Region>();
        foreach (var c in _il.GetExceptionRegions())
        {
            if (!tries.TryGetValue((c.TryOffset, c.TryLength), out var t))
            {
                t = new Region { Kind = RegionKind.Try, Start = c.TryOffset, End = c.TryOffset + c.TryLength };
                tries[(c.TryOffset, c.TryLength)] = t;
                all.Add(t);
            }
            var h = new Region
            {
                Kind = c.Kind switch
                {
                    ILExceptionRegionKind.Catch => RegionKind.Catch, ILExceptionRegionKind.Filter => RegionKind.Catch,
                    ILExceptionRegionKind.Finally => RegionKind.Finally, _ => RegionKind.Fault,
                },
                Start = c.HandlerOffset, End = c.HandlerOffset + c.HandlerLength, Try = t,
            };
            if (c.Kind == ILExceptionRegionKind.Catch) h.CatchType = (TypeDesc)_il.GetObject(c.ClassToken);
            t.Handlers.Add(h);
            all.Add(h);
            if (c.Kind == ILExceptionRegionKind.Filter)
            {
                var f = new Region { Kind = RegionKind.Filter, Start = c.FilterOffset, End = c.HandlerOffset, Try = t };
                h.Filter = f;
                all.Add(f);
            }
        }
        // nesting by containment: outer (earlier start, longer) first
        all.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : (b.End - b.Start).CompareTo(a.End - a.Start));
        var stack = new List<Region> { _body.Root };
        foreach (var r in all)
        {
            while (!(stack[^1].Start <= r.Start && r.End <= stack[^1].End)) stack.RemoveAt(stack.Count - 1);
            if (r.End > stack[^1].End) throw new InvalidProgramException($"{_method}: exception regions overlap");
            r.Parent = stack[^1];
            stack[^1].Children.Add(r);
            stack.Add(r);
        }
        _body.Regions.Add(_body.Root);
        _body.Regions.AddRange(all);
        _entry.Region = _body.Root;
        foreach (var b in _blockAt.Values) b.Region = Innermost(b.ILStart);
        foreach (var r in all)
        {
            r.Entry = _blockAt[r.Start];
            if (r.Entry.Region != r && !r.Contains(r.Entry.Region)) throw new InvalidProgramException($"{_method}: region at {r.Start:x} has no entry");
        }
        foreach (var r in all) if (r.IsHandler || r.Kind == RegionKind.Filter) _handlerOf[r.Entry] = 0;
    }

    Region Innermost(int off)
    {
        var r = _body.Root;
        for (bool deeper = true; deeper;)
        {
            deeper = false;
            foreach (var c in r.Children) if (c.Start <= off && off < c.End) { r = c; deeper = true; break; }
        }
        return r;
    }

    /// <summary>The variables that cannot be SSA values: written inside a try body (an exception can leave it between
    /// any two of its instructions) or inside a finally, fault or filter (they run on paths SSA does not see).</summary>
    void FindSlots()
    {
        // struct locals and arguments are variables (in the frame; an argument in the caller's copy)
        for (int v = 0; v < _nArgs + _nLocals; v++)
        {
            if (!Kinds.IsStruct(VarType(v))) continue;
            _slotOf[v] = _body.Vars.Count;
            _body.Vars.Add(new Var { Kind = VK.Struct, Type = VarType(v), InFrame = v >= _nArgs, ArgIndex = v < _nArgs ? v : -1 });
        }
        var rd = new ILReader(_code);
        while (rd.HasNext)
        {
            int at = rd.Offset;
            var op = rd.ReadILOpcode();
            int v = -1;
            bool addr = false;
            switch (op)
            {
                case ILOpcode.stloc_0: case ILOpcode.stloc_1: case ILOpcode.stloc_2: case ILOpcode.stloc_3: v = _nArgs + (op - ILOpcode.stloc_0); break;
                case ILOpcode.stloc_s: v = _nArgs + rd.ReadILByte(); break;
                case ILOpcode.stloc: v = _nArgs + rd.ReadILUInt16(); break;
                case ILOpcode.starg_s: v = rd.ReadILByte(); break;
                case ILOpcode.starg: v = rd.ReadILUInt16(); break;
                case ILOpcode.ldloca_s: v = _nArgs + rd.ReadILByte(); addr = true; break;
                case ILOpcode.ldloca: v = _nArgs + rd.ReadILUInt16(); addr = true; break;
                case ILOpcode.ldarga_s: v = rd.ReadILByte(); addr = true; break;
                case ILOpcode.ldarga: v = rd.ReadILUInt16(); addr = true; break;
                default: rd.Skip(op); break;
            }
            if (v < 0) continue;
            if (addr)
            {
                if (!_slotOf.TryGetValue(v, out int si)) { _slotOf[v] = si = _body.Vars.Count; _body.Vars.Add(new Var { Kind = Kinds.Of(VarType(v)), Type = VarType(v) }); }
                if (_body.Vars[si].ArgIndex < 0) _body.Vars[si].InFrame = true;
                continue;
            }
            bool protectedWrite = false;
            for (var r = Innermost(at); r != null; r = r.Parent)
                if (r.Kind is RegionKind.Try or RegionKind.Finally or RegionKind.Fault or RegionKind.Filter) { protectedWrite = true; break; }
            if (protectedWrite && !_slotOf.ContainsKey(v))
            {
                _slotOf[v] = _body.Vars.Count;
                _body.Vars.Add(new Var { Kind = Kinds.Of(VarType(v)), Type = VarType(v) });
            }
        }
        // il-runtime: what a filter uses is in the frame (the filter runs as a function of its own: FilterSlots.cs)
        FilterSlots();
        // il-runtime: a pinned local holds its object (Pinning.cs)
        PinSlots();
    }

    // ---------------------------------------------------------------- the control flow graph

    void LinkBlocks()
    {
        void Edge(Block a, Block b) { a.Succs.Add(b); b.Preds.Add(a); }
        Edge(_entry, _blockAt[0]);
        foreach (var b in _blockAt.Values)
        {
            var rd = new ILReader(_code, b.ILStart);
            int end = _blockEnd[b];
            ILOpcode last = ILOpcode.nop;
            int lastAt = b.ILStart;
            int prevAt = -1;
            while (rd.Offset < end) { prevAt = lastAt; lastAt = rd.Offset; last = rd.ReadILOpcode(); rd.Skip(last); }
            rd.Seek(lastAt);
            rd.ReadILOpcode();
            if (IsBranch(last))
            {
                var t = _blockAt[rd.ReadBranchDestination(last)];
                if (last is ILOpcode.br or ILOpcode.br_s or ILOpcode.leave or ILOpcode.leave_s) Edge(b, t);
                else if (last is ILOpcode.brtrue or ILOpcode.brtrue_s or ILOpcode.brfalse or ILOpcode.brfalse_s && prevAt >= b.ILStart && ConstantCallAt(prevAt) is long c)
                {
                    // a branch on a constant getter (IsSupported, IsHardwareAccelerated: CoreLib's substitutions): the
                    // other way is never compiled
                    bool taken = (c != 0) == (last is ILOpcode.brtrue or ILOpcode.brtrue_s);
                    Edge(b, taken ? t : _blockAt[end]);
                }
                else { Edge(b, t); Edge(b, _blockAt[end]); }
            }
            else if (last == ILOpcode.switch_)
            {
                uint n = rd.ReadILUInt32();
                int baseOff = rd.Offset + (int)n * 4;
                for (uint i = 0; i < n; i++) Edge(b, _blockAt[baseOff + (int)rd.ReadILUInt32()]);
                Edge(b, _blockAt[end]);
            }
            else if (!EndsBlock(last))
            {
                if (end >= _code.Length) throw new InvalidProgramException($"{_method}: control falls off the end of the method");
                Edge(b, _blockAt[end]);
            }
        }
    }

    /// <summary>The value of a call at this offset to a constant getter, or null.</summary>
    long? ConstantCallAt(int at)
    {
        var rd = new ILReader(_code, at);
        if (rd.ReadILOpcode() != ILOpcode.call) return null;
        return _il.GetObject(rd.ReadILToken()) is MethodDesc m ? ConstantGetter(m) : null;
    }

    /// <summary>A static method of no arguments whose body is a constant and ret (what a substitution makes of a feature
    /// switch, IsSupported, IsHardwareAccelerated), on a type whose initializer such a call cannot start: its value.</summary>
    public static long? ConstantGetter(MethodDesc m)
    {
        if (!m.Signature.IsStatic || m.Signature.Length != 0 || m.HasInstantiation && m.IsCanonicalMethod(CanonicalFormKind.Any)) return null;
        if (Constants.OfIntrinsic(m) is long k) return k;
        if (Constants.Known(m) is long kn) return kn;
        if (!(m.Signature.ReturnType.IsWellKnownType(WellKnownType.Boolean) || m.Signature.ReturnType.IsWellKnownType(WellKnownType.Int32))) return null;
        if (m.OwningType is MetadataType ot && ot.HasStaticConstructor && !ot.IsBeforeFieldInit) return null;
        if (m.GetTypicalMethodDefinition() is not Internal.TypeSystem.Ecma.EcmaMethod em || em.IsPInvoke || em.IsInternalCall || em.IsAbstract) return null;
        if (em.HasCustomAttribute("Il2Wasm.Runtime", "IntrinsicAttribute")) return null;
        var il = Internal.IL.EcmaMethodIL.Create(em);
        if (il == null) return null;
        var b = il.GetILBytes();
        if (b.Length == 2 && b[1] == (byte)ILOpcode.ret && b[0] >= (byte)ILOpcode.ldc_i4_m1 && b[0] <= (byte)ILOpcode.ldc_i4_8) return b[0] - (byte)ILOpcode.ldc_i4_0;
        if (b.Length == 3 && b[0] == (byte)ILOpcode.ldc_i4_s && b[2] == (byte)ILOpcode.ret) return (sbyte)b[1];
        return null;
    }

    /// <summary>Reverse postorder over the flow edges, with each handler entered from its try's entry.</summary>
    List<Block> Order()
    {
        var handlerEdges = new Dictionary<Block, List<Block>>();
        foreach (var r in _body.Regions)
        {
            if (!(r.IsHandler || r.Kind == RegionKind.Filter)) continue;
            var from = r.Try!.Entry;
            if (!handlerEdges.TryGetValue(from, out var l)) handlerEdges[from] = l = [];
            l.Add(r.Kind == RegionKind.Catch && r.Filter != null ? r.Filter.Entry : r.Entry);
            if (r.Filter != null) l.Add(r.Entry);
        }
        var seen = new HashSet<Block>();
        var post = new List<Block>();
        var stack = new Stack<(Block b, int i)>();
        stack.Push((_entry, 0)); seen.Add(_entry);
        while (stack.Count > 0)
        {
            var (b, i) = stack.Pop();
            var next = b.Succs.Concat(handlerEdges.GetValueOrDefault(b) ?? []).ToList();
            if (i < next.Count)
            {
                stack.Push((b, i + 1));
                var s = next[i];
                if (seen.Add(s)) stack.Push((s, 0));
            }
            else post.Add(b);
        }
        post.Reverse();
        return post;
    }

    // ---------------------------------------------------------------- SSA

    int StackVar(int i) => _nArgs + _nLocals + i;

    VK KindOfVar(int v, Block b, out StackType st, out TypeDesc? t)
    {
        if (v < _nArgs + _nLocals) { t = VarType(v); st = Kinds.StackOf(t); return Kinds.Of(t); }
        var e = _entryStack[b][v - _nArgs - _nLocals];
        st = e.s; t = e.t; return e.k;
    }

    void Write(int v, Block b, Node n)
    {
        if (!_cur.TryGetValue(b, out var d)) _cur[b] = d = new();
        d[v] = n;
    }

    Node Read(int v, Block b)
    {
        if (_cur.TryGetValue(b, out var d) && d.TryGetValue(v, out var n)) return n;
        return ReadRec(v, b);
    }

    Node ReadRec(int v, Block b)
    {
        Node val;
        if (_handlerOf.ContainsKey(b))
        {
            // a handler sees what its try saw on entry (nothing it reads is written inside the try: FindSlots)
            var r = _body.Regions.First(x => x.Entry == b && (x.IsHandler || x.Kind == RegionKind.Filter));
            val = Read(v, r.Try!.Entry);
        }
        else if (b == _entry) val = Initial(v);
        else if (!_sealed.Contains(b))
        {
            val = NewPhi(v, b);
            if (!_incomplete.TryGetValue(b, out var l)) _incomplete[b] = l = [];
            l.Add((v, val));
        }
        else if (b.Preds.Count == 1) val = Read(v, b.Preds[0]);
        else
        {
            val = NewPhi(v, b);
            Write(v, b, val);
            AddPhiOperands(v, val);
        }
        Write(v, b, val);
        return val;
    }

    Node NewPhi(int v, Block b)
    {
        var k = KindOfVar(v, b, out var st, out var t);
        var phi = NewNode(Opc.Phi, k, b);
        phi.SType = st; phi.Type = t;
        b.Phis.Add(phi);
        return phi;
    }

    void AddPhiOperands(int v, Node phi)
    {
        var b = phi.Block;
        phi.A = new Node[b.Preds.Count];
        for (int i = 0; i < b.Preds.Count; i++) phi.A[i] = _filled.Contains(b.Preds[i]) || b.Preds[i] == _entry ? Read(v, b.Preds[i]) : null!;
    }

    readonly Dictionary<int, Node> _initial = new();
    Node Initial(int v)
    {
        if (_initial.TryGetValue(v, out var n)) return n;
        if (v < _nArgs) n = _body.Args[v];
        else if (v < _nArgs + _nLocals) n = Zero(VarType(v), _entry);
        else throw new InvalidProgramException($"{_method}: a stack slot read on entry");
        _initial[v] = n;
        return n;
    }

    Node Zero(TypeDesc t, Block b)
    {
        var k = Kinds.Of(t);
        var n = NewNode(k == VK.Ref ? Opc.LdNull : Opc.Const, k, b);
        n.SType = Kinds.StackOf(t); n.Type = t;
        b.Code.Insert(0, n);
        return n;
    }

    void Seal(Block b)
    {
        if (!_sealed.Add(b)) return;
        if (_incomplete.Remove(b, out var l))
            foreach (var (v, phi) in l) AddPhiOperands(v, phi);
    }

    readonly Dictionary<Node, Node> _replaced = new();
    Node Resolve(Node n) { while (_replaced.TryGetValue(n, out var r)) n = r; return n; }

    void RemoveTrivialPhis()
    {
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var b in _body.Blocks)
                foreach (var phi in b.Phis.ToList())
                {
                    if (_replaced.ContainsKey(phi)) continue;
                    Node? same = null;
                    bool trivial = true;
                    foreach (var a0 in phi.A)
                    {
                        if (a0 == null) continue;
                        var a = Resolve(a0);
                        if (a == phi || a == same) continue;
                        if (same != null) { trivial = false; break; }
                        same = a;
                    }
                    if (trivial && same != null) { _replaced[phi] = same; b.Phis.Remove(phi); changed = true; }
                }
        }
        foreach (var b in _body.Blocks)
        {
            foreach (var n in b.Phis) for (int i = 0; i < n.A.Length; i++) n.A[i] = Resolve(n.A[i]);
            foreach (var n in b.Code) for (int i = 0; i < n.A.Length; i++) n.A[i] = Resolve(n.A[i]);
        }
    }

    // ---------------------------------------------------------------- filling the blocks

    void Fill()
    {
        // the arguments: values of the entry block
        _body.Args = new Node[_nArgs];
        for (int i = 0; i < _nArgs; i++)
        {
            var t = _argTypes[i];
            // (a struct argument is the address of the caller's copy)
            var a = NewNode(Opc.Arg, Kinds.Of(t) == VK.Struct ? VK.Ptr : Kinds.Of(t), _entry);
            a.I = i; a.Type = t; a.SType = Kinds.StackOf(t);
            _body.Args[i] = a;
            _entry.Code.Add(a);
        }
        // slot variables start with their argument's value
        foreach (var (v, slot) in _slotOf)
            if (v < _nArgs && _body.Vars[slot].ArgIndex < 0) { var st = NewNode(Opc.VarSt, VK.Void, _entry, _body.Args[v]); st.I = slot; _entry.Code.Add(st); }
        var br = NewNode(Opc.Br, VK.Void, _entry);
        _entry.Code.Add(br);
        _filled.Add(_entry); _sealed.Add(_entry);
        var order = Order();
        var reachable = order.ToHashSet();
        foreach (var b in _body.Blocks) b.Preds.RemoveAll(p => !reachable.Contains(p));
        foreach (var b in order)
        {
            if (b == _entry) continue;
            if (b.Preds.All(p => _filled.Contains(p))) Seal(b);
            FillBlock(b);
            _filled.Add(b);
            foreach (var s in b.Succs) if (s.Preds.All(p => _filled.Contains(p))) Seal(s);
        }
        foreach (var b in order) Seal(b);
    }

    void FillBlock(Block b)
    {
        var stack = new List<Node>();
        var reg = b.Region;
        if (_handlerOf.ContainsKey(b) && reg.Entry == b && reg.Kind is RegionKind.Catch or RegionKind.Filter
            || reg.Kind == RegionKind.Catch && reg.Filter == null && false)
        {
            var cv = NewNode(Opc.CatchVal, VK.Ref, b);
            cv.Type = reg.CatchType ?? _ctx.GetWellKnownType(WellKnownType.Object);
            cv.SType = StackType.ObjRef;
            b.Code.Add(cv);
            stack.Add(cv);
        }
        else if (_entryStack.TryGetValue(b, out var shape))
            for (int i = 0; i < shape.Count; i++) stack.Add(Read(StackVar(i), b));
        new BlockImporter(this, b, stack).Run();
    }

    /// <summary>The stack at a block's end, handed to its successors as the stack variables.</summary>
    void EndStack(Block b, List<Node> stack, IEnumerable<Block> succs)
    {
        for (int i = 0; i < stack.Count; i++) Write(StackVar(i), b, stack[i]);
        foreach (var s in succs)
        {
            var shape = stack.Select(n => (n.Kind, n.SType, n.Type)).ToList();
            if (_entryStack.TryGetValue(s, out var have))
            {
                if (have.Count != shape.Count) throw new InvalidProgramException($"{_method}: stack depths differ at IL_{s.ILStart:x4}");
                for (int i = 0; i < shape.Count; i++)
                {
                    if (have[i].k != shape[i].Kind) throw new InvalidProgramException($"{_method}: stack types differ at IL_{s.ILStart:x4}: {have[i].k} and {shape[i].Kind}");
                    // (a join of two references: their common type is not tracked; object is safe)
                    if (have[i].t != shape[i].Type) have[i] = (have[i].k, have[i].s, have[i].k == VK.Ref ? _ctx.GetWellKnownType(WellKnownType.Object) : have[i].t);
                }
            }
            else _entryStack[s] = shape;
        }
    }

    // ---------------------------------------------------------------- one block's instructions

    sealed partial class BlockImporter
    {
        readonly Importer I;
        readonly Block B;
        readonly List<Node> S;
        readonly TypeSystemContext Ctx;
        TypeDesc? _constrained;
        /// <summary>readonly. before an ldelema: the address is not written through, so no exact-type check.</summary>
        bool _readonly;

        public BlockImporter(Importer imp, Block b, List<Node> stack) { I = imp; B = b; S = stack; Ctx = imp._ctx; }

        Node Pop()
        {
            if (S.Count == 0) throw new InvalidProgramException($"{I._method}: stack underflow at IL_{_at:x4}");
            var n = S[^1]; S.RemoveAt(S.Count - 1); return n;
        }
        void Push(Node n) => S.Add(n);
        int _at;

        Node Add(Opc op, VK k, StackType st, TypeDesc? t, params Node[] a)
        {
            var n = I.NewNode(op, k, B, a);
            n.SType = st; n.Type = t; n.ILOffset = _at;
            B.Code.Add(n);
            return n;
        }

        Node AddOfType(Opc op, TypeDesc t, params Node[] a) => Add(op, Kinds.Of(t), Kinds.StackOf(t), t, a);

        Node Const(VK k, long v) => Add(Opc.Const, k, k == VK.I64 ? StackType.Int64 : StackType.Int32, Ctx.GetWellKnownType(k == VK.I64 ? WellKnownType.Int64 : WellKnownType.Int32)).WithI(v);
        Node ConstF(VK k, double v) { var n = Add(Opc.Const, k, StackType.Float, Ctx.GetWellKnownType(k == VK.F32 ? WellKnownType.Single : WellKnownType.Double)); n.D = v; return n; }

        Node LoadVar(int v)
        {
            if (I._slotOf.TryGetValue(v, out int slot)) { var n = AddOfType(Opc.VarLd, I.VarType(v)); n.I = slot; return n; }
            return I.Read(v, B);
        }

        Node Address(int v)
        {
            var t = I.VarType(v);
            var n = Add(Opc.LocA, VK.Ptr, StackType.ByRef, t.MakeByRefType());
            n.I = I._slotOf[v];
            return n;
        }

        void StoreVar(int v, Node val)
        {
            val = Coerce(val, I.VarType(v));
            Pin(v, val); // il-runtime: Pinning.cs
            if (I._slotOf.TryGetValue(v, out int slot)) { var n = Add(Opc.VarSt, VK.Void, StackType.None, null, val); n.I = slot; return; }
            I.Write(v, B, val);
        }

        /// <summary>A stored value as the destination's type holds it (int32 and native int are both i32 here; a
        /// float's precision is the destination's).</summary>
        Node Coerce(Node val, TypeDesc to)
        {
            var k = Kinds.Of(to);
            if (val.Kind == k || (k is VK.I32 or VK.Ptr && val.Kind is VK.I32 or VK.Ptr)) return val;
            if (k is VK.F32 or VK.F64 && val.Kind is VK.F32 or VK.F64) return Conv(val, k, 0, false, false, false);
            if (k == VK.Ref && val.Op == Opc.LdNull) return val;
            throw new InvalidProgramException($"{I._method}: a {val.Kind} value stored as {to} at IL_{_at:x4}");
        }

        Node Conv(Node v, VK to, byte small, bool unsignedTarget, bool fromUnsigned, bool ovf)
        {
            var st = to switch { VK.I64 => StackType.Int64, VK.F32 or VK.F64 => StackType.Float, VK.Ptr => StackType.NativeInt, _ => StackType.Int32 };
            var n = Add(Opc.Conv, to == VK.Ptr ? VK.Ptr : to, st, null, v);
            n.Sub = (byte)to; n.Small = small; n.Unsigned = unsignedTarget; n.I = fromUnsigned ? 1 : 0; n.Ovf = ovf;
            return n;
        }

        void Terminate(Opc op, params Node[] a)
        {
            var n = I.NewNode(op, VK.Void, B, a);
            n.ILOffset = _at;
            B.Code.Add(n);
        }

        public void Run()
        {
            var rd = new ILReader(I._code, B.ILStart);
            int end = I._blockEnd[B];
            while (rd.Offset < end)
            {
                _at = rd.Offset;
                var op = rd.ReadILOpcode();
                if (!One(ref rd, op)) return;
            }
            // fell through to the next block
            I.EndStack(B, S, B.Succs);
            Terminate(Opc.Br);
        }

        /// <summary>One instruction; false when it ended the block.</summary>
        bool One(ref ILReader rd, ILOpcode op)
        {
            switch (op)
            {
                case ILOpcode.nop: case ILOpcode.break_: return true;
                case ILOpcode.volatile_: case ILOpcode.tail: return true;
                case ILOpcode.readonly_: _readonly = true; return true;
                case ILOpcode.unaligned: rd.ReadILByte(); return true;
                case ILOpcode.no: rd.ReadILByte(); return true;
                case ILOpcode.constrained: _constrained = (TypeDesc)I._il.GetObject(rd.ReadILToken()); return true;

                case ILOpcode.ldarg_0: case ILOpcode.ldarg_1: case ILOpcode.ldarg_2: case ILOpcode.ldarg_3: Push(LoadVar(op - ILOpcode.ldarg_0)); return true;
                case ILOpcode.ldarg_s: Push(LoadVar(rd.ReadILByte())); return true;
                case ILOpcode.ldarg: Push(LoadVar(rd.ReadILUInt16())); return true;
                case ILOpcode.ldarga_s: Push(Address(rd.ReadILByte())); return true;
                case ILOpcode.ldarga: Push(Address(rd.ReadILUInt16())); return true;
                case ILOpcode.ldloca_s: Push(Address(I._nArgs + rd.ReadILByte())); return true;
                case ILOpcode.ldloca: Push(Address(I._nArgs + rd.ReadILUInt16())); return true;
                case ILOpcode.starg_s: StoreVar(rd.ReadILByte(), Pop()); return true;
                case ILOpcode.starg: StoreVar(rd.ReadILUInt16(), Pop()); return true;
                case ILOpcode.ldloc_0: case ILOpcode.ldloc_1: case ILOpcode.ldloc_2: case ILOpcode.ldloc_3: Push(LoadVar(I._nArgs + (op - ILOpcode.ldloc_0))); return true;
                case ILOpcode.ldloc_s: Push(LoadVar(I._nArgs + rd.ReadILByte())); return true;
                case ILOpcode.ldloc: Push(LoadVar(I._nArgs + rd.ReadILUInt16())); return true;
                case ILOpcode.stloc_0: case ILOpcode.stloc_1: case ILOpcode.stloc_2: case ILOpcode.stloc_3: StoreVar(I._nArgs + (op - ILOpcode.stloc_0), Pop()); return true;
                case ILOpcode.stloc_s: StoreVar(I._nArgs + rd.ReadILByte(), Pop()); return true;
                case ILOpcode.stloc: StoreVar(I._nArgs + rd.ReadILUInt16(), Pop()); return true;

                case ILOpcode.ldnull: Push(Add(Opc.LdNull, VK.Ref, StackType.ObjRef, Ctx.GetWellKnownType(WellKnownType.Object))); return true;
                case ILOpcode.ldc_i4_m1: case ILOpcode.ldc_i4_0: case ILOpcode.ldc_i4_1: case ILOpcode.ldc_i4_2: case ILOpcode.ldc_i4_3:
                case ILOpcode.ldc_i4_4: case ILOpcode.ldc_i4_5: case ILOpcode.ldc_i4_6: case ILOpcode.ldc_i4_7: case ILOpcode.ldc_i4_8:
                    Push(Const(VK.I32, op - ILOpcode.ldc_i4_0)); return true;
                case ILOpcode.ldc_i4_s: Push(Const(VK.I32, (sbyte)rd.ReadILByte())); return true;
                case ILOpcode.ldc_i4: Push(Const(VK.I32, (int)rd.ReadILUInt32())); return true;
                case ILOpcode.ldc_i8: Push(Const(VK.I64, (long)rd.ReadILUInt64())); return true;
                case ILOpcode.ldc_r4: Push(ConstF(VK.F32, rd.ReadILFloat())); return true;
                case ILOpcode.ldc_r8: Push(ConstF(VK.F64, rd.ReadILDouble())); return true;
                case ILOpcode.ldstr: Push(Add(Opc.LdStr, VK.Ref, StackType.ObjRef, Ctx.GetWellKnownType(WellKnownType.String)).WithX(I._il.GetObject(rd.ReadILToken()))); return true;
                case ILOpcode.dup: { var v = Pop(); Push(v); Push(v); return true; }
                case ILOpcode.pop: Pop(); return true;

                case ILOpcode.add: case ILOpcode.sub: case ILOpcode.mul: case ILOpcode.div: case ILOpcode.div_un: case ILOpcode.rem: case ILOpcode.rem_un:
                case ILOpcode.and: case ILOpcode.or: case ILOpcode.xor:
                    Binary((BinOp)(op - ILOpcode.add), false); return true;
                case ILOpcode.shl: case ILOpcode.shr: case ILOpcode.shr_un: Shift((BinOp)((int)BinOp.Shl + (op - ILOpcode.shl))); return true;
                case ILOpcode.add_ovf: Binary(BinOp.Add, true); return true;
                case ILOpcode.add_ovf_un: Binary(BinOp.Add, true, true); return true;
                case ILOpcode.sub_ovf: Binary(BinOp.Sub, true); return true;
                case ILOpcode.sub_ovf_un: Binary(BinOp.Sub, true, true); return true;
                case ILOpcode.mul_ovf: Binary(BinOp.Mul, true); return true;
                case ILOpcode.mul_ovf_un: Binary(BinOp.Mul, true, true); return true;
                case ILOpcode.neg: case ILOpcode.not:
                {
                    var v = Pop();
                    if (v.SType is not (StackType.Int32 or StackType.Int64 or StackType.NativeInt or StackType.Float) || (op == ILOpcode.not && v.SType == StackType.Float))
                        throw Invalid($"{op} of a {v.SType}");
                    var n = Add(Opc.Un, v.Kind, v.SType, v.Type, v); n.Sub = (byte)(op == ILOpcode.neg ? UnOp.Neg : UnOp.Not); Push(n); return true;
                }
                case ILOpcode.ceq: Compare(CmpOp.Eq); return true;
                case ILOpcode.cgt: Compare(CmpOp.Gt); return true;
                case ILOpcode.cgt_un: Compare(CmpOp.GtUn); return true;
                case ILOpcode.clt: Compare(CmpOp.Lt); return true;
                case ILOpcode.clt_un: Compare(CmpOp.LtUn); return true;
                case ILOpcode.ckfinite: { var v = Pop(); Push(Add(Opc.CheckFinite, v.Kind, v.SType, v.Type, v)); return true; }

                case ILOpcode.conv_i1: Push(Conv(Pop(), VK.I32, 1, false, false, false)); return true;
                case ILOpcode.conv_i2: Push(Conv(Pop(), VK.I32, 2, false, false, false)); return true;
                case ILOpcode.conv_i4: Push(Conv(Pop(), VK.I32, 4, false, false, false)); return true;
                case ILOpcode.conv_i8: Push(Conv(Pop(), VK.I64, 8, false, false, false)); return true;
                case ILOpcode.conv_u1: Push(Conv(Pop(), VK.I32, 1, true, false, false)); return true;
                case ILOpcode.conv_u2: Push(Conv(Pop(), VK.I32, 2, true, false, false)); return true;
                case ILOpcode.conv_u4: Push(Conv(Pop(), VK.I32, 4, true, false, false)); return true;
                case ILOpcode.conv_u8: Push(Conv(Pop(), VK.I64, 8, true, false, false)); return true;
                case ILOpcode.conv_i: Push(Conv(Pop(), VK.Ptr, 4, false, false, false)); return true;
                case ILOpcode.conv_u: Push(Conv(Pop(), VK.Ptr, 4, true, false, false)); return true;
                case ILOpcode.conv_r4: Push(Conv(Pop(), VK.F32, 0, false, false, false)); return true;
                case ILOpcode.conv_r8: Push(Conv(Pop(), VK.F64, 0, false, false, false)); return true;
                case ILOpcode.conv_r_un: Push(Conv(Pop(), VK.F64, 0, false, true, false)); return true;
                case ILOpcode.conv_ovf_i1: Push(Conv(Pop(), VK.I32, 1, false, false, true)); return true;
                case ILOpcode.conv_ovf_i2: Push(Conv(Pop(), VK.I32, 2, false, false, true)); return true;
                case ILOpcode.conv_ovf_i4: Push(Conv(Pop(), VK.I32, 4, false, false, true)); return true;
                case ILOpcode.conv_ovf_i8: Push(Conv(Pop(), VK.I64, 8, false, false, true)); return true;
                case ILOpcode.conv_ovf_u1: Push(Conv(Pop(), VK.I32, 1, true, false, true)); return true;
                case ILOpcode.conv_ovf_u2: Push(Conv(Pop(), VK.I32, 2, true, false, true)); return true;
                case ILOpcode.conv_ovf_u4: Push(Conv(Pop(), VK.I32, 4, true, false, true)); return true;
                case ILOpcode.conv_ovf_u8: Push(Conv(Pop(), VK.I64, 8, true, false, true)); return true;
                case ILOpcode.conv_ovf_i: Push(Conv(Pop(), VK.Ptr, 4, false, false, true)); return true;
                case ILOpcode.conv_ovf_u: Push(Conv(Pop(), VK.Ptr, 4, true, false, true)); return true;
                case ILOpcode.conv_ovf_i1_un: Push(Conv(Pop(), VK.I32, 1, false, true, true)); return true;
                case ILOpcode.conv_ovf_i2_un: Push(Conv(Pop(), VK.I32, 2, false, true, true)); return true;
                case ILOpcode.conv_ovf_i4_un: Push(Conv(Pop(), VK.I32, 4, false, true, true)); return true;
                case ILOpcode.conv_ovf_i8_un: Push(Conv(Pop(), VK.I64, 8, false, true, true)); return true;
                case ILOpcode.conv_ovf_u1_un: Push(Conv(Pop(), VK.I32, 1, true, true, true)); return true;
                case ILOpcode.conv_ovf_u2_un: Push(Conv(Pop(), VK.I32, 2, true, true, true)); return true;
                case ILOpcode.conv_ovf_u4_un: Push(Conv(Pop(), VK.I32, 4, true, true, true)); return true;
                case ILOpcode.conv_ovf_u8_un: Push(Conv(Pop(), VK.I64, 8, true, true, true)); return true;
                case ILOpcode.conv_ovf_i_un: Push(Conv(Pop(), VK.Ptr, 4, false, true, true)); return true;
                case ILOpcode.conv_ovf_u_un: Push(Conv(Pop(), VK.Ptr, 4, true, true, true)); return true;

                case ILOpcode.br: case ILOpcode.br_s: case ILOpcode.leave: case ILOpcode.leave_s:
                {
                    rd.ReadBranchDestination(op);
                    if (op is ILOpcode.leave or ILOpcode.leave_s) S.Clear();
                    I.EndStack(B, S, B.Succs);
                    Terminate(op is ILOpcode.leave or ILOpcode.leave_s ? Opc.Leave : Opc.Br);
                    return false;
                }
                case ILOpcode.brfalse: case ILOpcode.brfalse_s: case ILOpcode.brtrue: case ILOpcode.brtrue_s:
                {
                    rd.ReadBranchDestination(op);
                    var v = Pop();
                    Node cond = v;
                    if (op is ILOpcode.brfalse or ILOpcode.brfalse_s) cond = CompareWith(CmpOp.Eq, v, ZeroOf(v));
                    else if (v.Kind is not (VK.I32 or VK.Ptr)) cond = CompareWith(CmpOp.Ne, v, ZeroOf(v));
                    I.EndStack(B, S, B.Succs);
                    // (a branch on a constant getter: LinkBlocks kept only the way taken)
                    if (B.Succs.Count == 1) Terminate(Opc.Br);
                    else Terminate(Opc.BrIf, cond);
                    return false;
                }
                case >= ILOpcode.beq_s and <= ILOpcode.blt_un_s: case >= ILOpcode.beq and <= ILOpcode.blt_un:
                {
                    rd.ReadBranchDestination(op);
                    int k = op >= ILOpcode.beq ? op - ILOpcode.beq : op - ILOpcode.beq_s;
                    var cmp = k switch
                    {
                        0 => CmpOp.Eq, 1 => CmpOp.Ge, 2 => CmpOp.Gt, 3 => CmpOp.Le, 4 => CmpOp.Lt,
                        5 => CmpOp.Ne, 6 => CmpOp.GeUn, 7 => CmpOp.GtUn, 8 => CmpOp.LeUn, _ => CmpOp.LtUn,
                    };
                    var b2 = Pop(); var a2 = Pop();
                    var cond = CompareWith(cmp, a2, b2);
                    I.EndStack(B, S, B.Succs);
                    Terminate(Opc.BrIf, cond);
                    return false;
                }
                case ILOpcode.switch_:
                {
                    uint n = rd.ReadILUInt32();
                    for (uint i = 0; i < n; i++) rd.ReadILUInt32();
                    var v = Pop();
                    if (v.SType is not (StackType.Int32 or StackType.NativeInt)) throw Invalid("switch on a " + v.SType);
                    I.EndStack(B, S, B.Succs);
                    Terminate(Opc.Switch, v);
                    return false;
                }
                case ILOpcode.ret:
                {
                    var rt = I._method.Signature.ReturnType;
                    if (rt.IsVoid) { if (S.Count != 0) throw Invalid("ret with values on the stack"); Terminate(Opc.Ret); }
                    else { var v = Coerce(Pop(), rt); if (S.Count != 0) throw Invalid("ret with values on the stack"); Terminate(Opc.Ret, v); }
                    return false;
                }
                case ILOpcode.throw_:
                {
                    var v = Pop();
                    if (v.Kind != VK.Ref) throw Invalid("throw of a " + v.Kind);
                    Terminate(Opc.Throw, v);
                    return false;
                }
                case ILOpcode.rethrow: Terminate(Opc.Rethrow); return false;
                case ILOpcode.endfinally: S.Clear(); Terminate(Opc.EndFinally); return false;
                case ILOpcode.endfilter: Terminate(Opc.EndFilter, Pop()); return false;

                case ILOpcode.call: case ILOpcode.callvirt: Call((MethodDesc)I._il.GetObject(rd.ReadILToken()), op == ILOpcode.callvirt); return true;
                case ILOpcode.newobj: NewObj((MethodDesc)I._il.GetObject(rd.ReadILToken())); return true;
                case ILOpcode.newarr:
                {
                    var et = (TypeDesc)I._il.GetObject(rd.ReadILToken());
                    var len = Pop();
                    if (len.SType is not (StackType.Int32 or StackType.NativeInt)) throw Invalid("newarr length of a " + len.SType);
                    var at = et.MakeArrayType();
                    Push(Add(Opc.NewArr, VK.Ref, StackType.ObjRef, at, len).WithX(et));
                    return true;
                }
                case ILOpcode.ldlen:
                {
                    var a = Pop();
                    if (a.Kind != VK.Ref) throw Invalid("ldlen of a " + a.Kind);
                    Push(Add(Opc.LdLen, VK.Ptr, StackType.NativeInt, Ctx.GetWellKnownType(WellKnownType.IntPtr), a));
                    return true;
                }
                case >= ILOpcode.ldelem_i1 and <= ILOpcode.ldelem_ref:
                {
                    var idx = Pop(); var arr = Pop();
                    TypeDesc et = op switch
                    {
                        ILOpcode.ldelem_i1 => Ctx.GetWellKnownType(WellKnownType.SByte), ILOpcode.ldelem_u1 => Ctx.GetWellKnownType(WellKnownType.Byte),
                        ILOpcode.ldelem_i2 => Ctx.GetWellKnownType(WellKnownType.Int16), ILOpcode.ldelem_u2 => Ctx.GetWellKnownType(WellKnownType.UInt16),
                        ILOpcode.ldelem_i4 => Ctx.GetWellKnownType(WellKnownType.Int32), ILOpcode.ldelem_u4 => Ctx.GetWellKnownType(WellKnownType.UInt32),
                        ILOpcode.ldelem_i8 => Ctx.GetWellKnownType(WellKnownType.Int64), ILOpcode.ldelem_i => Ctx.GetWellKnownType(WellKnownType.IntPtr),
                        ILOpcode.ldelem_r4 => Ctx.GetWellKnownType(WellKnownType.Single), ILOpcode.ldelem_r8 => Ctx.GetWellKnownType(WellKnownType.Double),
                        _ => arr.Type is ArrayType { IsSzArray: true } at && !at.ElementType.IsValueType ? at.ElementType : Ctx.GetWellKnownType(WellKnownType.Object),
                    };
                    Push(LdElem(arr, idx, et));
                    return true;
                }
                case ILOpcode.ldelema:
                {
                    var et = (TypeDesc)I._il.GetObject(rd.ReadILToken());
                    var idx = Pop(); var arr = Pop();
                    if (arr.Kind != VK.Ref || idx.SType is not (StackType.Int32 or StackType.NativeInt)) throw Invalid("ldelema");
                    var ea = Add(Opc.LdElemA, VK.Ptr, StackType.ByRef, et.MakeByRefType(), arr, idx).WithX(et);
                    // (an address of an element of a reference type: the array must be exactly an array of it,
                    // ArrayTypeMismatchException otherwise, unless readonly. says it is only read; ECMA-335 III.4.10)
                    ea.Ovf = !_readonly && !et.IsValueType && !(et is MetadataType { IsSealed: true } && !et.IsInterface) && !et.IsGenericParameter;
                    _readonly = false;
                    Push(ea);
                    return true;
                }
                case ILOpcode.ldelem: { var et = (TypeDesc)I._il.GetObject(rd.ReadILToken()); var idx = Pop(); var arr = Pop(); Push(LdElem(arr, idx, et)); return true; }
                case >= ILOpcode.stelem_i and <= ILOpcode.stelem_ref:
                {
                    var val = Pop(); var idx = Pop(); var arr = Pop();
                    TypeDesc et = op switch
                    {
                        ILOpcode.stelem_i => Ctx.GetWellKnownType(WellKnownType.IntPtr), ILOpcode.stelem_i1 => Ctx.GetWellKnownType(WellKnownType.SByte),
                        ILOpcode.stelem_i2 => Ctx.GetWellKnownType(WellKnownType.Int16), ILOpcode.stelem_i4 => Ctx.GetWellKnownType(WellKnownType.Int32),
                        ILOpcode.stelem_i8 => Ctx.GetWellKnownType(WellKnownType.Int64), ILOpcode.stelem_r4 => Ctx.GetWellKnownType(WellKnownType.Single),
                        ILOpcode.stelem_r8 => Ctx.GetWellKnownType(WellKnownType.Double), _ => Ctx.GetWellKnownType(WellKnownType.Object),
                    };
                    StElem(arr, idx, val, et, op == ILOpcode.stelem_ref);
                    return true;
                }
                case ILOpcode.stelem: { var et = (TypeDesc)I._il.GetObject(rd.ReadILToken()); var val = Pop(); var idx = Pop(); var arr = Pop(); StElem(arr, idx, val, et, !et.IsValueType); return true; }

                case ILOpcode.ldfld:
                {
                    var f = (FieldDesc)I._il.GetObject(rd.ReadILToken());
                    var o = Pop();
                    if (f.IsStatic) { Push(AddOfType(Opc.LdSFld, f.FieldType).WithX(f)); return true; }
                    if (o.Kind is not (VK.Ref or VK.Ptr or VK.Struct)) throw Invalid("ldfld of a " + o.Kind);
                    Push(AddOfType(Opc.LdFld, f.FieldType, o).WithX(f));
                    return true;
                }
                case ILOpcode.stfld:
                {
                    var f = (FieldDesc)I._il.GetObject(rd.ReadILToken());
                    var v = Pop(); var o = Pop();
                    if (o.Kind != VK.Ref && o.Kind != VK.Ptr) throw Invalid("stfld on a " + o.Kind);
                    Add(Opc.StFld, VK.Void, StackType.None, null, o, Coerce(v, f.FieldType)).WithX(f);
                    return true;
                }
                case ILOpcode.ldflda:
                {
                    var f = (FieldDesc)I._il.GetObject(rd.ReadILToken());
                    var o = Pop();
                    Push(Add(Opc.LdFldA, VK.Ptr, StackType.ByRef, f.FieldType.MakeByRefType(), o).WithX(f));
                    return true;
                }
                case ILOpcode.ldsfld when I._il.GetObject(new ILReader(I._code, rd.Offset).ReadILToken()) is FieldDesc { OwningType.IsString: true } fe && fe.GetName() == "Empty":
                    // string.Empty: [Intrinsic], never initialized by its type (the runtime's empty string, as the JIT
                    // and NativeAOT treat it): the literal ""
                    rd.ReadILToken();
                    Push(Add(Opc.LdStr, VK.Ref, StackType.ObjRef, Ctx.GetWellKnownType(WellKnownType.String)).WithX(""));
                    return true;
                case ILOpcode.ldsfld: { var f = (FieldDesc)I._il.GetObject(rd.ReadILToken()); Push(AddOfType(Opc.LdSFld, f.FieldType).WithX(f)); return true; }
                case ILOpcode.stsfld: { var f = (FieldDesc)I._il.GetObject(rd.ReadILToken()); Add(Opc.StSFld, VK.Void, StackType.None, null, Coerce(Pop(), f.FieldType)).WithX(f); return true; }
                case ILOpcode.ldsflda: { var f = (FieldDesc)I._il.GetObject(rd.ReadILToken()); Push(Add(Opc.LdSFldA, VK.Ptr, StackType.ByRef, f.FieldType.MakeByRefType()).WithX(f)); return true; }

                case >= ILOpcode.ldind_i1 and <= ILOpcode.ldind_ref:
                {
                    var addr = Pop();
                    TypeDesc t = op switch
                    {
                        ILOpcode.ldind_i1 => Ctx.GetWellKnownType(WellKnownType.SByte), ILOpcode.ldind_u1 => Ctx.GetWellKnownType(WellKnownType.Byte),
                        ILOpcode.ldind_i2 => Ctx.GetWellKnownType(WellKnownType.Int16), ILOpcode.ldind_u2 => Ctx.GetWellKnownType(WellKnownType.UInt16),
                        ILOpcode.ldind_i4 => Ctx.GetWellKnownType(WellKnownType.Int32), ILOpcode.ldind_u4 => Ctx.GetWellKnownType(WellKnownType.UInt32),
                        ILOpcode.ldind_i8 => Ctx.GetWellKnownType(WellKnownType.Int64), ILOpcode.ldind_i => Ctx.GetWellKnownType(WellKnownType.IntPtr),
                        ILOpcode.ldind_r4 => Ctx.GetWellKnownType(WellKnownType.Single), ILOpcode.ldind_r8 => Ctx.GetWellKnownType(WellKnownType.Double),
                        _ => addr.Type is ByRefType br && !br.ParameterType.IsValueType ? br.ParameterType : Ctx.GetWellKnownType(WellKnownType.Object),
                    };
                    if (addr.Kind != VK.Ptr) throw Invalid("ldind through a " + addr.Kind);
                    Push(AddOfType(Opc.LdInd, t, addr));
                    return true;
                }
                case ILOpcode.stind_ref: case ILOpcode.stind_i1: case ILOpcode.stind_i2: case ILOpcode.stind_i4: case ILOpcode.stind_i8:
                case ILOpcode.stind_r4: case ILOpcode.stind_r8: case ILOpcode.stind_i:
                {
                    var v = Pop(); var addr = Pop();
                    TypeDesc t = op switch
                    {
                        ILOpcode.stind_i1 => Ctx.GetWellKnownType(WellKnownType.SByte), ILOpcode.stind_i2 => Ctx.GetWellKnownType(WellKnownType.Int16),
                        ILOpcode.stind_i4 => Ctx.GetWellKnownType(WellKnownType.Int32), ILOpcode.stind_i8 => Ctx.GetWellKnownType(WellKnownType.Int64),
                        ILOpcode.stind_r4 => Ctx.GetWellKnownType(WellKnownType.Single), ILOpcode.stind_r8 => Ctx.GetWellKnownType(WellKnownType.Double),
                        ILOpcode.stind_i => Ctx.GetWellKnownType(WellKnownType.IntPtr), _ => Ctx.GetWellKnownType(WellKnownType.Object),
                    };
                    if (addr.Kind != VK.Ptr) throw Invalid("stind through a " + addr.Kind);
                    Add(Opc.StInd, VK.Void, StackType.None, t, addr, Coerce(v, t));
                    return true;
                }

                case ILOpcode.castclass: case ILOpcode.isinst:
                {
                    var t = (TypeDesc)I._il.GetObject(rd.ReadILToken());
                    var o = Pop();
                    if (o.Kind != VK.Ref) throw Invalid($"{op} of a {o.Kind}");
                    Push(Add(op == ILOpcode.castclass ? Opc.CastClass : Opc.IsInst, VK.Ref, StackType.ObjRef, t, o).WithX(t));
                    return true;
                }
                case ILOpcode.box:
                {
                    var t = (TypeDesc)I._il.GetObject(rd.ReadILToken());
                    var v = Pop();
                    if (!t.IsValueType) { Push(v); return true; }
                    Push(Add(Opc.Box, VK.Ref, StackType.ObjRef, t, Coerce(v, t)).WithX(t));
                    return true;
                }
                case ILOpcode.unbox_any:
                {
                    var t = (TypeDesc)I._il.GetObject(rd.ReadILToken());
                    var o = Pop();
                    if (!t.IsValueType) { Push(Add(Opc.CastClass, VK.Ref, StackType.ObjRef, t, o).WithX(t)); return true; }
                    Push(AddOfType(Opc.UnboxAny, t, o).WithX(t));
                    return true;
                }

                case ILOpcode.ldobj:
                {
                    var t = (TypeDesc)I._il.GetObject(rd.ReadILToken());
                    var addr = Pop();
                    if (addr.Kind != VK.Ptr) throw Invalid("ldobj through a " + addr.Kind);
                    Push(AddOfType(Kinds.IsStruct(t) ? Opc.LdObj : Opc.LdInd, t, addr).WithX(t));
                    return true;
                }
                case ILOpcode.stobj:
                {
                    var t = (TypeDesc)I._il.GetObject(rd.ReadILToken());
                    var v = Pop(); var addr = Pop();
                    if (addr.Kind != VK.Ptr) throw Invalid("stobj through a " + addr.Kind);
                    Add(Opc.StInd, VK.Void, StackType.None, t, addr, Coerce(v, t));
                    return true;
                }
                case ILOpcode.cpobj:
                {
                    var t = (TypeDesc)I._il.GetObject(rd.ReadILToken());
                    var src = Pop(); var dst = Pop();
                    var v = AddOfType(Kinds.IsStruct(t) ? Opc.LdObj : Opc.LdInd, t, src).WithX(t);
                    Add(Opc.StInd, VK.Void, StackType.None, t, dst, v);
                    return true;
                }
                case ILOpcode.initobj:
                {
                    var t = (TypeDesc)I._il.GetObject(rd.ReadILToken());
                    var addr = Pop();
                    if (addr.Kind != VK.Ptr) throw Invalid("initobj through a " + addr.Kind);
                    Add(Opc.InitObj, VK.Void, StackType.None, t, addr).WithX(t);
                    return true;
                }
                case ILOpcode.unbox:
                {
                    var t = (TypeDesc)I._il.GetObject(rd.ReadILToken());
                    var o = Pop();
                    Push(Add(Opc.Unbox, VK.Ptr, StackType.ByRef, t.MakeByRefType(), o).WithX(t));
                    return true;
                }
                case ILOpcode.sizeof_:
                {
                    var t = (TypeDesc)I._il.GetObject(rd.ReadILToken());
                    Push(Const(VK.I32, Kinds.Of(t) == VK.Struct ? ((DefType)t).InstanceFieldSize.AsInt : Kinds.SizeOf(t)));
                    return true;
                }
                case ILOpcode.ldftn:
                {
                    var fm = (MethodDesc)I._il.GetObject(rd.ReadILToken());
                    if (_constrained != null)
                    {
                        // constrained. T ldftn: a static virtual's implementation on T (a delegate of T.M)
                        var ct = _constrained;
                        _constrained = null;
                        fm = ResolveOn(ct, fm, fm.Signature.IsStatic) ?? throw new InvalidProgramException($"{I._method}: {ct} does not implement {fm}");
                    }
                    Push(Add(Opc.LdFtn, VK.Ptr, StackType.NativeInt, Ctx.GetWellKnownType(WellKnownType.IntPtr)).WithX(fm));
                    return true;
                }
                case ILOpcode.ldvirtftn:
                {
                    var fm = (MethodDesc)I._il.GetObject(rd.ReadILToken());
                    var o = Pop();
                    Push(Add(Opc.LdVirtFtn, VK.Ptr, StackType.NativeInt, Ctx.GetWellKnownType(WellKnownType.IntPtr), o).WithX(fm));
                    return true;
                }
                case ILOpcode.calli:
                {
                    var csig = (MethodSignature)I._il.GetObject(rd.ReadILToken());
                    var fp = Pop();
                    int n = csig.Length + (csig.IsStatic ? 0 : 1);
                    var args = new Node[n + 1];
                    for (int i = n - 1; i >= 0; i--) args[i] = Pop();
                    for (int i = 0; i < csig.Length; i++) args[i + (csig.IsStatic ? 0 : 1)] = Coerce(args[i + (csig.IsStatic ? 0 : 1)], csig[i]);
                    args[n] = fp;
                    var rt = csig.ReturnType;
                    var c = rt.IsVoid ? Add(Opc.CallI, VK.Void, StackType.None, null, args) : AddOfType(Opc.CallI, rt, args);
                    c.X = csig;
                    if (!rt.IsVoid) Push(c);
                    return true;
                }
                case ILOpcode.initblk: case ILOpcode.cpblk:
                {
                    // (address, value or source, size): memory.fill and memory.copy
                    var size = Pop(); var v = Pop(); var addr = Pop();
                    if (addr.Kind != VK.Ptr || size.SType is not (StackType.Int32 or StackType.NativeInt)) throw Invalid(op.ToString());
                    Add(op == ILOpcode.initblk ? Opc.InitBlk : Opc.CpBlk, VK.Void, StackType.None, null, addr, v, size);
                    return true;
                }
                case ILOpcode.localloc:
                {
                    var size = Pop();
                    if (S.Count != 0) throw Invalid("localloc with values on the stack");
                    I._body.UsesLocAlloc = true;
                    Push(Add(Opc.LocAlloc, VK.Ptr, StackType.NativeInt, Ctx.GetWellKnownType(WellKnownType.IntPtr), size));
                    return true;
                }
                case ILOpcode.ldtoken:
                {
                    var tok = I._il.GetObject(rd.ReadILToken());
                    // typeof(T): ldtoken T, call Type.GetTypeFromHandle; known types compared with == fold to a constant
                    if (tok is TypeDesc tt && rd.HasNext && rd.PeekILOpcode() == ILOpcode.call)
                    {
                        int back = rd.Offset;
                        rd.ReadILOpcode();
                        if (I._il.GetObject(rd.ReadILToken()) is MethodDesc gth && gth.GetName() == "GetTypeFromHandle")
                        {
                            Push(Add(Opc.TypeOf, VK.Ref, StackType.ObjRef, Ctx.SystemModule.GetType("System"u8, "Type"u8)).WithX(tt));
                            return true;
                        }
                        rd.Seek(back);
                    }
                    if (tok is FieldDesc fd && rd.HasNext && rd.PeekILOpcode() == ILOpcode.call)
                    {
                        // RuntimeHelpers.InitializeArray(array, field) and CreateSpan<T>(field): the field's data
                        int back = rd.Offset;
                        rd.ReadILOpcode();
                        if (I._il.GetObject(rd.ReadILToken()) is MethodDesc init)
                        {
                            if (init.GetName() == "InitializeArray")
                            {
                                var arr = Pop();
                                Add(Opc.InitArray, VK.Void, StackType.None, null, arr).WithX(fd);
                                return true;
                            }
                            if (init.GetName() == "CreateSpan")
                            {
                                Push(AddOfType(Opc.CreateSpan, init.Signature.ReturnType).WithX(fd).WithI(Kinds.SizeOf(init.Instantiation[0])));
                                return true;
                            }
                        }
                        rd.Seek(back);
                    }
                    // a handle: RuntimeTypeHandle, RuntimeFieldHandle, RuntimeMethodHandle
                    var ht = Ctx.SystemModule.GetType("System"u8, tok switch { TypeDesc => "RuntimeTypeHandle"u8, FieldDesc => "RuntimeFieldHandle"u8, _ => "RuntimeMethodHandle"u8 });
                    Push(AddOfType(Opc.LdToken, ht).WithX(tok));
                    return true;
                }
                default: throw new NotSupportedException($"{I._method}: IL {op} at IL_{_at:x4} is not imported yet");
            }
        }

        Exception Invalid(string what) => new InvalidProgramException($"{I._method}: {what} at IL_{_at:x4}");

        Node ZeroOf(Node v) => v.Kind switch
        {
            VK.Ref => Add(Opc.LdNull, VK.Ref, StackType.ObjRef, Ctx.GetWellKnownType(WellKnownType.Object)),
            VK.I64 => Const(VK.I64, 0),
            VK.F32 => ConstF(VK.F32, 0), VK.F64 => ConstF(VK.F64, 0),
            _ => Const(VK.I32, 0),
        };

        /// <summary>The result type of a binary numeric operation (ECMA-335 III.1.5, table III.2), or None.</summary>
        static StackType Numeric(StackType a, StackType b, bool addSub)
        {
            if (a == StackType.Int32 && b == StackType.Int32) return StackType.Int32;
            if (a == StackType.Int64 && b == StackType.Int64) return StackType.Int64;
            if (a == StackType.Float && b == StackType.Float) return StackType.Float;
            if (a is StackType.Int32 or StackType.NativeInt && b is StackType.Int32 or StackType.NativeInt) return StackType.NativeInt;
            if (addSub && a == StackType.ByRef && b is StackType.Int32 or StackType.NativeInt) return StackType.ByRef;
            if (addSub && b == StackType.ByRef && a is StackType.Int32 or StackType.NativeInt) return StackType.ByRef;
            if (addSub && a == StackType.ByRef && b == StackType.ByRef) return StackType.NativeInt;
            return StackType.None;
        }

        void Binary(BinOp op, bool ovf, bool un = false)
        {
            var b = Pop(); var a = Pop();
            var st = Numeric(a.SType, b.SType, op is BinOp.Add or BinOp.Sub);
            if (st == StackType.None || (st == StackType.Float && op is BinOp.And or BinOp.Or or BinOp.Xor or BinOp.DivUn or BinOp.RemUn))
                throw Invalid($"{op} of {a.SType} and {b.SType}");
            VK k = st switch { StackType.Int64 => VK.I64, StackType.Int32 => VK.I32, StackType.Float => a.Kind == VK.F64 || b.Kind == VK.F64 ? VK.F64 : VK.F32, _ => VK.Ptr };
            if (k is VK.F32 or VK.F64) { if (a.Kind != k) a = Conv(a, k, 0, false, false, false); if (b.Kind != k) b = Conv(b, k, 0, false, false, false); }
            var n = Add(Opc.Bin, k, st, st == StackType.ByRef ? (a.SType == StackType.ByRef ? a.Type : b.Type) : null, a, b);
            n.Sub = (byte)op; n.Ovf = ovf; n.Unsigned = un;
            Push(n);
        }

        void Shift(BinOp op)
        {
            var b = Pop(); var a = Pop();
            if (a.SType is not (StackType.Int32 or StackType.Int64 or StackType.NativeInt) || b.SType is not (StackType.Int32 or StackType.NativeInt))
                throw Invalid($"{op} of {a.SType} by {b.SType}");
            var n = Add(Opc.Bin, a.Kind, a.SType, null, a, b);
            n.Sub = (byte)op;
            Push(n);
        }

        void Compare(CmpOp op) { var b = Pop(); var a = Pop(); Push(CompareWith(op, a, b)); }

        Node CompareWith(CmpOp op, Node a, Node b)
        {
            // (typeof(A) and typeof(B) compared as references: the same Type object exactly when the same type)
            if (a.Op == Opc.TypeOf && b.Op == Opc.TypeOf && op is CmpOp.Eq or CmpOp.Ne) return Const(VK.I32, (op == CmpOp.Eq) == (a.X == b.X) ? 1 : 0);
            bool ok = a.SType == b.SType && a.SType != StackType.None
                || a.SType is StackType.Int32 or StackType.NativeInt && b.SType is StackType.Int32 or StackType.NativeInt
                || a.SType is StackType.ByRef or StackType.NativeInt && b.SType is StackType.ByRef or StackType.NativeInt;
            if (!ok) throw Invalid($"{op} of {a.SType} and {b.SType}");
            if (a.SType == StackType.ObjRef && op is not (CmpOp.Eq or CmpOp.Ne or CmpOp.GtUn or CmpOp.LtUn)) throw Invalid($"{op} of object references");
            if (a.Kind is VK.F32 or VK.F64 && a.Kind != b.Kind) { a = Conv(a, VK.F64, 0, false, false, false); b = Conv(b, VK.F64, 0, false, false, false); }
            var n = Add(Opc.Cmp, VK.I32, StackType.Int32, Ctx.GetWellKnownType(WellKnownType.Boolean), a, b);
            n.Sub = (byte)op;
            return n;
        }

        Node LdElem(Node arr, Node idx, TypeDesc et)
        {
            if (arr.Kind != VK.Ref || idx.SType is not (StackType.Int32 or StackType.NativeInt)) throw Invalid("ldelem");
            return AddOfType(Opc.LdElem, et, arr, idx).WithX(et);
        }

        void StElem(Node arr, Node idx, Node val, TypeDesc et, bool checkType)
        {
            if (arr.Kind != VK.Ref || idx.SType is not (StackType.Int32 or StackType.NativeInt)) throw Invalid("stelem");
            var n = Add(Opc.StElem, VK.Void, StackType.None, null, arr, idx, Coerce(val, et)).WithX(et);
            n.Ovf = checkType;
        }

        void Call(MethodDesc m, bool virt)
        {
            if (_constrained == null && Importer.ConstantGetter(m) is long cv) { Push(Const(VK.I32, cv)); return; }
            // typeof(T).IsValueType and its kind: known for the instantiation (as the JIT and NativeAOT fold it)
            if (S.Count > 0 && S[^1].Op == Opc.TypeOf && !m.Signature.IsStatic && m.Signature.Length == 0 && m.OwningType is MetadataType { Name: var _ } tt0 && tt0.GetName() == "Type"
                && S[^1].X is TypeDesc known && TypeQuery(m.GetName(), known) is bool q)
            {
                Pop();
                Push(Const(VK.I32, q ? 1 : 0));
                return;
            }
            // typeof(A) == typeof(B), both known: a constant (as NativeAOT and ILLink fold it)
            if (m.GetName() is "op_Equality" or "op_Inequality" && m.OwningType is MetadataType ot && ot.GetName() == "Type" && S.Count >= 2
                && S[^1].Op == Opc.TypeOf && S[^2].Op == Opc.TypeOf)
            {
                var b2 = Pop(); var a2 = Pop();
                bool same = a2.X == b2.X;
                Push(Const(VK.I32, (m.GetName() == "op_Equality") == same ? 1 : 0));
                return;
            }
            var sig = m.Signature;
            int n = sig.Length + (sig.IsStatic ? 0 : 1);
            var args = new Node[n];
            for (int i = n - 1; i >= 0; i--) args[i] = Pop();
            for (int i = 0; i < sig.Length; i++) args[i + (sig.IsStatic ? 0 : 1)] = Coerce(args[i + (sig.IsStatic ? 0 : 1)], sig[i]);
            if (_constrained != null)
            {
                var ct = _constrained;
                _constrained = null;
                if (sig.IsStatic)
                {
                    // a static virtual (an interface's static abstract member): the constrained type's own
                    m = ResolveOn(ct, m, true) ?? throw new InvalidProgramException($"{I._method}: {ct} does not implement {m}");
                    virt = false;
                }
                else if (!ct.IsValueType)
                {
                    // (a reference type: the byref this is dereferenced, then a normal virtual call)
                    args[0] = AddOfType(Opc.LdInd, ct, args[0]);
                }
                else
                {
                    var impl = ResolveOn(ct, m, false);
                    if (impl != null && impl.OwningType == ct) { m = impl; virt = false; }
                    else
                    {
                        // the value boxed, and the call made on the box
                        var v = Kinds.IsStruct(ct) ? AddOfType(Opc.LdObj, ct, args[0]).WithX(ct) : AddOfType(Opc.LdInd, ct, args[0]);
                        args[0] = Add(Opc.Box, VK.Ref, StackType.ObjRef, ct, v).WithX(ct);
                    }
                }
            }
            if (!sig.IsStatic && args[0].Kind != VK.Ref && args[0].Kind != VK.Ptr) throw Invalid("a call's this of a " + args[0].Kind);
            var kind = CallKind.Direct;
            if (virt && m.IsVirtual && !m.IsFinal && !(m.OwningType is MetadataType { IsSealed: true }))
                kind = m.OwningType.IsInterface ? CallKind.Interface : CallKind.Virtual;
            var rt = sig.ReturnType;
            var c = rt.IsVoid ? Add(Opc.Call, VK.Void, StackType.None, null, args) : AddOfType(Opc.Call, rt, args);
            c.X = m; c.Sub = (byte)kind; c.Ovf = virt && !sig.IsStatic;
            if (!rt.IsVoid) Push(c);
        }

        static bool? TypeQuery(string getter, TypeDesc t) => getter switch
        {
            "get_IsValueType" => t.IsValueType,
            "get_IsEnum" => t.IsEnum,
            "get_IsPrimitive" => t.IsPrimitive && !t.IsEnum,
            "get_IsInterface" => t.IsInterface,
            "get_IsArray" => t.IsArray,
            "get_IsByRef" => t.IsByRef,
            "get_IsPointer" => t.IsPointer,
            "get_IsClass" => !t.IsValueType && !t.IsInterface,
            _ => null,
        };

        /// <summary>The method a constrained call reaches on the type (a generic method instantiated as called).</summary>
        MethodDesc? ResolveOn(TypeDesc ct, MethodDesc m, bool isStatic)
        {
            var def = m.HasInstantiation ? m.GetMethodDefinition() : m;
            var algo = Ctx.GetVirtualMethodAlgorithmForType(ct);
            MethodDesc? r;
            if (isStatic)
            {
                r = algo.ResolveInterfaceMethodToStaticVirtualMethodOnType(def, ct) ?? algo.ResolveVariantInterfaceMethodToStaticVirtualMethodOnType(def, ct);
                if (r == null && algo.ResolveInterfaceMethodToDefaultImplementationOnType(def, ct, out var dim) == DefaultInterfaceMethodResolution.DefaultImplementation) r = dim;
            }
            else
            {
                r = def.OwningType.IsInterface ? ct.ResolveInterfaceMethodToVirtualMethodOnType(def) ?? ct.ResolveVariantInterfaceMethodToVirtualMethodOnType(def) : ct.FindVirtualFunctionTargetMethodOnObjectType(def);
                if (r == null && def.OwningType.IsInterface && algo.ResolveInterfaceMethodToDefaultImplementationOnType(def, ct, out var dim) == DefaultInterfaceMethodResolution.DefaultImplementation) r = dim;
            }
            if (r != null && m.HasInstantiation) r = Ctx.GetInstantiatedMethod(r.GetMethodDefinition(), m.Instantiation);
            return r;
        }

        void NewObj(MethodDesc ctor)
        {
            var t = ctor.OwningType;

            var sig = ctor.Signature;
            if (t.IsArray)
            {
                // a multi-dimensional array: its lengths (or lower bounds and lengths) are the constructor's arguments
                var dims = new Node[sig.Length];
                for (int i = sig.Length - 1; i >= 0; i--) dims[i] = Coerce(Pop(), sig[i]);
                Push(Add(Opc.NewObj, VK.Ref, StackType.ObjRef, t, dims).WithX(ctor));
                return;
            }
            var args = new Node[sig.Length];
            for (int i = sig.Length - 1; i >= 0; i--) args[i] = Coerce(Pop(), sig[i]);
            if (t.IsValueType) { Push(AddOfType(Opc.NewObj, t, args).WithX(ctor)); return; }
            Push(Add(Opc.NewObj, VK.Ref, StackType.ObjRef, t, args).WithX(ctor));
        }
    }
}

static class NodeExt
{
    public static Node WithI(this Node n, long i) { n.I = i; return n; }
    public static Node WithX(this Node n, object? x) { n.X = x; return n; }
}
