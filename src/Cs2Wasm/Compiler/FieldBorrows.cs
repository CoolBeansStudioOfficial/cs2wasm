using Cs2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Cs2Wasm.Compiler;

// Locals that borrow from a field (section 4.1: "a variable that is never reassigned borrows from its source for its
// scope"). `var x = this.f;` (or `p.f` for a parameter never assigned) would retain the field's object and release it
// when x goes out of scope. The object is already kept alive by the field, as long as nothing writes the field
// meanwhile: so when no code the function can run (itself and everything it calls, through dispatch stubs and the
// function table too) stores to that field, its address is never taken, and no other thread can run, x holds no
// count. Decided once the whole program is compiled: the declaration stores through a placeholder that becomes the
// retain, or nothing; on success the local's releases (scope exit, unwinding) go too.
sealed partial class FunctionCompiler
{
    /// <summary>The field a declaration can borrow from (key), when the local and its source qualify.</summary>
    string? FieldBorrowCandidate(ILocalSymbol l, IOperation init)
    {
        if (l.IsRef || IsCaptured(l) || WrittenLocals().Contains(l) || _closure != null || _machine != null) return null;
        while (init is IConversionOperation { OperatorMethod: null } c && (c.Conversion.IsIdentity || c.Conversion.IsReference && c.Conversion.IsImplicit)) init = c.Operand;
        if (init is not IFieldReferenceOperation { Field.IsStatic: false, Instance: { } inst } fr) return null;
        if (!Types.IsRef(T.Resolve(fr.Field.Type, Map)) || fr.Field.ContainingType.TypeKind != TypeKind.Class) return null;
        switch (inst)
        {
            case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } when M.ContainingType.TypeKind == TypeKind.Class: break;
            case IParameterReferenceOperation { Parameter: { RefKind: RefKind.None } p } when !IsCaptured(p) && !_assignedParams.Contains(p) && !_assignedParams.Contains(p.OriginalDefinition) && !TypeOf(inst).IsValueType: break;
            default: return null;
        }
        return Driver.FieldKey(Owner(fr.Field), StorageField(fr.Field));
    }

    /// <summary>The declaration of a borrowing candidate: the field's value goes to the local through the placeholder.</summary>
    void EmitFieldBorrow(Var v, IOperation init, string key)
    {
        var o = EmitValue(init);
        ConvertTop(TypeOf(init), v.Type, ref o);
        var marker = D.Module.AddFunction("borrow? " + key, new FuncType([ValType.I32], [ValType.I32]));
        if (o == Own.Owned) { E.Set(v.Local); return; }
        E.Call(marker);
        E.Set(v.Local);
        D.FieldBorrows.Add((E.Fn, v.Local, key, marker));
    }
}

internal sealed partial class Driver
{
    public static string FieldKey(ITypeSymbol owner, ISymbol storage) => Types.Key(owner) + "::" + storage.Name;
    /// <summary>The instance fields each function stores to (by key).</summary>
    public readonly Dictionary<WasmFunction, HashSet<string>> FieldWrites = [];
    /// <summary>Fields whose address code takes (ref, out, Interlocked): written by whoever holds the address.</summary>
    public readonly HashSet<string> FieldsAddressTaken = [];
    public readonly List<(WasmFunction fn, int local, string field, WasmFunction marker)> FieldBorrows = [];
    /// <summary>Temps holding a field of a stable object for a call: the retain and the release placeholders.</summary>
    readonly List<(WasmFunction fn, string field, WasmFunction retain, WasmFunction release)> _tempBorrows = [];

    public (WasmFunction retain, WasmFunction release) NewFieldBorrow(WasmFunction fn, string key)
    {
        var r = Module.AddFunction("borrow? " + key, new FuncType([ValType.I32], [ValType.I32]));
        var d = Module.AddFunction("unborrow? " + key, new FuncType([ValType.I32], []));
        _tempBorrows.Add((fn, key, r, d));
        return (r, d);
    }

    public void NoteFieldWrite(WasmFunction fn, string key)
    {
        if (!FieldWrites.TryGetValue(fn, out var s)) FieldWrites[fn] = s = [];
        s.Add(key);
    }

