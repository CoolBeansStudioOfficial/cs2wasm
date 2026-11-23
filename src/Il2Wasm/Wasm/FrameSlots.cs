namespace Il2Wasm.Wasm;

/// <summary>
/// Struct values in locals (section 5). A struct local or temporary lives in the function's frame on the shadow stack,
/// so that its address can be taken; most never have it taken by anything but the code that reads and writes their
/// fields. This pass finds the frame slots (the compiler records each variable's place: WasmFunction.FrameSlots) whose
/// address goes nowhere but into loads, stores and copies of constant size, and gives each field of such a slot a
/// local of its own: the loads and stores become local.get and local.set, a copy between two such slots becomes moves
/// of locals, a copy to or from memory becomes the loads or stores of the fields. A call that takes such an address
/// and is small and cannot throw is inlined first, so that the address stays here. When no slot is left in memory the
/// frame goes: no stack pointer traffic, no clearing.
///
/// What is followed: the frame base (set once, in the prologue), the base plus constants, and locals holding those
/// (a forward dataflow over the control flow; a local that may hold two different addresses, or an address and
/// something else, and is read after, is an address that got away). An address that reaches anything else (a call, a
/// store as a value, arithmetic with a variable, a branch with it on the stack) takes its slot out; the frame base
/// itself reaching anything (a filter's record: section 7.4) takes the whole frame out.
/// </summary>
static partial class Locals
{
    /// <summary>The module's stack pointer (WasmModule.Encode sets it before this pass).</summary>
    [ThreadStatic] public static WasmGlobal? StackPointer;
    static readonly bool s_noFrameSlots = Environment.GetEnvironmentVariable("IL2WASM_NO_FRAME_SLOTS") == "1";
    const int MaxInlinedBody = 64;

    const int TopL = -1, BotL = -2;
    enum K : byte { Top, Frame, Const }
    /// <summary>A value on the stack: what it is, and the instructions [P, E] that made it when they do nothing else
    /// (they go when its one use turns into a local's).</summary>
    readonly record struct V(K Kind, int N, int P = -1, int E = -1);

    sealed class Cell { public int Off, Width, Local = -1; public bool Flt, Int; }

    sealed class FrameScan
    {
        public bool Bail;
        public readonly List<(int i, int off, int width, bool flt, int p, int e)> Acc = [];
        public readonly List<(int i, int dst, int src, int n, (int p, int e) dp, (int p, int e) sp)> Copies = [];
        public readonly List<(int i, int dst, int n, int p, int e)> Fills = [];
        public readonly HashSet<int> Escaped = [];
        public readonly List<int> InlineSites = [];
    }

    /// <summary>Returns whether the function changed.</summary>
    public static bool PromoteFrame(WasmFunction f)
    {
        if (s_noFrameSlots || f.IsImport || f.NoFramePromotion || f.FrameSlots == null || f.FrameSlots.Count == 0 || StackPointer == null) return false;
        bool any = false;
        for (int round = 0; round < 4; round++)
        {
            var b = f.Body;
            // the prologue: sp -> entry; entry - size -> base -> sp; the frame cleared
            if (b.Count < 11 || b[0].Op != Op.GlobalGet || !ReferenceEquals(b[0].Ref, StackPointer) || b[1].Op != Op.LocalTee || b[2].Op != Op.I32Const
                || b[3].Op != Op.I32Sub || b[4].Op != Op.LocalTee || b[5].Op != Op.GlobalSet || !ReferenceEquals(b[5].Ref, StackPointer)
                || b[6].Op != Op.LocalGet || b[6].Imm != b[4].Imm || b[7].Op != Op.I32Const || b[7].Imm != 0 || b[8].Op != Op.I32Const || b[8].Imm != b[2].Imm
                || b[9].Op != Op.MemoryFill) return any;
            int entry = (int)b[1].Imm, fb = (int)b[4].Imm, size = (int)b[2].Imm;
            for (int i = 10; i < b.Count; i++) if (b[i].Op is Op.LocalSet or Op.LocalTee && (b[i].Imm == fb || b[i].Imm == entry)) return any;
            var scan = Scan(f, fb, size);
            if (scan == null || scan.Bail) return any;
            if (scan.InlineSites.Count > 0 && round < 3)
            {
                var sites = scan.InlineSites.ToHashSet();
                var nb = new List<Instr>(b.Count + 64);
                for (int i = 0; i < b.Count; i++)
                {
                    if (sites.Contains(i)) Inliner.Expand(f, (WasmFunction)b[i].Ref!, nb, b[i].Pos, b[i].Inl);
                    else nb.Add(b[i]);
                }
                f.Body = nb;
                any = true;
                continue;
            }
            return Rewrite(f, scan, fb, entry, size) || any;
        }
        return any;
    }

