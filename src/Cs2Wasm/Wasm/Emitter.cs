namespace Cs2Wasm.Wasm;

internal sealed class Label { public string Name = ""; public override string ToString() => Name; }

/// <summary>Appends instructions to a function body, tracking structured-control depth for branch labels.</summary>
internal sealed class Emitter
{
    public readonly WasmFunction Fn;
    readonly List<Label?> _blocks = [];
    public int CurrentPos = -1;
    /// <summary>Reference values pushed since the last instruction (Instr.Probes).</summary>
    public int PendingProbes;
    public Emitter(WasmFunction fn) { Fn = fn; }

    public List<Instr> Body => Fn.Body;

    public int AddLocal(ValType t) { Fn.Locals.Add(t); return Fn.Type.Params.Length + Fn.Locals.Count - 1; }

    public ref Instr Emit(Op op, long imm = 0, long imm2 = 0, object? r = null)
    {
        Fn.Body.Add(new Instr { Op = op, Imm = imm, Imm2 = imm2, Ref = r, Pos = CurrentPos, Probes = (ushort)PendingProbes });
        PendingProbes = 0;
        return ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Fn.Body)[^1];
    }

    public void I32(int v) => Emit(Wasm.Op.I32Const, v);
    /// <summary>A type id (by the compiler's internal numbering; the module's number is written by the encoder).</summary>
    public void Tid(int id) => Emit(Wasm.Op.I32Const, id, 0, TypeIdRef.Of(id));
    /// <summary>A function's table index (a delegate's or filter's target): the function is live only while some
    /// live code loads it.</summary>
    public void I32Fn(WasmModule m, WasmFunction f) => Emit(Wasm.Op.I32Const, m.TableSlot(f), 0, f);
    public void I32Addr(IAddress a, int offset = 0) => Emit(Wasm.Op.I32Const, offset, 0, a);
    public void I64(long v) => Emit(Wasm.Op.I64Const, v);
    public void F32(float v) => Emit(Wasm.Op.F32Const, BitConverter.SingleToInt32Bits(v));
    public void F64(double v) => Emit(Wasm.Op.F64Const, BitConverter.DoubleToInt64Bits(v));
    public void Get(int l) => Emit(Wasm.Op.LocalGet, l);
    public void Set(int l) => Emit(Wasm.Op.LocalSet, l);
    public void Tee(int l) => Emit(Wasm.Op.LocalTee, l);
    public void GGet(WasmGlobal g) => Emit(Wasm.Op.GlobalGet, 0, 0, g);
    public void GSet(WasmGlobal g) => Emit(Wasm.Op.GlobalSet, 0, 0, g);
    public void Call(WasmFunction f)
    {
        if (RcSites != null && f.Name is "rt.retain" or "rt.release") NoteRcSite(f.Name);
        // (CS2WASM_COUNT_RC: the compiler code that asked for the count, with the call: the run's counts by cause)
        if (s_countRc && f.Name is "rt.retain" or "rt.release") { Emit(Wasm.Op.Call, 0, RcCause(), f); return; }
        Emit(Wasm.Op.Call, 0, 0, f);
    }

    /// <summary>CS2WASM_STATS: the compiler methods that emitted each retain and release (where counts come from).</summary>
    public static readonly Dictionary<string, int>? RcSites = Environment.GetEnvironmentVariable("CS2WASM_STATS") == "1" ? new() : null;

    static readonly bool s_countRc = Environment.GetEnvironmentVariable("CS2WASM_COUNT_RC") == "1";
    public static readonly List<string> RcCauses = [];
    static readonly Dictionary<string, int> s_rcCauseIds = [];

    static int RcCause()
    {
        var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
        var key = string.Join(" <- ", frames.Select(fr => fr.GetMethod()).Where(m => m != null && m.DeclaringType?.Name is not "Emitter").Take(3).Select(m => m!.Name));
        lock (s_rcCauseIds)
        {
            if (!s_rcCauseIds.TryGetValue(key, out int id)) { s_rcCauseIds[key] = id = RcCauses.Count + 1; RcCauses.Add(key); }
            return id;
        }
    }

    static void NoteRcSite(string what)
    {
        var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
        var names = frames.Select(fr => fr.GetMethod()).Where(m => m != null && m.DeclaringType?.Name is not "Emitter")
            .Take(2).Select(m => m!.Name);
        var key = what[3..] + " <- " + string.Join(" <- ", names);
        lock (RcSites!) RcSites[key] = RcSites.GetValueOrDefault(key) + 1;
    }
    public void Op(Op op) => Emit(op);
    public void Load(Op op, int offset = 0) => Emit(op, offset, NaturalAlign(op));
    public void Store(Op op, int offset = 0) => Emit(op, offset, NaturalAlign(op));

    static int NaturalAlign(Op op) => op switch
    {
        Wasm.Op.I32Load8S or Wasm.Op.I32Load8U or Wasm.Op.I64Load8S or Wasm.Op.I64Load8U or Wasm.Op.I32Store8 or Wasm.Op.I64Store8 => 0,
        Wasm.Op.I32Load16S or Wasm.Op.I32Load16U or Wasm.Op.I64Load16S or Wasm.Op.I64Load16U or Wasm.Op.I32Store16 or Wasm.Op.I64Store16 => 1,
        Wasm.Op.I64Load or Wasm.Op.F64Load or Wasm.Op.I64Store or Wasm.Op.F64Store => 3,
        Wasm.Op.I64AtomicLoad or Wasm.Op.I64AtomicStore or Wasm.Op.I64AtomicRmwAdd or Wasm.Op.I64AtomicRmwSub or Wasm.Op.I64AtomicRmwXchg or Wasm.Op.I64AtomicRmwCmpxchg or Wasm.Op.MemoryAtomicWait64 => 3,
        _ => 2,
    };

    // ---- structured control ----
    public Label Block(object? bt = null, string name = "") { var l = new Label { Name = name }; Emit(Wasm.Op.Block, 0, 0, bt); _blocks.Add(l); return l; }
    public Label Loop(object? bt = null, string name = "") { var l = new Label { Name = name }; Emit(Wasm.Op.Loop, 0, 0, bt); _blocks.Add(l); return l; }
    public Label If(object? bt = null) { var l = new Label(); Emit(Wasm.Op.If, 0, 0, bt); _blocks.Add(l); return l; }
    public void Else() => Emit(Wasm.Op.Else);
    public void End() { Emit(Wasm.Op.End); _blocks.RemoveAt(_blocks.Count - 1); }

    /// <summary>Opens a try_table; catch labels refer to enclosing blocks (resolved now, outside the try_table).</summary>
    public Label TryTable(object? bt, params (byte kind, WasmTag? tag, Label target)[] catches)
    {
        var info = new TryTableInfo { BlockType = bt };
        foreach (var c in catches) info.Catches.Add(new CatchClause { Kind = c.kind, Tag = c.tag, Label = Depth(c.target) });
        var l = new Label();
        Emit(Wasm.Op.TryTable, 0, 0, info);
        _blocks.Add(l);
        return l;
    }

    public int Depth(Label l)
    {
        for (int i = _blocks.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_blocks[i], l)) return _blocks.Count - 1 - i;
        throw new InvalidOperationException("label not in scope: " + l.Name);
    }

    public void Br(Label l) => Emit(Wasm.Op.Br, Depth(l));
    public void BrIf(Label l) => Emit(Wasm.Op.BrIf, Depth(l));
    public void BrTable(Label[] targets, Label dflt) => Emit(Wasm.Op.BrTable, Depth(dflt), 0, targets.Select(Depth).ToArray());
    public int OpenBlocks => _blocks.Count;
}
