namespace Cs2Wasm.Wasm;

/// <summary>
/// Section 5: loads a loop repeats. In an innermost loop that writes nothing to memory (no store, no call but to a
/// helper that throws), memory is the same at every load, so
///  - a load from an address the loop has already loaded from in the same turn, with nothing in between that
///    changes the address, is the value it got then (`bodies[j]` read for two of its fields);
///  - a load from `p + constant`, p a reference the loop does not assign, is the same in every turn: it is done
///    once, before the loop (a field of an object the loop works on, an array's length in the condition).
/// The load before the loop runs even when the loop's body would not: it cannot fail, since p is a reference (the
/// loop itself checks it for null or reads its length, and C# has every variable assigned before the loop that
/// reads it), so it is null or an object, and a small offset from either is inside the memory.
/// Nothing a program can observe changes: no exception moves, and a field another thread writes meanwhile has no
/// promised value in a loop that does not synchronize (volatile fields are read with other instructions).
/// </summary>
static class LoopLoads
{
    sealed class Sym(string key, int[] deps, int baseLocal, long off)
    {
        public readonly string Key = key;
        public readonly int[] Deps = deps;
        /// <summary>The local this is a constant offset from; -1: none. -2: a constant (Off).</summary>
        public readonly int Base = baseLocal;
        public readonly long Off = off;
    }

    static bool IsLoad(Op op) => op >= Op.I32Load && op <= Op.I64Load32U;

    static ValType LoadType(Op op) => op switch
    {
        Op.I64Load or Op.I64Load8S or Op.I64Load8U or Op.I64Load16S or Op.I64Load16U or Op.I64Load32S or Op.I64Load32U => ValType.I64,
        Op.F32Load => ValType.F32,
        Op.F64Load => ValType.F64,
        _ => ValType.I32,
    };

    static bool Throws(in Instr x) => x.Op == Op.Call && x.Ref is WasmFunction h && h.Name.Contains("ThrowHelpers.", StringComparison.Ordinal);

