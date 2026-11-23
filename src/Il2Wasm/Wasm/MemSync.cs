namespace Il2Wasm.Wasm;

/// <summary>
/// Threads: before an access the engine checks against the size of the memory, the thread makes sure it knows that
/// size. A shared memory grown by one thread is not grown for the others at once: each instance keeps the size it
/// last saw (`memory.size` answers that number), and V8 brings it up to date when the thread next passes a stack
/// check (a function's entry, a loop's head) or grows the memory itself. Plain loads and stores do not ask for the
/// size (the pages are there, the trap handler has nothing to do). Atomic accesses in code of the baseline tier
/// (Liftoff) and the bulk operations do, against the instance's own number: a thread that reads a reference another
/// thread stored after it grew the memory, and counts the object before it passes a stack check, traps with "memory
/// access out of bounds" on a word that is there (Node 24.19, V8 13.6; tools/grow-race.mjs shows it: 19 trials of
/// 20, none with the test below). Test 139 grows its memory once in every run, from 256 to 512 pages, while its
/// threads start, and one loaded run in some tens of thousands ended so, in Rt.LoadRetainSlow's atomic load of the
/// count of an object whose reference it had just read from a shared place. With `node --no-wasm-stack-checks` (no
/// stack check, so a thread learns of a growth only by growing) the same module trapped there, at the trace's two
/// frames, in 7 runs of 60; with this pass in none of 300. Test 197 so run: 60 of 60 without the pass, none with it;
/// the suite runs it that way too (`// node-flags:` in the test).
/// So, in a program with threads, before each atomic access and each copy or fill: if the address is past what this
/// instance takes the memory's size to be, `memory.grow(0)`, which brings the instance up to date (the address was
/// handed out after its pages were there, so the size is past it then). A shift and a compare of numbers the thread
/// has at hand, no word that threads share. Not before an access at a constant address (the statics: below the size
/// the memory starts with). IL2WASM_NO_MEM_SYNC=1 (a measurement): none.
/// </summary>
static class MemSync
{
    public static void Run(WasmModule m)
    {
        if (Environment.GetEnvironmentVariable("IL2WASM_NO_MEM_SYNC") == "1") return;
        foreach (var f in m.Functions)
        {
            if (f.IsImport || f.ForwardTo != null) continue;
            var b = f.Body;
            // (scratch locals, made when first needed: the address, and an access's other operands by type)
            int a = -1, n = -1;
            var spill = new Dictionary<(ValType, int), int>();
            int Local(ValType t) { f.Locals.Add(t); return f.Type.Params.Length + f.Locals.Count - 1; }
            for (int i = 0; i < b.Count; i++)
            {
                var op = b[i].Op;
                var values = Values(op);
                if (values == null) continue;
                int pos = b[i].Pos;
                var g = new List<Instr>();
                void Add(Op o, long imm = 0) => g.Add(new Instr { Op = o, Imm = imm, Pos = pos });
                // (if the address's page is not below what `memory.size` says: memory.grow(0))
                void Check()
                {
                    Add(Op.I32Const, 16); Add(Op.I32ShrU); Add(Op.MemorySize); Add(Op.I32GeU);
                    Add(Op.If); Add(Op.I32Const, 0); Add(Op.MemoryGrow); Add(Op.Drop); Add(Op.End);
                }
                if (op is Op.MemoryCopy or Op.MemoryFill)
                {
                    // (to, from or value, bytes: the last byte written, and read, by the higher of the two addresses)
                    if (a < 0) a = Local(ValType.I32);
                    if (n < 0) n = Local(ValType.I32);
                    int v = Spill(ValType.I32, 0);
                    Add(Op.LocalSet, n); Add(Op.LocalSet, v); Add(Op.LocalTee, a);
                    Add(Op.LocalGet, v); Add(Op.LocalGet, n);
                    Add(Op.LocalGet, a);
                    if (op == Op.MemoryCopy) { Add(Op.LocalGet, v); Add(Op.LocalGet, a); Add(Op.LocalGet, v); Add(Op.I32GtU); Add(Op.Select); }
                    Add(Op.LocalGet, n); Add(Op.I32Add);
                    Check();
                    b.InsertRange(i, g);
                    i += g.Count;
                    continue;
                }
                // (the other operands are constants or locals pushed right before: the test goes in before them)
                int at = i;
                bool simple = true;
                for (int k = 1; k <= values.Length && simple; k++) simple = i - k >= 0 && b[i - k].Op is Op.I32Const or Op.I64Const or Op.LocalGet;
                if (simple) at = i - values.Length;
                long offset = b[i].Imm;
                if (simple && at > 0 && b[at - 1].Op == Op.I32Const) continue; // (a static's address)
                if (simple && at > 0 && b[at - 1].Op == Op.LocalGet) Add(Op.LocalGet, b[at - 1].Imm);
                else
                {
                    if (a < 0) a = Local(ValType.I32);
                    if (!simple) for (int k = values.Length - 1; k >= 0; k--) Add(Op.LocalSet, Spill(values[k], k));
                    Add(Op.LocalTee, a);
                    Add(Op.LocalGet, a);
                }
                if (offset != 0) { Add(Op.I32Const, offset); Add(Op.I32Add); }
                Check();
                if (!simple) for (int k = 0; k < values.Length; k++) Add(Op.LocalGet, Spill(values[k], k));
                b.InsertRange(at, g);
                i += g.Count;
            }

            int Spill(ValType t, int k)
            {
                if (!spill.TryGetValue((t, k), out int l)) spill[(t, k)] = l = Local(t);
                return l;
            }
        }
    }

    static readonly ValType[] s_none = [], s_i32 = [ValType.I32], s_i64 = [ValType.I64], s_i32i32 = [ValType.I32, ValType.I32], s_i64i64 = [ValType.I64, ValType.I64], s_i32i64 = [ValType.I32, ValType.I64];

    /// <summary>The operands after the address of an access that is checked against the memory's size; null for any
    /// other instruction. (A copy's and a fill's are gone through by themselves.)</summary>
    static ValType[]? Values(Op op) => op switch
    {
        Op.I32AtomicLoad or Op.I64AtomicLoad => s_none,
        Op.I32AtomicStore or Op.I32AtomicRmwAdd or Op.I32AtomicRmwSub or Op.I32AtomicRmwXchg or Op.I32AtomicRmw8XchgU or Op.I32AtomicRmw16XchgU or Op.MemoryAtomicNotify => s_i32,
        Op.I64AtomicStore or Op.I64AtomicRmwAdd or Op.I64AtomicRmwSub or Op.I64AtomicRmwXchg => s_i64,
        Op.I32AtomicRmwCmpxchg or Op.I32AtomicRmw8CmpxchgU or Op.I32AtomicRmw16CmpxchgU => s_i32i32,
        Op.I64AtomicRmwCmpxchg => s_i64i64,
        Op.MemoryAtomicWait32 => s_i32i64,
        Op.MemoryAtomicWait64 => s_i64i64,
        Op.MemoryCopy or Op.MemoryFill => s_none,
        _ => null,
    };
}
