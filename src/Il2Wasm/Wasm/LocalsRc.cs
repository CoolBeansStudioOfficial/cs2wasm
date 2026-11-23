namespace Il2Wasm.Wasm;

/// <summary>
/// Count traffic the lowering leaves that has no effect (section 4: ownership; section 5: the final pass):
/// - `local.get x; call rt.retain; local.get x; call rt.release` (a return of an owned local) is `local.get x`;
/// - a release of a local that is still zero (its first assignment releases the "previous" value; a cleanup handler
///   releases a local no throwing instruction can see set) goes: a forward dataflow over the control flow, exception
///   edges included, of the locals that may be non-zero;
/// - a function's cleanup handler (block; try_table catch_all_ref; body; …; handler; throw_ref) that is left with
///   nothing to do goes with its try_table.
/// </summary>
static partial class Locals
{
    static bool IsRt(in Instr i, string name) => name == "rt.retain" ? Rc.IsRetain(i) : name == "rt.release" ? Rc.IsRelease(i) : i.Op == Op.Call && i.Ref is WasmFunction g && g.Name == name;
    static readonly bool s_transfers = Environment.GetEnvironmentVariable("IL2WASM_NO_TRANSFERS") != "1";

    /// <summary>`p = n; ...; n = something else` (a list walked with the node before it kept, a rotation of variables):
    /// p's new count and the release of n's old one cancel, n's count becomes p's. From `local.get n; call rt.retain;
    /// [local.get p; call rt.release;] local.set p` to `local.get n; call rt.release; [i32.const 0;] local.set n`, with
    /// nothing in between that reads n, writes p or releases p's value (p holds the object all the way, so it is
    /// destroyed no earlier than before), nothing that can throw (the handler would release n), and no branch.</summary>
    static bool Transfers(List<Instr> b)
    {
        bool any = false;
        for (int i = 0; i + 2 < b.Count; i++)
        {
            if (b[i].Op != Op.LocalGet || !IsRt(b[i + 1], "rt.retain")) continue;
            long n = b[i].Imm;
            int k = i + 2;
            if (k + 2 < b.Count && b[k].Op == Op.LocalGet && IsRt(b[k + 1], "rt.release") && b[k + 2].Op == Op.LocalSet && b[k + 2].Imm == b[k].Imm) k += 2;
            if (b[k].Op != Op.LocalSet || b[k].Imm == n) continue;
            long p = b[k].Imm;
            int j = -1;
            for (int q = k + 1; q + 1 < b.Count; q++)
            {
                var x = b[q];
                if (x.Op == Op.LocalGet)
                {
                    bool rel = IsRt(b[q + 1], "rt.release");
                    if (x.Imm == n)
                    {
                        if (rel && q + 2 < b.Count && (b[q + 2].Op == Op.LocalSet && b[q + 2].Imm == n
                            || q + 3 < b.Count && b[q + 2].Op == Op.I32Const && b[q + 2].Imm == 0 && b[q + 2].Ref == null && b[q + 3].Op == Op.LocalSet && b[q + 3].Imm == n)) j = q;
                        break;
                    }
                    if (x.Imm == p && rel) break;
                    continue;
                }
                if (x.Op is Op.LocalSet or Op.LocalTee) { if (x.Imm == n || x.Imm == p) break; continue; }
                if (x.Op == Op.Call) { if (IsRt(x, "rt.retain") || IsRt(x, "rt.release")) continue; break; }
                if (x.Op is Op.CallIndirect or Op.Block or Op.Loop or Op.If or Op.Else or Op.End or Op.Br or Op.BrIf or Op.BrTable or Op.Return
                    or Op.Throw or Op.ThrowRef or Op.Unreachable or Op.TryTable) break;
            }
            if (j < 0) continue;
            b.RemoveRange(j, 2);
            b.RemoveAt(i + 1);
            any = true;
        }
        return any;
    }