    /// <summary>Returns whether the function changed.</summary>
    public static bool Run(WasmFunction f)
    {
        var b = f.Body;
        int n = b.Count;
        bool any = false;
        for (int i = 0; i < n && !any; i++) any = b[i].Op == Op.Loop;
        if (!any) return false;
        var end = new int[n];
        var hasElse = new bool[n];
        var open = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            var op = b[i].Op;
            if (op is Op.Block or Op.Loop or Op.If or Op.TryTable) open.Push(i);
            else if (op == Op.Else && open.Count > 0) hasElse[open.Peek()] = true;
            else if (op == Op.End && open.Count > 0) end[open.Pop()] = i;
        }
        // edits: a load replaced by a local (drop the address, get the local); a load whose value is kept (tee)
        Dictionary<int, int>? replace = null, keep = null;
        SortedDictionary<int, List<Instr>>? before = null;
        int firstLocal = f.Type.Params.Length;
        for (int l = 0; l < n; l++)
        {
            if (b[l].Op != Op.Loop) continue;
            int e = end[l];
            if (e <= l || !Clean(b, l, e)) continue;
            var assigned = new HashSet<int>();
            var isRef = new HashSet<int>();
            for (int k = l + 1; k < e; k++)
            {
                var x = b[k];
                if (x.Op is Op.LocalSet or Op.LocalTee) assigned.Add((int)x.Imm);
                if (x.Op != Op.LocalGet) continue;
                // what shows the local is a reference: its null check, or its length read
                int q = k + 1;
                if (q < e && b[q].Op == Op.LocalTee) q++;
                if (q + 2 < e && b[q].Op == Op.I32Eqz && b[q + 1].Op == Op.If && Throws(b[q + 2])) isRef.Add((int)x.Imm);
                else if (k + 1 < e && b[k + 1].Op == Op.I32Load && ReferenceEquals(b[k + 1].Ref, Loops.Length)) isRef.Add((int)x.Imm);
            }
            var stack = new List<Sym?>();
            var vals = new Dictionary<int, Sym>();
            var avail = new Dictionary<string, int>();      // load key -> the first load's index
            var hoisted = new Dictionary<string, int>();    // load key -> the local that holds it
            availDeps.Clear();
            Sym? Pop() { if (stack.Count == 0) return null; var s = stack[^1]; stack.RemoveAt(stack.Count - 1); return s; }
            void ClearAll() { stack.Clear(); vals.Clear(); avail.Clear(); availDeps.Clear(); }
            void Assigned(int x)
            {
                List<string>? gone = null;
                foreach (var (k, d) in availDeps) if (Array.IndexOf(d, x) >= 0) (gone ??= []).Add(k);
                if (gone != null) foreach (var k in gone) { avail.Remove(k); availDeps.Remove(k); }
                List<int>? gl = null;
                foreach (var (k, s) in vals) if (Array.IndexOf(s.Deps, x) >= 0) (gl ??= []).Add(k);
                if (gl != null) foreach (var k in gl) vals.Remove(k);
                for (int k = 0; k < stack.Count; k++) if (stack[k] is { } s && Array.IndexOf(s.Deps, x) >= 0) stack[k] = null;
            }
            for (int i = l + 1; i < e; i++)
            {
                var x = b[i];
                switch (x.Op)
                {
                    case Op.Block: continue;
                    case Op.If:
                        Pop();
                        // (a body that does not come back, a null check's throw or a `continue`: what follows it is
                        // what came before it)
                        if (!hasElse[i] && end[i] > i + 1 && (b[end[i] - 1].Op is Op.Unreachable or Op.Br or Op.Return or Op.Throw || Throws(b[end[i] - 1]))) { i = end[i]; continue; }
                        continue;
                    case Op.Else: case Op.End: ClearAll(); continue;
                    case Op.Br: case Op.Return: case Op.Unreachable: case Op.Throw: continue;
                    case Op.BrIf: case Op.BrTable: Pop(); continue;
                    case Op.LocalGet:
                        {
                            int v = (int)x.Imm;
                            stack.Add(vals.TryGetValue(v, out var s) ? s : new Sym("L" + v, [v], v, 0));
                            continue;
                        }
                    case Op.LocalSet: case Op.LocalTee:
                        {
                            int v = (int)x.Imm;
                            var s = Pop();
                            Assigned(v);
                            if (s != null && Array.IndexOf(s.Deps, v) >= 0) s = null;
                            if (s != null) vals[v] = s;
                            if (x.Op == Op.LocalTee) stack.Add(s);
                            continue;
                        }
                    case Op.I32Const:
                        stack.Add(x.Ref == null ? new Sym("#" + x.Imm, [], -2, (int)x.Imm) : null);
                        continue;
                    case Op.I32Add: case Op.I32Sub: case Op.I32Mul: case Op.I32Shl:
                        {
                            var r = Pop(); var a = Pop();
                            if (a == null || r == null) { stack.Add(null); continue; }
                            if (x.Op == Op.I32Add && a.Base == -2 && r.Base != -2) (a, r) = (r, a);
                            var deps = a.Deps.Length == 0 ? r.Deps : r.Deps.Length == 0 ? a.Deps : [.. a.Deps.Union(r.Deps)];
                            if (x.Op == Op.I32Add && r.Base == -2 && a.Base != -1)
                            {
                                long off = a.Off + r.Off;
                                stack.Add(a.Base == -2 ? new Sym("#" + (int)off, [], -2, (int)off) : new Sym("L" + a.Base + "+" + off, deps, a.Base, off));
                            }
                            else stack.Add(new Sym("(" + a.Key + " " + (int)x.Op + " " + r.Key + ")", deps, -1, 0));
                            continue;
                        }
                }
                if (IsLoad(x.Op))
                {
                    var a = Pop();
                    if (a == null) { stack.Add(null); continue; }
                    string key = (int)x.Op + "@" + a.Key + "+" + x.Imm;
                    if (hoisted.TryGetValue(key, out int h))
                    {
                        (replace ??= [])[i] = h;
                        stack.Add(new Sym("H" + h, [], -1, 0));
                        continue;
                    }
                    long at = a.Off + x.Imm;
                    if (a.Base >= 0 && !assigned.Contains(a.Base) && isRef.Contains(a.Base) && at >= 0 && at < 32768)
                    {
                        h = firstLocal + f.Locals.Count;
                        f.Locals.Add(LoadType(x.Op));
                        hoisted[key] = h;
                        var pre = (before ??= []).TryGetValue(l, out var list) ? list : before[l] = [];
                        pre.Add(new Instr { Op = Op.LocalGet, Imm = a.Base, Pos = x.Pos });
                        if (a.Off != 0) { pre.Add(new Instr { Op = Op.I32Const, Imm = a.Off, Pos = x.Pos }); pre.Add(new Instr { Op = Op.I32Add, Pos = x.Pos }); }
                        pre.Add(x);
                        pre.Add(new Instr { Op = Op.LocalSet, Imm = h, Pos = x.Pos });
                        (replace ??= [])[i] = h;
                        stack.Add(new Sym("H" + h, [], -1, 0));
                        continue;
                    }
                    if (avail.TryGetValue(key, out int first))
                    {
                        keep ??= [];
                        if (!keep.TryGetValue(first, out h)) { h = firstLocal + f.Locals.Count; f.Locals.Add(LoadType(x.Op)); keep[first] = h; }
                        (replace ??= [])[i] = h;
                        stack.Add(new Sym("V" + first, [], -1, 0));
                        continue;
                    }
                    avail[key] = i;
                    availDeps[key] = a.Deps;
                    stack.Add(new Sym("V" + i, [], -1, 0));
                    continue;
                }
                var eff = Locals.PlainEffect(x);
                if (eff == null || x.Op == Op.Call) { ClearAll(); continue; }
                for (int k = 0; k < eff.Value.pop; k++) Pop();
                for (int k = 0; k < eff.Value.push; k++) stack.Add(null);
            }
            l = e;
        }
        if (replace == null) return false;
        var o = new List<Instr>(n + 16);
        for (int i = 0; i < n; i++)
        {
            if (before != null && before.TryGetValue(i, out var pre)) o.AddRange(pre);
            if (replace.TryGetValue(i, out int h))
            {
                o.Add(new Instr { Op = Op.Drop, Pos = b[i].Pos });
                o.Add(new Instr { Op = Op.LocalGet, Imm = h, Pos = b[i].Pos });
                continue;
            }
            o.Add(b[i]);
            if (keep != null && keep.TryGetValue(i, out h)) o.Add(new Instr { Op = Op.LocalTee, Imm = h, Pos = b[i].Pos });
        }
        f.Body = o;
        return true;
    }

    [ThreadStatic] static Dictionary<string, int[]>? t_deps;
    static Dictionary<string, int[]> availDeps => t_deps ??= [];

    /// <summary>An innermost loop that writes nothing to memory and calls nothing but helpers that throw.</summary>
    static bool Clean(List<Instr> b, int l, int e)
    {
        for (int k = l + 1; k < e; k++)
        {
            var x = b[k];
            var op = x.Op;
            if (op is Op.Loop or Op.TryTable or Op.CallIndirect or Op.GlobalSet or Op.MemoryGrow or Op.MemoryCopy or Op.MemoryFill or Op.MemoryInit or Op.V128Store or Op.ThrowRef) return false;
            if (op >= Op.I32Store && op <= Op.I64Store32) return false;
            if ((int)op >= 0xFE00) return false;
            // (a helper that throws does not come back; the `unreachable` after it is written later, NoReturn.cs)
            if (op == Op.Call && !Throws(x)) return false;
        }
        return true;
    }
}
