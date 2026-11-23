using Il2Wasm.Wasm;
using Microsoft.CodeAnalysis;

namespace Il2Wasm.Compiler;

/// <summary>
/// Which functions can read a static's reference once before a loop (Wasm/StaticLoads.cs does it): those that, with
/// everything they call, never store the static. Decided with the borrows (ResolveFieldBorrows), when every function
/// exists and the stores still carry their marks. The conditions, each of which a store breaks:
///  - the field: a static of a reference type, not [ThreadStatic], read with a plain load (not in a program with
///    threads: another thread may store the static meanwhile, and release what it held);
///  - no code takes its address (`ref`, `out`, Interlocked, Volatile), and reflection cannot set it;
///  - the function stores it nowhere: FieldWrites, which has the stores that only replace a null (`s ??= v`: the
///    borrows leave those out, a read must not) and the stores without a place;
///  - nothing it calls stores it: the summary of all stores (StoreSummary under AllStoresPrefix), an indirect call
///    by what it can reach, a call of the host by what the host can call back (the exports, the table);
///  - the type's static initializer: its own stores are not in the summary. The loop must not refer to the type's
///    initialization flag (StaticLoads.Run: no check of it, no initializer inlined), so the check that came before a
///    read in the loop was made before the loop, and the initializer has run or is running further up the stack.
/// IL2WASM_NO_STATIC_HOIST=1: off.
/// </summary>
internal sealed partial class Driver
{
    static readonly bool s_noStaticHoist = Environment.GetEnvironmentVariable("IL2WASM_NO_STATIC_HOIST") == "1";
    static readonly bool s_staticHoistDebug = Environment.GetEnvironmentVariable("IL2WASM_STATIC_HOIST_DEBUG") == "1";
    /// <summary>Before a field's key: the key of all its stores in a StoreSummary (those that only replace a null too).</summary>
    internal const string AllStoresPrefix = "\u0001";
    readonly Dictionary<object, (string key, object? flag)> _staticRefSlots = new(ReferenceEqualityComparer.Instance);

    /// <summary>A static of a reference type read with a plain load from `slot`: its key (FieldKey), its type's
    /// initialization flag (null: no initializer).</summary>
    internal void NoteStaticRefRead(IAddress slot, string key, IAddress? flag)
    {
        if (!s_noStaticHoist) _staticRefSlots.TryAdd(slot, (key, flag));
    }

    /// <summary>The functions with a loop that read such a static, each with the slots it reads.</summary>
    Dictionary<WasmFunction, List<object>> StaticLoadCandidates()
    {
        var r = new Dictionary<WasmFunction, List<object>>();
        // (threads: only the engine thread's own statics are noted, FunctionCompiler.EngineStatic)
        if (_staticRefSlots.Count == 0 || UsesThreads && !ThreadMode) return r;
        foreach (var f in Module.Functions)
        {
            if (f.IsImport) continue;
            var b = f.Body;
            bool loop = false;
            List<object>? l = null;
            for (int i = 0; i < b.Count; i++)
            {
                if (b[i].Op == Op.Loop) loop = true;
                else if (b[i].Op == Op.I32Const && b[i].Ref is { } s && b[i].Imm == 0 && i + 1 < b.Count && b[i + 1].Op == Op.I32Load && b[i + 1].Imm == 0
                    && _staticRefSlots.ContainsKey(s) && !(l ??= []).Contains(s)) l.Add(s);
            }
            if (loop && l != null) r[f] = l;
        }
        return r;
    }

    void ResolveStaticLoads(Dictionary<WasmFunction, List<object>> cands, StoreSummary summary)
    {
        if (cands.Count == 0) return;
        var reflected = new HashSet<string>();
        if (_reflect != null)
            foreach (var (t, all, names, ctors) in _reflect.Types.Values)
                foreach (var m in KeptMembers(t, all, names, ctors))
                    if (m is IFieldSymbol { IsStatic: true } sf) reflected.Add(FieldKey(Types.Canonical(sf.ContainingType), sf));
        // (what the host can call back: an export, a function in the table)
        var host = new Dictionary<string, bool>();
        bool HostMayStore(string q)
        {
            if (host.TryGetValue(q, out var known)) return known;
            bool r = summary.HasIndirect(q, new Instr { Op = Op.CallIndirect });
            if (!r) foreach (var x in Module.Functions) if (x.Exports.Count > 0 && !x.IsImport && summary.Has(x, q)) { r = true; break; }
            return host[q] = r;
        }
        foreach (var (f, slots) in cands)
            foreach (var slot in slots)
            {
                var (key, flag) = _staticRefSlots[slot];
                string? why = null;
                if (FieldsAddressTaken.Contains(key)) why = "address taken";
                else if (reflected.Contains(key)) why = "reflection";
                else if (FieldWrites.TryGetValue(f, out var own) && own.Contains(key)) why = "the function stores it";
                else
                {
                    string q = AllStoresPrefix + key;
                    var b = f.Body;
                    for (int i = 0; i < b.Count && why == null; i++)
                    {
                        var ins = b[i];
                        if (ins.Op == Op.Call && StoresOf(b, i) is { } g) { if (g.IsImport ? HostMayStore(q) : summary.Has(g, q)) why = "a call of " + g.Name; }
                        else if (ins.Op == Op.CallIndirect) { if (summary.HasIndirect(q, ins)) why = "an indirect call"; }
                    }
                }
                if (s_staticHoistDebug) Console.Error.WriteLine($"static hoist {f.Name} {key}: {why ?? "yes"}");
                if (why == null) (f.StableStatics ??= new(ReferenceEqualityComparer.Instance))[slot] = flag;
            }
    }
}
