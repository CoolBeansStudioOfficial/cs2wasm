namespace Cs2Wasm.Wasm;

/// <summary>
/// Section 5: a load repeated in straight-line code across stores that cannot touch it. `p.Y += p.Vy; p.Vy += g;`
/// reads p.Vy twice with a store to p.Y between: the engine reads it again (it does not know the store is elsewhere),
/// a compiler that knows the two places are different fields does not. Here the proof needs no knowledge of fields:
/// the second load is from the same local plus the same constant as the first, the local has not been assigned
/// since, and everything between that writes memory is a store to that same local plus a constant whose bytes do not
/// overlap the load's. The same address, bytes no store between wrote: the same value, which is kept in a local.
/// What ends a value's use: an assignment of the local; a store through anything but that very local (another local
/// may hold the same object, or a place inside it), or to bytes that overlap; a call; a copy, a fill, an atomic or
/// vector instruction; any branch, block or loop (only straight-line code is looked at). A field another thread
/// writes between the two reads has no promised value there (a volatile field is read with other instructions, and
/// is never one of these loads). Runs after FloatStores, on the final bodies (a value "just loaded" is stored
/// without a NaN test there, which this would hide). CS2WASM_NO_SAME_BASE_LOADS=1: off. The churn scene's loop
/// without its store, tools/churn-raw.cs: 33.0 us a frame to 30.0 with p.Vy read once by hand.
/// </summary>
static class SameBaseLoads
{
    static readonly bool s_off = Environment.GetEnvironmentVariable("CS2WASM_NO_SAME_BASE_LOADS") == "1";

    static bool IsLoad(Op op) => op >= Op.I32Load && op <= Op.I64Load32U;
    static bool IsStore(Op op) => op >= Op.I32Store && op <= Op.I64Store32;

    static int Bytes(Op op) => op switch
    {
        Op.I32Load8S or Op.I32Load8U or Op.I64Load8S or Op.I64Load8U or Op.I32Store8 or Op.I64Store8 => 1,
        Op.I32Load16S or Op.I32Load16U or Op.I64Load16S or Op.I64Load16U or Op.I32Store16 or Op.I64Store16 => 2,
        Op.I32Load or Op.F32Load or Op.I64Load32S or Op.I64Load32U or Op.I32Store or Op.F32Store or Op.I64Store32 => 4,
        _ => 8,
    };

    static ValType LoadType(Op op) => op switch
    {
        Op.I64Load or Op.I64Load8S or Op.I64Load8U or Op.I64Load16S or Op.I64Load16U or Op.I64Load32S or Op.I64Load32U => ValType.I64,
        Op.F32Load => ValType.F32,
        Op.F64Load => ValType.F64,
        _ => ValType.I32,
    };

    /// <summary>The local a store's address is, when the address is just that local: the instructions between it and
    /// the store are the stored value's, by their effect on the stack. -1: not known.</summary>
    static int StoreBase(List<Instr> b, int store)
    {
        int need = 1, k = store - 1;
        for (; k >= 0; k--)
        {
            if (Locals.PlainEffect(b[k]) is not { } e) return -1;
            need -= e.push;
            if (need < 0) return -1;
            need += e.pop;
            if (need == 0) break;
        }
        return k >= 1 && b[k - 1].Op is Op.LocalGet or Op.LocalTee ? (int)b[k - 1].Imm : -1;
    }

    /// <summary>Returns whether the function changed.</summary>
    public static bool Run(WasmFunction f)
    {
        if (s_off || f.IsImport) return false;
        var b = f.Body;
        int n = b.Count;
        // (the loads whose value is still what memory holds: by local, load and offset, the instruction that loaded it)
        var avail = new Dictionary<(int local, Op op, long off), int>();
        List<(int first, int again)>? pairs = null;
        var drop = new List<(int local, Op op, long off)>();
        for (int i = 0; i < n; i++)
        {
            var ins = b[i];
            var op = ins.Op;
            if (avail.Count == 0 && !IsLoad(op)) continue;
            if (op is Op.LocalSet or Op.LocalTee)
            {
                drop.Clear();
                foreach (var key in avail.Keys) if (key.local == (int)ins.Imm) drop.Add(key);
                foreach (var key in drop) avail.Remove(key);
                continue;
            }
            if (IsLoad(op))
            {
                // (the address: a local just read, or just assigned and left on the stack; the latter's earlier values
                // went when it was assigned, so it can only be a first read)
                if (i == 0 || b[i - 1].Op is not (Op.LocalGet or Op.LocalTee)) continue;
                var key = ((int)b[i - 1].Imm, op, ins.Imm);
                if (avail.TryGetValue(key, out int first)) { if (b[i - 1].Op == Op.LocalGet) (pairs ??= []).Add((first, i)); }
                else avail[key] = i;
                continue;
            }
            if (IsStore(op))
            {
                int x = StoreBase(b, i);
                if (x < 0) { avail.Clear(); continue; }
                long lo = ins.Imm, hi = lo + Bytes(op);
                drop.Clear();
                foreach (var key in avail.Keys)
                    if (key.local != x || key.off < hi && lo < key.off + Bytes(key.op)) drop.Add(key);
                foreach (var key in drop) avail.Remove(key);
                continue;
            }
            // anything else that may write memory or leave the straight line: nothing is known after it
            if (op is Op.Call or Op.CallIndirect or Op.MemoryCopy or Op.MemoryFill or Op.MemoryInit or Op.MemoryGrow or Op.V128Store
                || (int)op >= 0xFC00 || Locals.PlainEffect(ins) == null)
                avail.Clear();
        }
        if (pairs == null) return false;
        int firstLocal = f.Type.Params.Length;
        var temp = new Dictionary<int, int>();
        var again = new Dictionary<int, int>();
        foreach (var (first, ag) in pairs)
        {
            if (!temp.TryGetValue(first, out int t))
            {
                f.Locals.Add(LoadType(b[first].Op));
                temp[first] = t = firstLocal + f.Locals.Count - 1;
            }
            again[ag] = t;
        }
        var nb = new List<Instr>(n + temp.Count);
        for (int i = 0; i < n; i++)
        {
            // (the local's read before a load that is replaced goes with it)
            if (again.ContainsKey(i + 1)) continue;
            if (again.TryGetValue(i, out int t)) { nb.Add(new Instr { Op = Op.LocalGet, Imm = t, Pos = b[i].Pos, Inl = b[i].Inl }); continue; }
            nb.Add(b[i]);
            if (temp.TryGetValue(i, out int keep)) nb.Add(new Instr { Op = Op.LocalTee, Imm = keep, Pos = b[i].Pos, Inl = b[i].Inl });
        }
        f.Body = nb;
        return true;
    }
}
