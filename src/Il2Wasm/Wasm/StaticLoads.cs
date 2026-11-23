namespace Il2Wasm.Wasm;

/// <summary>
/// Section 5: a static field's reference read again on every turn of a loop. A loop that walks a list a static holds
/// (`for (...; i &lt; s_list.Count; ...) { var x = s_list[i]; ... s_list[w++] = x; }`) reads the static's slot for
/// every use, and checks it for null every time: the loop stores to memory and calls, so LoopLoads leaves it, and the
/// engine has no way to know the slot is not one of the stores'. The compiler knows which code stores a static
/// (Driver.FieldWrites, with everything a function calls: Driver.ResolveStaticLoads): where nothing the function
/// runs can store it, the function is given the slot (WasmFunction.StableStatics), and here each outermost loop reads
/// such a slot once, before the loop, into a local.
///
/// The read before the loop is of a slot in the statics' memory: it cannot fail, and it runs also when the loop's
/// body would not have read. What it reads is what every read in the loop would have read: no store of the field runs
/// between the loop's start and its end. The type's static initializer is the one store the summary leaves out (its
/// own stores of its type's statics): a read in the loop comes after the check that runs the initializer, and when
/// that check is not inside the loop it was made before the loop, or the code is the initializer's own; so the
/// initializer has run, or is running further up the stack, and stores nothing while the loop runs. A loop that
/// refers to the type's initialization flag at all (a check that stayed in it, an initializer inlined there) is left.
/// The local holds no count: the static holds its object for as long as the function runs.
/// </summary>
static class StaticLoads
{
    /// <summary>Returns whether the function changed.</summary>
    public static bool Run(WasmFunction f)
    {
        var stable = f.StableStatics;
        if (stable == null) return false;
        var b = f.Body;
        int n = b.Count;
        // the outermost loops: [start, end]
        List<(int l, int e)>? loops = null;
        {
            var open = new Stack<(int at, bool loop)>();
            int depth = 0;
            for (int i = 0; i < n; i++)
            {
                var op = b[i].Op;
                if (op is Op.Block or Op.Loop or Op.If or Op.TryTable) { open.Push((i, op == Op.Loop)); if (op == Op.Loop) depth++; }
                else if (op == Op.End && open.Count > 0)
                {
                    var (at, loop) = open.Pop();
                    if (loop && --depth == 0) (loops ??= []).Add((at, i));
                }
            }
        }
        if (loops == null) return false;
        static bool ReadAt(List<Instr> b, int i, int e) => i + 1 < e && b[i].Op == Op.I32Const && b[i].Ref != null && b[i].Imm == 0 && b[i + 1].Op == Op.I32Load && b[i + 1].Imm == 0 && b[i + 1].Ref == null;
        // (from the last loop to the first: the positions of the earlier ones stay)
        bool changed = false;
        int firstLocal = f.Type.Params.Length;
        for (int li = loops.Count - 1; li >= 0; li--)
        {
            var (l, e) = loops[li];
            Dictionary<object, int>? local = null;
            HashSet<object>? refused = null;
            for (int i = l + 1; i < e; i++)
            {
                if (!ReadAt(b, i, e) || !stable.TryGetValue(b[i].Ref!, out var flag)) continue;
                var slot = b[i].Ref!;
                if (refused != null && refused.Contains(slot)) continue;
                if (local != null && local.ContainsKey(slot)) continue;
                bool ok = true;
                if (flag != null) for (int k = l + 1; k < e && ok; k++) if (ReferenceEquals(b[k].Ref, flag)) ok = false;
                if (!ok) { (refused ??= new(ReferenceEqualityComparer.Instance)).Add(slot); continue; }
                (local ??= new(ReferenceEqualityComparer.Instance))[slot] = firstLocal + f.Locals.Count;
                f.Locals.Add(ValType.I32);
            }
            if (local == null) continue;
            var o = new List<Instr>(n + 3 * local.Count);
            // (before the block the loop leaves by, when it opens right before the loop: `block; loop` stays together)
            int at = l;
            while (at > 0 && b[at - 1].Op == Op.Block && b[at - 1].Ref == null) at--;
            for (int i = 0; i < at; i++) o.Add(b[i]);
            foreach (var (slot, h) in local)
            {
                o.Add(new Instr { Op = Op.I32Const, Ref = slot, Pos = -1 });
                o.Add(new Instr { Op = Op.I32Load, Imm = 0, Imm2 = 2, Pos = -1 });
                o.Add(new Instr { Op = Op.LocalSet, Imm = h, Pos = -1 });
            }
            for (int i = at; i < n; i++)
            {
                if (i > l && i < e && ReadAt(b, i, e) && local.TryGetValue(b[i].Ref!, out int h))
                {
                    o.Add(b[i + 1] with { Op = Op.LocalGet, Imm = h, Imm2 = 0, Ref = null });
                    i++;
                }
                else o.Add(b[i]);
            }
            b = o; n = b.Count;
            changed = true;
        }
        if (changed) f.Body = b;
        return changed;
    }
}