    static FrameScan? Scan(WasmFunction f, int fb, int size)
    {
        var b = f.Body;
        int n = b.Count, nl = f.Type.Params.Length + f.Locals.Count;
        var fl = Analyze(f);
        var leader = new bool[n + 1];
        leader[10] = true;
        int leaders = 1;
        void Lead(int s) { if (s < n && !leader[s]) { leader[s] = true; leaders++; } }
        for (int i = 10; i < n; i++)
        {
            var su = fl.Succ[i];
            foreach (var s in su) if (s != i + 1) Lead(s);
            if (su.Count != 1 || su[0] != i + 1) Lead(i + 1);
            foreach (var s in fl.Exc[i]) Lead(s);
            if (b[i].Op == Op.End) Lead(i);
        }
        if ((long)leaders * nl > 6_000_000) return null;
        var inS = new int[n + 1][];
        var start = new int[nl];
        Array.Fill(start, TopL);
        start[fb] = 0;
        inS[10] = start;
        var scan = new FrameScan();
        var stack = new List<V>();
        var work = new Stack<int>();
        var queued = new bool[n + 1];
        bool record = false;
        var top = new V(K.Top, 0);
        // what is on the stack under a block, by the block's end: the block cannot touch it, and it is there again after
        var saved = new V[n][];
        var openOf = new int[n];
        Array.Fill(openOf, -1);
        for (int i = 0; i < n; i++) if (b[i].Op is Op.Block or Op.Loop or Op.If or Op.TryTable && fl.Match[i] >= 0) openOf[fl.Match[i]] = i;

        // (how many values a block leaves; -1: it takes some, which this pass does not follow)
        int Leaves(in Instr ins) => (ins.Ref is TryTableInfo tt ? tt.BlockType : ins.Ref) switch
        {
            null => 0,
            ValType => 1,
            FuncType ft => ft.Params.Length == 0 ? ft.Results.Length : -1,
            _ => -1,
        };
        void Save(int end)
        {
            var cur = stack.ToArray();
            var old = saved[end];
            if (record)
            {
                // (an entry that is not the same on every way in is not followed after the block)
                for (int k = 0; k < cur.Length; k++) if (cur[k].Kind == K.Frame && (old == null || k >= old.Length || old[k] != cur[k])) scan.Escaped.Add(cur[k].N);
                return;
            }
            if (old == null) { saved[end] = cur; if (inS[end] != null && !queued[end]) { queued[end] = true; work.Push(end); } return; }
            bool ch = false;
            if (old.Length != cur.Length) { scan.Bail = true; return; }
            for (int k = 0; k < cur.Length; k++) if (old[k] != cur[k] && old[k].Kind != K.Top) { old[k] = top; ch = true; }
            if (ch && inS[end] != null && !queued[end]) { queued[end] = true; work.Push(end); }
        }

        V Pop() { if (stack.Count == 0) return top; var v = stack[^1]; stack.RemoveAt(stack.Count - 1); return v; }
        void Esc(V v) { if (record && v.Kind == K.Frame) scan.Escaped.Add(v.N); }
        V Framed(int k) { if (k < 0 || k >= size) { scan.Bail = true; return top; } return new V(K.Frame, k); }
        bool PlainConst(int i) => i >= 0 && b[i].Op == Op.I32Const && b[i].Ref == null;

        void Step(int i, int[] st)
        {
            var ins = b[i];
            switch (ins.Op)
            {
                case Op.If: case Op.Block: case Op.Loop: case Op.TryTable:
                    if (ins.Op == Op.If) Esc(Pop());
                    if (Leaves(ins) < 0 || fl.Match[i] < 0) { foreach (var v in stack) Esc(v); stack.Clear(); return; }
                    Save(fl.Match[i]);
                    stack.Clear();
                    return;
                case Op.Else:
                    foreach (var v in stack) Esc(v);
                    stack.Clear();
                    return;
                case Op.End:
                    {
                        foreach (var v in stack) Esc(v);
                        stack.Clear();
                        if (saved[i] is { } under) stack.AddRange(under);
                        // (what the block leaves: its opener is the instruction this end matches)
                        int open = openOf[i];
                        int leaves = open >= 0 ? Leaves(b[open]) : 0;
                        for (int k = 0; k < leaves; k++) stack.Add(top);
                        return;
                    }
                case Op.BrIf:
                    // (the values under the condition stay for the code that follows; the top one may go with the branch)
                    Pop();
                    if (stack.Count > 0) Esc(stack[^1]);
                    return;
                case Op.Br: case Op.BrTable: case Op.Return: case Op.Unreachable: case Op.Throw: case Op.ThrowRef:
                    foreach (var v in stack) Esc(v);
                    stack.Clear();
                    return;
                case Op.LocalGet: { int x = st[(int)ins.Imm]; stack.Add(x >= 0 ? new V(K.Frame, x, i, i) : top); return; }
                case Op.LocalSet: { var v = Pop(); st[(int)ins.Imm] = v.Kind == K.Frame ? v.N : TopL; return; }
                case Op.LocalTee: { var v = Pop(); st[(int)ins.Imm] = v.Kind == K.Frame ? v.N : TopL; stack.Add(v with { P = -1, E = -1 }); return; }
                case Op.I32Const: stack.Add(ins.Ref == null ? new V(K.Const, (int)ins.Imm, i, i) : top); return;
                case Op.Drop: Pop(); return;
                case Op.I32Add:
                    {
                        var y = Pop(); var x = Pop();
                        // (made by the instructions of both operands and this one, when they follow each other)
                        int p = x.P >= 0 && y.P == x.E + 1 && y.E == i - 1 ? x.P : -1;
                        V r;
                        if (x.Kind == K.Frame && y.Kind == K.Const || x.Kind == K.Const && y.Kind == K.Frame) r = Framed(x.N + y.N);
                        else { Esc(x); Esc(y); r = top; }
                        stack.Add(r.Kind == K.Frame && p >= 0 ? r with { P = p, E = i } : r);
                        return;
                    }
                case Op.I32Sub:
                    {
                        var y = Pop(); var x = Pop();
                        if (x.Kind == K.Frame && y.Kind == K.Const) stack.Add(Framed(x.N - y.N));
                        else { Esc(x); Esc(y); stack.Add(top); }
                        return;
                    }
                case Op.MemoryCopy:
                    {
                        var c = Pop(); var s = Pop(); var d = Pop();
                        if (c.Kind == K.Const && c.N > 0 && PlainConst(i - 1) && (d.Kind == K.Frame || s.Kind == K.Frame))
                        { if (record) scan.Copies.Add((i, d.Kind == K.Frame ? d.N : -1, s.Kind == K.Frame ? s.N : -1, c.N, (d.P, d.E), (s.P, s.E))); }
                        else { Esc(d); Esc(s); }
                        return;
                    }
                case Op.MemoryFill:
                    {
                        var c = Pop(); var v = Pop(); var d = Pop();
                        if (d.Kind == K.Frame && c.Kind == K.Const && c.N > 0 && v.Kind == K.Const && v.N == 0 && PlainConst(i - 1) && PlainConst(i - 2))
                        { if (record) scan.Fills.Add((i, d.N, c.N, d.P, d.E)); }
                        else Esc(d);
                        return;
                    }
            }
            if (ins.Op >= Op.I32Load && ins.Op <= Op.I64Load32U)
            {
                var a = Pop();
                if (record && a.Kind == K.Frame) { var (w, flt) = AccessOf(ins.Op); scan.Acc.Add((i, a.N + (int)ins.Imm, w, flt, a.P, a.E)); }
                stack.Add(top);
                return;
            }
            if (ins.Op >= Op.I32Store && ins.Op <= Op.I64Store32)
            {
                var v = Pop(); var a = Pop();
                Esc(v);
                if (record && a.Kind == K.Frame) { var (w, flt) = AccessOf(ins.Op); scan.Acc.Add((i, a.N + (int)ins.Imm, w, flt, a.P, a.E)); }
                return;
            }
            if (Effect(ins) is not var (pop, push)) { scan.Bail = true; stack.Clear(); return; }
            bool framed = false;
            for (int k = 0; k < pop; k++) { var v = Pop(); if (v.Kind == K.Frame) { framed = true; Esc(v); } }
            for (int k = 0; k < push; k++) stack.Add(top);
            if (record && framed && ins.Op == Op.Call && ins.Ref is WasmFunction g && g != f && !g.IsImport && g.Body.Count <= MaxInlinedBody
                && MayThrow != null && (!MayThrow.Contains(g) && Inliner.Inlinable(g, MayThrow) || g.InlineHint && Inliner.InlinableHinted(g))) scan.InlineSites.Add(i);
        }

        void Flow(int[] st, int s)
        {
            if (s >= n) return;
            var t = inS[s];
            if (record)
            {
                if (t == null) return;
                for (int x = 0; x < nl; x++) if (st[x] >= 0 && t[x] != st[x] && Has(fl.LiveIn[s], x)) scan.Escaped.Add(st[x]);
                return;
            }
            bool ch = false;
            if (t == null) { inS[s] = (int[])st.Clone(); ch = true; }
            else for (int x = 0; x < nl; x++) if (t[x] != st[x] && t[x] != TopL) { t[x] = TopL; ch = true; }
            if (ch && !queued[s]) { queued[s] = true; work.Push(s); }
        }

        void Block(int l)
        {
            var st = (int[])inS[l].Clone();
            stack.Clear();
            for (int i = l; i < n; i++)
            {
                Step(i, st);
                if (scan.Bail) return;
                bool fall = false;
                foreach (var s in fl.Succ[i]) { if (s == i + 1 && s < n && !leader[s]) fall = true; else Flow(st, s); }
                foreach (var s in fl.Exc[i]) Flow(st, s);
                if (fall) continue;
                // (what is still on the stack goes on into code this walk starts afresh: not followed there)
                foreach (var v in stack) Esc(v);
                stack.Clear();
                return;
            }
        }

        work.Push(10); queued[10] = true;
        while (work.Count > 0)
        {
            int l = work.Pop();
            queued[l] = false;
            Block(l);
            if (scan.Bail) return scan;
        }
        record = true;
        for (int l = 10; l < n; l++) if (leader[l] && inS[l] != null) { Block(l); if (scan.Bail) return scan; }
        return scan;
    }