    /// <summary>Resolves the placeholders (RuntimeGen.Finish, when every function exists).</summary>
    public void ResolveFieldBorrows()
    {
        if (FieldBorrows.Count == 0 && _tempBorrows.Count == 0) return;
        var byFn = FieldBorrows.GroupBy(b => b.fn).ToDictionary(g => g.Key, g => g.ToList());
        bool threads = UsesThreads;
        // what each function may store, with everything it can call (an indirect call: any function in the table)
        Dictionary<WasmFunction, HashSet<string>>? summary = null;
        if (!threads)
        {
            var tableFns = new HashSet<WasmFunction>();
            foreach (var f in Module.Functions)
                foreach (var ins in f.Body)
                    if (ins.Op == Op.I32Const && ins.Ref is WasmFunction t) tableFns.Add(t);
            foreach (var f in Module.Functions) if (f.InTable || f.TableRoot) tableFns.Add(f);
            summary = [];
            var callees = new Dictionary<WasmFunction, List<WasmFunction>>();
            foreach (var f in Module.Functions)
            {
                var cs = new List<WasmFunction>();
                bool indirect = false;
                foreach (var ins in f.Body)
                {
                    if (ins.Op == Op.Call && ins.Ref is WasmFunction g) cs.Add(g);
                    else if (ins.Op == Op.CallIndirect) indirect = true;
                }
                if (f.ForwardTo != null) cs.Add(f.ForwardTo);
                if (indirect) cs.AddRange(tableFns);
                callees[f] = cs;
                summary[f] = FieldWrites.TryGetValue(f, out var w) ? [.. w] : [];
            }
            // (to a fixpoint: summaries only grow)
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var f in Module.Functions)
                {
                    var s = summary[f];
                    int before = s.Count;
                    foreach (var g in callees[f]) if (g != f && summary.TryGetValue(g, out var gs)) s.UnionWith(gs);
                    if (s.Count != before) changed = true;
                }
            }
        }
        var reflected = ReflectedFieldKeys();
        foreach (var (fn, list) in byFn)
        {
            var b = fn.Body;
            foreach (var (_, local, field, marker) in list)
            {
                bool ok = !threads && summary != null && !summary[fn].Contains(field) && !FieldsAddressTaken.Contains(field) && !reflected.Contains(field);
                // the local's other writes are the releases at scope exit (get; release; 0; set): nothing moves its value
                if (ok)
                    for (int i = 0; i < b.Count && ok; i++)
                        if (b[i].Op is Op.LocalSet or Op.LocalTee && b[i].Imm == local && !(i > 0 && b[i - 1].Op == Op.Call && b[i - 1].Ref == marker))
                            ok = i >= 3 && b[i - 1].Op == Op.I32Const && b[i - 1].Imm == 0 && b[i - 1].Ref == null
                                 && b[i - 2].Op == Op.Call && b[i - 2].Ref == Rt.Release && b[i - 3].Op == Op.LocalGet && b[i - 3].Imm == local;
                for (int i = 0; i < b.Count; i++)
                {
                    if (b[i].Op == Op.Call && b[i].Ref == marker)
                        b[i] = ok ? b[i] with { Op = Op.Nop, Ref = null } : b[i] with { Ref = Rt.Retain };
                    else if (ok && b[i].Op == Op.LocalGet && b[i].Imm == local && i + 1 < b.Count && b[i + 1].Op == Op.Call && b[i + 1].Ref == Rt.Release)
                    {
                        b[i] = b[i] with { Op = Op.Nop };
                        b[i + 1] = b[i + 1] with { Op = Op.Nop, Ref = null };
                    }
                }
            }
            fn.Body.RemoveAll(x => x.Op == Op.Nop && x.Ref == null && x.Pos < 0);
        }
        // the temps: the retain and the release go together
        var markers = new Dictionary<WasmFunction, WasmFunction?>();
        foreach (var (fn, field, retain, release) in _tempBorrows)
        {
            bool ok = !threads && summary != null && !FieldsAddressTaken.Contains(field) && !reflected.Contains(field);
            // the borrow lasts from the retain to the statement's release: only what runs in between may not store the
            // field (the function's own stores anywhere: the whole function's summary decides)
            if (ok && FieldWrites.TryGetValue(fn, out var own) && own.Contains(field)) ok = false;
            if (ok)
            {
                var b = fn.Body;
                int r = b.FindIndex(x => x.Op == Op.Call && x.Ref == retain);
                int e = r < 0 ? -1 : b.FindIndex(r + 1, x => x.Op == Op.Call && x.Ref == release);
                if (r < 0 || e < 0) ok = !summary![fn].Contains(field);
                else
                    for (int k = r + 1; k < e && ok; k++)
                    {
                        var ins = b[k];
                        if (ins.Op == Op.Call && ins.Ref is WasmFunction g && g != retain && summary!.TryGetValue(g, out var gs) && gs.Contains(field)) ok = false;
                        else if (ins.Op == Op.CallIndirect && summary![fn].Contains(field)) ok = false;
                    }
            }
            markers[retain] = ok ? null : Rt.Retain;
            markers[release] = ok ? null : Rt.Release;
        }
        if (markers.Count > 0)
            foreach (var f in Module.Functions)
            {
                var b = f.Body;
                bool any = false;
                for (int i = 0; i < b.Count; i++)
                    if (b[i].Op == Op.Call && b[i].Ref is WasmFunction g && markers.TryGetValue(g, out var to))
                    {
                        any = true;
                        // (retain → the value stays; release → its operand is dropped)
                        if (to != null) b[i] = b[i] with { Ref = to };
                        else b[i] = g.Type.Results.Length == 1 ? b[i] with { Op = Op.Nop, Ref = null } : b[i] with { Op = Op.Drop, Ref = null };
                    }
                if (any) b.RemoveAll(x => x.Op == Op.Nop && x.Ref == null && x.Pos < 0);
            }
        foreach (var m in markers.Keys) Module.Functions.Remove(m);
        foreach (var (_, _, _, marker) in FieldBorrows) Module.Functions.Remove(marker);
    }

    /// <summary>Fields reflection can set (kept for the program's reflection: FieldInfo.SetValue writes them).</summary>
    HashSet<string> ReflectedFieldKeys()
    {
        var r = new HashSet<string>();
        if (_reflect == null) return r;
        foreach (var (t, all, names, ctors) in _reflect.Types.Values)
            foreach (var m in KeptMembers(t, all, names, ctors))
                if (m is IFieldSymbol f && !f.IsStatic) r.Add(FieldKey(Types.Canonical(f.ContainingType), f));
        return r;
    }
}