    static bool Counts(WasmFunction f)
    {
        var b = f.Body;
        bool any = false;
        for (int i = 0; i + 3 < b.Count; i++)
            if (b[i].Op == Op.LocalGet && IsRt(b[i + 1], "rt.retain") && b[i + 2].Op == Op.LocalGet && b[i + 2].Imm == b[i].Imm && IsRt(b[i + 3], "rt.release"))
            {
                b.RemoveRange(i + 1, 3);
                any = true;
            }
        if (s_transfers) any |= Transfers(b);
        var fl = Analyze(f);
        int n = b.Count, w = fl.Words, np = f.Type.Params.Length;
        // may-be-non-zero locals before each instruction (null: not reached)
        var inS = new ulong[]?[n + 1];
        inS[0] = new ulong[w];
        for (int p = 0; p < np; p++) Set(inS[0]!, p);
        bool Merge(int s, ulong[] v)
        {
            if (s > n) s = n;
            if (inS[s] == null) { inS[s] = (ulong[])v.Clone(); return true; }
            bool ch = false;
            var t = inS[s]!;
            for (int k = 0; k < w; k++) { var u = t[k] | v[k]; if (u != t[k]) { t[k] = u; ch = true; } }
            return ch;
        }
        bool changed = true;
        var outS = new ulong[w];
        while (changed)
        {
            changed = false;
            for (int i = 0; i < n; i++)
            {
                if (inS[i] is not { } st) continue;
                st.CopyTo(outS, 0);
                var ins = b[i];
                if (ins.Op is Op.LocalSet or Op.LocalTee)
                {
                    bool zero = i > 0 && b[i - 1].Op == Op.I32Const && b[i - 1].Imm == 0 && b[i - 1].Ref == null;
                    if (zero) Clear(outS, (int)ins.Imm); else Set(outS, (int)ins.Imm);
                }
                foreach (var s in fl.Succ[i]) changed |= Merge(s, outS);
                foreach (var s in fl.Exc[i]) changed |= Merge(s, st);
            }
        }
        var nop = new Instr { Op = Op.Nop, Pos = -1 };
        for (int i = 0; i + 1 < n; i++)
            if (b[i].Op == Op.LocalGet && IsRt(b[i + 1], "rt.release") && inS[i] is { } st && !Has(st, (int)b[i].Imm))
            {
                b[i] = nop with { Pos = b[i].Pos }; b[i + 1] = nop with { Pos = b[i + 1].Pos };
                any = true;
            }
        // a borrow: retained into a local and released again before anything could drop the owner's reference (no
        // call, store, or control flow in between, nothing else reads the local after)
        // (with threads another thread may drop the owner's reference meanwhile: the count keeps the value alive)
        for (int i = 0; i + 1 < n && !Threads; i++)
        {
            if (!IsRt(b[i], "rt.retain") || b[i + 1].Op is not (Op.LocalSet or Op.LocalTee)) continue;
            int t = (int)b[i + 1].Imm;
            for (int j = i + 2; j + 1 < n; j++)
            {
                var y = b[j];
                if (y.Op == Op.LocalGet && y.Imm == t && IsRt(b[j + 1], "rt.release"))
                {
                    if (!Has(fl.LiveOut[j + 1], t)) { b[i] = nop with { Pos = b[i].Pos }; b[j] = nop with { Pos = y.Pos }; b[j + 1] = nop with { Pos = b[j + 1].Pos }; any = true; }
                    break;
                }
                if (y.Op is Op.LocalSet or Op.LocalTee && y.Imm == t) break;
                if (y.Op is Op.Call or Op.CallIndirect || y.Op >= Op.I32Store && y.Op <= Op.I64Store32 || y.Op is Op.MemoryCopy or Op.MemoryFill || Effect(y) is null) break;
            }
        }
        if (RemoveEmptyCleanup(f, fl, inS)) any = true;
        if (!any) return false;
        f.Body = f.Body.Where(x => x.Op != Op.Nop).ToList();
        Peephole.Run(f);
        return true;
    }

    /// <summary>The function's cleanup handler, when nothing is left in it (or no exception can reach it).</summary>
    static bool RemoveEmptyCleanup(WasmFunction f, Flow fl, ulong[]?[] inS)
    {
        var b = f.Body;
        int n = b.Count;
        for (int k = 0; k + 1 < n; k++)
        {
            if (b[k].Op != Op.Block || b[k].Ref is not ValType.ExnRef || b[k + 1].Op != Op.TryTable) continue;
            if (b[k + 1].Ref is not TryTableInfo { Catches: [{ Kind: 3, Label: 0 }] }) continue;
            int e = fl.Match[k + 1];
            if (e < 0 || e + 2 >= n || b[e + 1].Op != Op.Unreachable || b[e + 2].Op != Op.End || fl.Match[k] != e + 2) continue;
            if (b[n - 1].Op != Op.ThrowRef) continue;
            bool reached = inS[e + 3 < n ? e + 3 : n] != null;
            bool empty = true;
            for (int j = e + 3; j < n - 1; j++) if (b[j].Op != Op.Nop) { empty = false; break; }
            if (reached && !empty) continue;
            // branches in the body that leave it come two labels closer; none may target the two removed
            int depth = 0;
            for (int j = k + 2; j < e; j++)
            {
                var ins = b[j];
                bool bad = ins.Op switch
                {
                    Op.Br or Op.BrIf => ins.Imm == depth || ins.Imm == depth + 1,
                    Op.BrTable => ins.Imm == depth || ins.Imm == depth + 1 || ((int[])ins.Ref!).Any(t => t == depth || t == depth + 1),
                    Op.TryTable => ((TryTableInfo)ins.Ref!).Catches.Any(c => c.Label == depth || c.Label == depth + 1),
                    _ => false,
                };
                if (bad) return false;
                if (ins.Op is Op.Block or Op.Loop or Op.If or Op.TryTable) depth++;
                else if (ins.Op == Op.End) depth--;
            }
            depth = 0;
            for (int j = k + 2; j < e; j++)
            {
                var ins = b[j];
                switch (ins.Op)
                {
                    case Op.Br or Op.BrIf when ins.Imm > depth: b[j] = ins with { Imm = ins.Imm - 2 }; break;
                    case Op.BrTable:
                        b[j] = ins with { Imm = ins.Imm > depth ? ins.Imm - 2 : ins.Imm, Ref = ((int[])ins.Ref!).Select(t => t > depth ? t - 2 : t).ToArray() };
                        break;
                    case Op.TryTable:
                        {
                            var tt = (TryTableInfo)ins.Ref!;
                            b[j] = ins with { Ref = new TryTableInfo { BlockType = tt.BlockType, Catches = tt.Catches.Select(c => new CatchClause { Kind = c.Kind, Tag = c.Tag, Label = c.Label > depth ? c.Label - 2 : c.Label }).ToList() } };
                            break;
                        }
                }
                if (ins.Op is Op.Block or Op.Loop or Op.If or Op.TryTable) depth++;
                else if (ins.Op == Op.End) depth--;
            }
            var body = b.GetRange(k + 2, e - (k + 2));
            var rebuilt = new List<Instr>(n);
            rebuilt.AddRange(b.Take(k));
            rebuilt.AddRange(body);
            // (the body never falls off its end: the try was followed by unreachable)
            rebuilt.Add(new Instr { Op = Op.Unreachable, Pos = -1 });
            f.Body = rebuilt;
            return true;
        }
        return false;
    }
}