    static (int width, bool flt) AccessOf(Op op) => op switch
    {
        Op.I32Load or Op.I32Store => (4, false),
        Op.F32Load or Op.F32Store => (4, true),
        Op.I64Load or Op.I64Store => (8, false),
        Op.F64Load or Op.F64Store => (8, true),
        Op.I32Load8S or Op.I32Load8U or Op.I32Store8 => (1, false),
        Op.I32Load16S or Op.I32Load16U or Op.I32Store16 => (2, false),
        _ => (0, false), // (a 64-bit load of part of a word: the slot stays in memory)
    };

    static bool Rewrite(WasmFunction f, FrameScan scan, int fb, int entry, int size)
    {
        var b = f.Body;
        var slots = f.FrameSlots!.Distinct().OrderBy(s => s.off).ToArray();
        for (int i = 1; i < slots.Length; i++) if (slots[i - 1].off + slots[i - 1].size > slots[i].off) return false;
        int SlotOf(int k)
        {
            int lo = 0, hi = slots.Length - 1, r = -1;
            while (lo <= hi) { int m = (lo + hi) / 2; if (slots[m].off <= k) { r = m; lo = m + 1; } else hi = m - 1; }
            return r >= 0 && k < slots[r].off + slots[r].size ? r : -1;
        }
        int End(int s) => slots[s].off + slots[s].size;
        var poison = new bool[slots.Length];
        foreach (var k in scan.Escaped) { int s = SlotOf(k); if (s < 0) return false; poison[s] = true; }
        foreach (var a in scan.Acc) if (SlotOf(a.off) < 0) return false;
        foreach (var c in scan.Copies) if (c.dst >= 0 && SlotOf(c.dst) < 0 || c.src >= 0 && SlotOf(c.src) < 0) return false;
        foreach (var c in scan.Fills) if (SlotOf(c.dst) < 0) return false;

        var cells = new SortedDictionary<int, Cell>();
        bool again = true;
        void Poison(int s) { if (!poison[s]) { poison[s] = true; again = true; } }
        // (the cell that covers the byte, if any)
        Cell? At(int k)
        {
            foreach (int w in (ReadOnlySpan<int>)[0, 1, 2, 3, 4, 5, 6, 7])
                if (cells.TryGetValue(k - w, out var c)) return c.Off + c.Width > k ? c : null;
            return null;
        }
        bool Free(int off, int width) { for (int k = off; k < off + width; k++) if (At(k) != null) return false; return true; }
        List<Cell> In(int from, int to) => cells.Values.Where(c => c.Off + c.Width > from && c.Off < to).ToList();

        while (again)
        {
            again = false;
            cells.Clear();
            foreach (var a in scan.Acc)
            {
                int s = SlotOf(a.off);
                if (poison[s]) continue;
                if (a.width == 0 || a.off + a.width > End(s)) { Poison(s); continue; }
                if (cells.TryGetValue(a.off, out var c)) { if (c.Width != a.width) { Poison(s); continue; } }
                else if (!Free(a.off, a.width)) { Poison(s); continue; }
                else cells[a.off] = c = new Cell { Off = a.off, Width = a.width };
                if (a.flt) c.Flt = true; else c.Int = true;
            }
            foreach (var c in scan.Copies)
            {
                if (c.dst >= 0 && c.dst + c.n > End(SlotOf(c.dst))) Poison(SlotOf(c.dst));
                if (c.src >= 0 && c.src + c.n > End(SlotOf(c.src))) Poison(SlotOf(c.src));
                if (c.dst >= 0 && c.src >= 0 && SlotOf(c.dst) == SlotOf(c.src)) Poison(SlotOf(c.dst));
            }
            foreach (var c in scan.Fills) if (c.dst + c.n > End(SlotOf(c.dst))) Poison(SlotOf(c.dst));
            if (again) continue;
            // what one side of a copy has as a cell, the other side has too; then whole words where neither has any
            bool grew = true;
            while (grew && !again)
            {
                grew = false;
                foreach (var c in scan.Copies)
                {
                    bool d = c.dst >= 0 && !poison[SlotOf(c.dst)], s = c.src >= 0 && !poison[SlotOf(c.src)];
                    for (int side = 0; side < 2 && !again; side++)
                    {
                        bool mine = side == 0 ? s : d, other = side == 0 ? d : s;
                        int from = side == 0 ? c.src : c.dst, to = side == 0 ? c.dst : c.src;
                        if (!mine) continue;
                        foreach (var cell in In(from, from + c.n))
                        {
                            if (cell.Off < from || cell.Off + cell.Width > from + c.n) { Poison(SlotOf(from)); break; }
                            if (!other) continue;
                            int po = cell.Off - from + to;
                            if (cells.TryGetValue(po, out var peer))
                            {
                                if (peer.Width != cell.Width) { Poison(SlotOf(to)); break; }
                                if (peer.Flt != cell.Flt || peer.Int != cell.Int) { peer.Flt = cell.Flt |= peer.Flt; peer.Int = cell.Int |= peer.Int; grew = true; }
                            }
                            else if (!Free(po, cell.Width)) { Poison(SlotOf(to)); break; }
                            else { cells[po] = new Cell { Off = po, Width = cell.Width, Flt = cell.Flt, Int = cell.Int }; grew = true; }
                        }
                    }
                }
                if (again || grew) continue;
                foreach (var c in scan.Copies)
                {
                    for (int side = 0; side < 2; side++)
                    {
                        int from = side == 0 ? c.src : c.dst;
                        if (from < 0 || poison[SlotOf(from)]) continue;
                        for (int k = from; k < from + c.n;)
                        {
                            if (At(k) is { } have) { k = have.Off + have.Width; continue; }
                            int w = k % 8 == 0 && k + 8 <= from + c.n && Free(k, 8) ? 8 : k % 4 == 0 && k + 4 <= from + c.n && Free(k, 4) ? 4 : 0;
                            if (w == 0) { Poison(SlotOf(from)); break; }
                            cells[k] = new Cell { Off = k, Width = w, Int = true };
                            grew = true;
                            k += w;
                        }
                    }
                }
            }
            if (again) continue;
            foreach (var c in cells.Values) if (c.Flt && c.Int) Poison(SlotOf(c.Off));
            foreach (var c in scan.Fills)
                if (!poison[SlotOf(c.dst)])
                    foreach (var cell in In(c.dst, c.dst + c.n))
                        if (cell.Off < c.dst || cell.Off + cell.Width > c.dst + c.n) { Poison(SlotOf(c.dst)); break; }
        }
        foreach (var k in cells.Keys.ToList()) if (poison[SlotOf(k)]) cells.Remove(k);

        bool whole = !poison.Any(p => p);
        if (whole)
        {
            // (nothing else moves the stack pointer: a stackalloc would)
            int uses = 0, pairs = 0;
            for (int i = 10; i < b.Count; i++)
                if (ReferenceEquals(b[i].Ref, StackPointer) && b[i].Op is Op.GlobalGet or Op.GlobalSet)
                {
                    uses++;
                    if (b[i].Op == Op.GlobalSet && b[i - 1].Op == Op.LocalGet && b[i - 1].Imm == entry) pairs++;
                }
            if (uses != pairs) whole = false;
            for (int i = 10; i < b.Count && whole; i++)
                if (b[i].Op == Op.LocalGet && b[i].Imm == entry && !(i + 1 < b.Count && b[i + 1].Op == Op.GlobalSet && ReferenceEquals(b[i + 1].Ref, StackPointer))) whole = false;
        }
        if (cells.Count == 0 && !whole) return false;

        int np = f.Type.Params.Length;
        int NewLocal(ValType t) { f.Locals.Add(t); return np + f.Locals.Count - 1; }
        foreach (var c in cells.Values)
            c.Local = NewLocal(c.Width == 8 ? c.Flt ? ValType.F64 : ValType.I64 : c.Width == 4 && c.Flt ? ValType.F32 : ValType.I32);
        int tmpA = -1;
        var acc = new Dictionary<int, Cell>();
        foreach (var a in scan.Acc) if (cells.TryGetValue(a.off, out var c)) acc[a.i] = c;
        var copies = scan.Copies.Where(c => c.dst >= 0 && !poison[SlotOf(c.dst)] || c.src >= 0 && !poison[SlotOf(c.src)]).ToDictionary(c => c.i);
        var fills = scan.Fills.Where(c => !poison[SlotOf(c.dst)]).ToDictionary(c => c.i);
        var skip = new HashSet<int>();
        foreach (var i in copies.Keys) skip.Add(i - 1);
        foreach (var i in fills.Keys) { skip.Add(i - 1); skip.Add(i - 2); }
        // the instructions that made an address only to have it dropped go with it
        var made = new Dictionary<int, (int p, int e)>();
        foreach (var a in scan.Acc) if (a.p >= 0 && cells.ContainsKey(a.off)) made[a.i] = (a.p, a.e);
        bool Gone((int p, int e) m) { if (m.p < 0) return false; for (int k = m.p; k <= m.e; k++) skip.Add(k); return true; }
        foreach (var m in made.Values) Gone(m);
        var goneD = new HashSet<int>(); var goneS = new HashSet<int>(); var goneF = new HashSet<int>();
        foreach (var cp in copies.Values)
        {
            bool d = cp.dst >= 0 && !poison[SlotOf(cp.dst)], s = cp.src >= 0 && !poison[SlotOf(cp.src)];
            if (s && Gone(cp.sp)) goneS.Add(cp.i);
            if (d && Gone(cp.dp)) goneD.Add(cp.i);
        }
        foreach (var fi in fills.Values) if (Gone((fi.p, fi.e))) goneF.Add(fi.i);

        var o = new List<Instr>(b.Count + 16);
        for (int i = whole ? 10 : 0; i < b.Count; i++)
        {
            var ins = b[i];
            void Add(Op op, long imm = 0) => o.Add(new Instr { Op = op, Imm = imm, Pos = ins.Pos });
            if (skip.Contains(i)) continue;
            if (whole && ins.Op == Op.LocalGet && ins.Imm == entry) { i++; continue; } // (with its global.set: checked above)
            if (acc.TryGetValue(i, out var cell))
            {
                bool gone = made.ContainsKey(i);
                if (ins.Op >= Op.I32Load && ins.Op <= Op.I64Load32U)
                {
                    if (!gone) Add(Op.Drop);
                    Add(Op.LocalGet, cell.Local);
                    if (ins.Op == Op.I32Load8S) Add(Op.I32Extend8S); else if (ins.Op == Op.I32Load16S) Add(Op.I32Extend16S);
                }
                else
                {
                    if (ins.Op == Op.I32Store8) { Add(Op.I32Const, 255); Add(Op.I32And); }
                    else if (ins.Op == Op.I32Store16) { Add(Op.I32Const, 65535); Add(Op.I32And); }
                    Add(Op.LocalSet, cell.Local);
                    if (!gone) Add(Op.Drop);
                }
                continue;
            }
            if (copies.TryGetValue(i, out var cp))
            {
                bool d = cp.dst >= 0 && !poison[SlotOf(cp.dst)], s = cp.src >= 0 && !poison[SlotOf(cp.src)];
                if (d && s)
                {
                    // (the source is on top: its instructions come after the destination's, so each goes on its own)
                    if (!goneS.Contains(i)) Add(Op.Drop);
                    if (!goneD.Contains(i)) Add(Op.Drop);
                    foreach (var c in In(cp.src, cp.src + cp.n)) { Add(Op.LocalGet, c.Local); Add(Op.LocalSet, cells[c.Off - cp.src + cp.dst].Local); }
                }
                else if (s)
                {
                    if (tmpA < 0) tmpA = NewLocal(ValType.I32);
                    if (!goneS.Contains(i)) Add(Op.Drop);
                    Add(Op.LocalSet, tmpA);
                    foreach (var c in In(cp.src, cp.src + cp.n))
                    {
                        Add(Op.LocalGet, tmpA); Add(Op.LocalGet, c.Local);
                        Add(c.Width == 8 ? c.Flt ? Op.F64Store : Op.I64Store : c.Width == 4 ? c.Flt ? Op.F32Store : Op.I32Store : c.Width == 2 ? Op.I32Store16 : Op.I32Store8, c.Off - cp.src);
                    }
                }
                else
                {
                    if (tmpA < 0) tmpA = NewLocal(ValType.I32);
                    Add(Op.LocalSet, tmpA);
                    if (!goneD.Contains(i)) Add(Op.Drop);
                    foreach (var c in In(cp.dst, cp.dst + cp.n))
                    {
                        Add(Op.LocalGet, tmpA);
                        Add(c.Width == 8 ? c.Flt ? Op.F64Load : Op.I64Load : c.Width == 4 ? c.Flt ? Op.F32Load : Op.I32Load : c.Width == 2 ? Op.I32Load16U : Op.I32Load8U, c.Off - cp.dst);
                        Add(Op.LocalSet, c.Local);
                    }
                }
                continue;
            }
            if (fills.TryGetValue(i, out var fi))
            {
                if (!goneF.Contains(i)) Add(Op.Drop);
                foreach (var c in In(fi.dst, fi.dst + fi.n))
                {
                    Add(c.Width == 8 ? c.Flt ? Op.F64Const : Op.I64Const : c.Width == 4 && c.Flt ? Op.F32Const : Op.I32Const);
                    Add(Op.LocalSet, c.Local);
                }
                continue;
            }
            o.Add(ins);
        }
        f.Body = o;
        if (whole) f.FrameSlots = null;
        return true;
    }
}
