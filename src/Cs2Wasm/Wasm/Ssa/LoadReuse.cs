namespace Cs2Wasm.Wasm.Ssa;

/// <summary>
/// Loads reused across blocks and calls (spike of docs/AUDIT.md step 6.3, field-load value numbering;
/// CS2WASM_IR_GVN=1, programs without threads). A load `local.get x; T.load off` has the key (T, x, off). Over the
/// function's control flow a key is available where every path to it went through a load of that key with nothing
/// after it that writes x (a local.set or local.tee of it) or memory (a store, a bulk copy or fill, a call of a
/// function that can store: Effects.MayStore). Every load of a key writes its value into one local; a load where the
/// key is available reads that local instead. On every path the last load of the key read the same address of the
/// same memory, so the value is the same; it did not trap then, so it does not now.
/// Calls that store nothing on the way back: rt.nonnull (it stores only on its throwing path) and calls that never
/// return. rt.retain writes only count words and an arena member's words before its header: it ends only keys with
/// an offset below 16.
/// </summary>
static class LoadReuse
{
    public sealed class Stats
    {
        public int Loads, Keys, Replaced;
        public readonly Dictionary<string, int> ByFunction = [];
        public override string ToString() => $"ir loads: {Loads} loads from a local's address, {Keys} keys, {Replaced} reused\n"
            + string.Concat(ByFunction.OrderByDescending(x => x.Value).Take(15).Select(x => $"  reused {x.Value,5} {x.Key}\n"));
    }

    static bool IsLoad(Op op) => op >= Op.I32Load && op <= Op.I64Load32U;

    static ValType LoadType(Op op) => op switch
    {
        Op.I64Load or Op.I64Load8S or Op.I64Load8U or Op.I64Load16S or Op.I64Load16U or Op.I64Load32S or Op.I64Load32U => ValType.I64,
        Op.F32Load => ValType.F32,
        Op.F64Load => ValType.F64,
        _ => ValType.I32,
    };

    public static bool Run(WasmFunction f, Effects e, Stats st)
    {
        var b = f.Body;
        int n = b.Count;
        // keys: (op, local, offset) of each `local.get x; load`
        var keyOf = new int[n];
        Array.Fill(keyOf, -1);
        var keys = new Dictionary<(Op, long, long), int>();
        var keyLocal = new List<int>();
        var keyOff = new List<long>();
        var keyOp = new List<Op>();
        for (int i = 1; i < n; i++)
        {
            if (!IsLoad(b[i].Op) || b[i - 1].Op != Op.LocalGet) continue;
            st.Loads++;
            var k = (b[i].Op, b[i - 1].Imm, b[i].Imm);
            if (!keys.TryGetValue(k, out int id)) { id = keys.Count; keys[k] = id; keyLocal.Add((int)b[i - 1].Imm); keyOff.Add(b[i].Imm); keyOp.Add(b[i].Op); }
            keyOf[i] = id;
        }
        int nk = keys.Count;
        if (nk == 0) return false;
        // a key that is loaded once only can never be available again
        var count = new int[nk];
        for (int i = 0; i < n; i++) if (keyOf[i] >= 0) count[keyOf[i]]++;
        if (count.All(c => c < 2)) return false;
        st.Keys += nk;
        int w = (nk + 63) / 64;
        var byLocal = new Dictionary<int, ulong[]>();
        for (int k = 0; k < nk; k++)
        {
            if (!byLocal.TryGetValue(keyLocal[k], out var m)) byLocal[keyLocal[k]] = m = new ulong[w];
            m[k >> 6] |= 1UL << (k & 63);
        }
        var lowOff = new ulong[w];
        for (int k = 0; k < nk; k++) if (keyOff[k] < 16) lowOff[k >> 6] |= 1UL << (k & 63);
        var g = Cfg.Build(b, e.MayThrow);
        // must-availability: in[s] = AND over predecessors; unvisited = all (top)
        var avIn = new ulong[]?[n + 1];
        avIn[0] = new ulong[w];
        var work = new Stack<int>();
        var inWork = new bool[n + 1];
        work.Push(0); inWork[0] = true;
        var o = new ulong[w];
        bool Meet(int s, ulong[] v)
        {
            if (avIn[s] == null) { avIn[s] = (ulong[])v.Clone(); return true; }
            bool ch = false;
            var a = avIn[s]!;
            for (int k = 0; k < w; k++) { var x = a[k] & v[k]; if (x != a[k]) { a[k] = x; ch = true; } }
            return ch;
        }
        void Transfer(int i, ulong[] into)
        {
            var x = b[i];
            if (x.Op is Op.LocalSet or Op.LocalTee && byLocal.TryGetValue((int)x.Imm, out var m))
                for (int k = 0; k < w; k++) into[k] &= ~m[k];
            else if (x.Op == Op.Call && x.Ref is WasmFunction cg)
            {
                if (cg == e.Retain) { for (int k = 0; k < w; k++) into[k] &= ~lowOff[k]; }
                else if (cg == e.NonNull || e.NeverReturns.Contains(cg)) { }
                else if (e.MayStore.Contains(cg)) Array.Clear(into);
            }
            else if (e.Stores(x)) Array.Clear(into);
            if (keyOf[i] >= 0) into[keyOf[i] >> 6] |= 1UL << (keyOf[i] & 63);
        }
        while (work.Count > 0)
        {
            int i = work.Pop(); inWork[i] = false;
            if (i >= n) continue;
            var ai = avIn[i]!;
            ai.CopyTo(o, 0);
            Transfer(i, o);
            foreach (var s in g.Succ[i]) if (Meet(s, o) && !inWork[s]) { inWork[s] = true; work.Push(s); }
            // (a call that throws may have stored before it did: the catch gets what is left after its kills; a load,
            // the only instruction that makes a key available, does not throw)
            foreach (var s in g.Exc[i]) if (Meet(s, o) && !inWork[s]) { inWork[s] = true; work.Push(s); }
        }
        // a load whose key is available reads the key's local; every load of a key that is reused somewhere keeps its value there
        var reused = new bool[nk];
        var replace = new bool[n];
        for (int i = 0; i < n; i++)
        {
            int k = keyOf[i];
            if (k < 0 || avIn[i] is not { } a) continue;
            if ((a[k >> 6] & (1UL << (k & 63))) == 0) continue;
            replace[i] = true; reused[k] = true;
        }
        if (!reused.Any(x => x)) return false;
        int np = f.Type.Params.Length;
        var local = new int[nk];
        for (int k = 0; k < nk; k++) if (reused[k]) { local[k] = np + f.Locals.Count; f.Locals.Add(LoadType(keyOp[k])); }
        var outl = new List<Instr>(n + 16);
        for (int i = 0; i < n; i++)
        {
            int k = keyOf[i];
            if (k >= 0 && replace[i])
            {
                outl.Add(new Instr { Op = Op.Drop, Pos = b[i].Pos, Inl = b[i].Inl });
                outl.Add(new Instr { Op = Op.LocalGet, Imm = local[k], Pos = b[i].Pos, Inl = b[i].Inl });
                st.Replaced++;
                continue;
            }
            outl.Add(b[i]);
            if (k >= 0 && reused[k]) outl.Add(new Instr { Op = Op.LocalTee, Imm = local[k], Pos = b[i].Pos, Inl = b[i].Inl });
        }
        f.Body = outl;
        st.ByFunction[f.Name] = replace.Count(x => x);
        return true;
    }
}
