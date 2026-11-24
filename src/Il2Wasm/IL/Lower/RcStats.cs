#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Memory;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

/// <summary>Executed counts per count site (docs/MEMORY-IR.md 9.1, R11, Lobster's --rcstats): IL2WASM_COUNT_RC=1.
/// The sites are counted where they end up, after the back end's passes (Module.AfterOptimize): every call of
/// rt.retain and rt.release in the module, in the program's, the library's, the runtime's and the generated functions
/// (destroy, dispatch, the struct helpers), gets a counter of its own. A call to a struct helper (rt.retain_struct,
/// rt.release_struct) is a site too, of calls: tools/il-rcstats.mjs charges the helper's own count operations to the
/// sites that call it. At each object count operation the object's count before it is sorted into buckets (null,
/// immortal, 1, 2, 3-7, 8-63, 64 and more): "popular" is a count of 8 or more (Shahriyar's 3-bit overflow), and an
/// operation "changes a count" when the object is not null and not immortal.
///
/// The table is a zeroed static block: a header (the buckets, by retain and release), then three i64 per site
/// (executed, changed a count, popular). The export il2wasm_rcstats gives its address, il2wasm_rcstats_sites the
/// number of sites; host/run.mjs writes it out at exit (IL2WASM_RCSTATS_OUT=file), and IL2WASM_COUNT_RC_SITES=file
/// gets the site table: one line per site (id, kind, owner, role, category, function, cause, context), then the types
/// by id. With IL2WASM_IL_COUNT_OBJECTS=1 as well, the objects allocated and freed are counted per type too (live
/// objects at exit per type, MEMORY-IR 9.2).
///
/// A site's role (what the count is for: a parameter, a local, a field, an element, a static, a return...) is the
/// lowering's tag when FunctionLowering called <see cref="Count"/> right after emitting the count call, else what the
/// compiler method that emitted the call and the instructions around it say (runtime and generated code, and code the
/// lowering does not tag).
/// Off (the default), nothing here runs: the module is byte-identical.</summary>
public sealed class RcStats
{
    public static readonly bool On = Environment.GetEnvironmentVariable("IL2WASM_COUNT_RC") == "1";
    public static readonly string? SitesFile = Environment.GetEnvironmentVariable("IL2WASM_COUNT_RC_SITES") is { Length: > 0 } f ? f : null;

    /// <summary>Bytes before the first site: the buckets of retains (8 i64), of releases (8 i64), then the calls that
    /// entered rt.retain and rt.release (an i64 each; the rest unused).</summary>
    const int Header = 256, SiteBytes = 24, ReleaseBuckets = 64, EnteredRetain = 128, EnteredRelease = 136;

    readonly IlCompiler _c;
    internal RcStats(IlCompiler c) { _c = c; }

    // ------------------------------------------------------------------ the lowering's tags

    readonly HashSet<ushort> _tags = [];
    readonly Dictionary<WasmFunction, int> _lastTagged = [];
    readonly List<(WasmFunction Fn, int At, ushort Tag)> _pending = [];

    static bool IsCountCall(in Instr x) => x.Op == WOp.Call && x.Ref is WasmFunction g && IsCountFn(g.Name);
    static bool IsCountFn(string n) => n is "rt.retain" or "rt.release" || n.StartsWith("rt.retain_struct ", StringComparison.Ordinal) || n.StartsWith("rt.release_struct ", StringComparison.Ordinal);

    /// <summary>A new site (dense id, in lowering order: stable, since lowering order is) for one count operation.
    /// The sites counted are the module's count calls after optimization, numbered there; this gives -1.</summary>
    internal int Site(MethodDesc method, Node countOp, RcCategory category) => -1;

    /// <summary>The counter of a site, at the count operation: FunctionLowering calls it right after it emitted the
    /// count call (rt.retain, rt.release, a struct helper). Nothing is emitted: the call just emitted is tagged with the
    /// category and the role of the node, and the counter is put beside it after optimization (a tag rides along when
    /// the call is inlined). A count call of the node not yet emitted is tagged when it is (the next untagged one).</summary>
    internal void Count(Emitter e, MethodDesc method, Node countOp, RcCategory category)
    {
        ushort tag = Emitter.CauseOf("tag " + category + " " + RoleOf(countOp));
        lock (_tags)
        {
            _tags.Add(tag);
            var body = e.Fn.Body;
            int from = _lastTagged.TryGetValue(e.Fn, out int l) ? l : 0;
            for (int i = body.Count - 1; i >= from; i--)
            {
                if (!IsCountCall(body[i]) || _tags.Contains(body[i].Cause)) continue;
                var x = body[i]; x.Cause = tag; body[i] = x;
                _lastTagged[e.Fn] = i + 1;
                return;
            }
            _pending.Add((e.Fn, body.Count, tag));
        }
    }

    void ResolvePending()
    {
        foreach (var (fn, at, tag) in _pending)
        {
            var body = fn.Body;
            for (int i = at; i < body.Count; i++)
            {
                if (!IsCountCall(body[i]) || _tags.Contains(body[i].Cause)) continue;
                var x = body[i]; x.Cause = tag; body[i] = x;
                break;
            }
        }
        _pending.Clear();
    }

    /// <summary>What a count operation is for, from its node: the slot a load or store is of, else the source of the
    /// value counted (a Retain or Release of an argument is a parameter's, of a call's result a return's).</summary>
    static string RoleOf(Node n) => n.Op switch
    {
        Opc.Retain or Opc.Release => n.A.Length > 0 ? SourceOf(n.A[0], 0) : "other",
        Opc.LdFld or Opc.StFld => "field",
        Opc.LdElem or Opc.StElem => "element",
        Opc.LdSFld or Opc.StSFld => "static",
        Opc.VarLd or Opc.VarSt or Opc.InitObj => "local",
        Opc.LdInd or Opc.StInd or Opc.LdObj => "indirect",
        Opc.Ret => "return",
        Opc.Throw or Opc.Rethrow or Opc.CatchVal => "exception",
        Opc.Phi => "phi",
        Opc.Call or Opc.CallI or Opc.NewObj => "argument",
        Opc.CastClass or Opc.IsInst => "cast",
        Opc.Arg => "parameter",
        Opc.Box => "box",
        _ => n.Op.ToString().ToLowerInvariant(),
    };

    static string SourceOf(Node v, int depth) => v.Op switch
    {
        Opc.Arg => "parameter",
        Opc.LdFld => "field",
        Opc.LdElem => "element",
        Opc.LdSFld => "static",
        Opc.VarLd => "local",
        Opc.LdInd or Opc.LdObj => "indirect",
        Opc.Call or Opc.CallI => "return",
        Opc.NewObj or Opc.NewArr or Opc.Box => "new",
        Opc.Phi => "phi",
        Opc.CatchVal => "exception",
        Opc.CastClass or Opc.IsInst when v.A.Length > 0 && depth < 8 => SourceOf(v.A[0], depth + 1),
        _ => "other",
    };

    // ------------------------------------------------------------------ the table

    IAddress _table = null!;
    int _capacity;
    WasmFunction _retainHit = null!, _releaseHit = null!, _callHit = null!, _sitesFn = null!;
    IAddress? _live;
    int _liveSlots;

    /// <summary>The table, its counting functions and exports, and the hook that puts the counters at the sites after
    /// optimization (from GenerateLate, once every method is lowered).</summary>
    internal void EmitLate()
    {
        ResolvePending();
        // (the capacity: the count calls so far, which inlining copies, and the generated functions' to come;
        // IL2WASM_COUNT_RC_MAX sets it, and sites past it share the last counter, which the host reports)
        int calls = 0;
        foreach (var f in _c.Module.Functions) foreach (var x in f.Body) if (x.Op == WOp.Call && x.Ref is WasmFunction g && (IsCountFn(g.Name) || g.Name is "rt.retain_contents")) calls++;
        _capacity = int.TryParse(Environment.GetEnvironmentVariable("IL2WASM_COUNT_RC_MAX"), out int cap) && cap > 0 ? cap : Math.Max(1 << 14, 3 * calls + (1 << 14));
        _table = _c.NamedStatic("rcstats.table", Header + SiteBytes * (_capacity + 1), 8);

        // (IL2WASM_IL_COUNT_OBJECTS: objects per type, made and freed)
        if (_c.CountObjects)
        {
            _liveSlots = _c.RcTypeCount() + 4096;
            _live = _c.NamedStatic("rcstats.live", 8 * _liveSlots, 8);
        }

        _retainHit = ObjectHit("rcstats.retain", 0);
        _releaseHit = ObjectHit("rcstats.release", ReleaseBuckets);
        _callHit = _c.Module.AddFunction("rcstats.call", new FuncType([ValType.I32], []));
        {
            var e = new Emitter(_callHit);
            SiteAddr(e, 0); e.I64(1); e.Emit(WOp.I64AtomicRmwAdd, Header, 3); e.Op(WOp.Drop);
        }
        _callHit.Exports.Add("il2wasm_rcstats_call");
        var tf = _c.Module.AddFunction("il2wasm_rcstats", new FuncType([], [ValType.I32]));
        tf.Exports.Add("il2wasm_rcstats");
        new Emitter(tf).I32Addr(_table);
        // (the number of sites: its body is written once they are known)
        _sitesFn = _c.Module.AddFunction("il2wasm_rcstats_sites", new FuncType([], [ValType.I32]));
        _sitesFn.Exports.Add("il2wasm_rcstats_sites");
        new Emitter(_sitesFn).I32(0);
        if (_live != null)
        {
            var lf = _c.Module.AddFunction("il2wasm_rcstats_live", new FuncType([], [ValType.I32]));
            lf.Exports.Add("il2wasm_rcstats_live");
            new Emitter(lf).I32Addr(_live);
            var ln = _c.Module.AddFunction("il2wasm_rcstats_live_slots", new FuncType([], [ValType.I32]));
            ln.Exports.Add("il2wasm_rcstats_live_slots");
            new Emitter(ln).I32(_liveSlots);
        }
        _c.Module.AfterOptimize = Instrument;
    }

    /// <summary>The address of site (local s) in the table, on the stack.</summary>
    void SiteAddr(Emitter e, int s)
    {
        e.Get(s); e.I32(_capacity); e.Get(s); e.I32(_capacity); e.Op(WOp.I32LtU); e.Op(WOp.Select);
        e.I32(SiteBytes); e.Op(WOp.I32Mul); e.I32Addr(_table); e.Op(WOp.I32Add);
    }

    /// <summary>(object, site) -> object: the site's counters and the object's count's bucket.</summary>
    WasmFunction ObjectHit(string name, int buckets)
    {
        var f = _c.Module.AddFunction(name, new FuncType([ValType.I32, ValType.I32], [ValType.I32]));
        f.Exports.Add("il2wasm_" + name.Replace('.', '_'));
        var e = new Emitter(f);
        int c = e.AddLocal(ValType.I32), b = e.AddLocal(ValType.I32), a = e.AddLocal(ValType.I32);
        // (address 0 is readable; its word is not used when the object is null)
        e.Get(0); e.Load(WOp.I32Load); e.Set(c);
        e.I32(6); e.Set(b);
        void Step(int bucket, Action cond) { e.I32(bucket); e.Get(b); cond(); e.Op(WOp.Select); e.Set(b); }
        Step(5, () => { e.Get(c); e.I32(64); e.Op(WOp.I32LtS); });
        Step(4, () => { e.Get(c); e.I32(8); e.Op(WOp.I32LtS); });
        Step(3, () => { e.Get(c); e.I32(2); e.Op(WOp.I32Eq); });
        Step(2, () => { e.Get(c); e.I32(1); e.Op(WOp.I32Eq); });
        Step(1, () => { e.Get(c); e.I32(1); e.Op(WOp.I32LtS); });
        Step(0, () => { e.Get(0); e.Op(WOp.I32Eqz); });
        e.I32Addr(_table); e.Get(b); e.I32(3); e.Op(WOp.I32Shl); e.Op(WOp.I32Add); e.I64(1); e.Emit(WOp.I64AtomicRmwAdd, buckets, 3); e.Op(WOp.Drop);
        SiteAddr(e, 1); e.Set(a);
        e.Get(a); e.I64(1); e.Emit(WOp.I64AtomicRmwAdd, Header, 3); e.Op(WOp.Drop);
        e.Get(a); e.Get(b); e.I32(2); e.Op(WOp.I32GeU); e.Op(WOp.I64ExtendI32U); e.Emit(WOp.I64AtomicRmwAdd, Header + 8, 3); e.Op(WOp.Drop);
        e.Get(a); e.Get(b); e.I32(5); e.Op(WOp.I32GeU); e.Op(WOp.I64ExtendI32U); e.Emit(WOp.I64AtomicRmwAdd, Header + 16, 3); e.Op(WOp.Drop);
        e.Get(0);
        return f;
    }

    // ------------------------------------------------------------------ after optimization: the sites

    sealed record SiteInfo(string Kind, string Owner, string Role, string Category, string Function, string Cause, string Context);

    void Instrument(IEnumerable<WasmFunction> functions)
    {
        var fns = functions.ToList();
        var methods = new Dictionary<WasmFunction, MethodDesc>();
        var byName = new Dictionary<string, MethodDesc>();
        foreach (var (m, f) in _c.RcFunctionMap()) { methods.TryAdd(f, m); byName.TryAdd(f.Name, m); }
        var sites = new List<SiteInfo>();
        var own = new HashSet<WasmFunction> { _retainHit, _releaseHit, _callHit };
        foreach (var f in fns)
        {
            if (own.Contains(f)) continue;
            if (f.Name is "rt.retain" or "rt.release")
            {
                // (the calls that reach the function itself, against the sites' sum: a copy of it written into a
                // caller would be a count no site saw, which the host reports)
                var pre = new Emitter(new WasmFunction("tmp", f.Type));
                pre.I32Addr(_table); pre.I64(1); pre.Emit(WOp.I64AtomicRmwAdd, f.Name == "rt.retain" ? EnteredRetain : EnteredRelease, 3); pre.Op(WOp.Drop);
                f.Body.InsertRange(0, pre.Fn.Body);
                continue;
            }
            // (a body the back end shares among identical ones, "shared <name>": its first method's)
            if (!methods.TryGetValue(f, out var md) && f.Name.StartsWith("shared ", StringComparison.Ordinal)) byName.TryGetValue(f.Name[7..], out md);
            string owner = OwnerOf(f, md);
            var b = f.Body;
            for (int i = 0; i < b.Count; i++)
            {
                if (b[i].Op != WOp.Call || b[i].Ref is not WasmFunction g) continue;
                bool obj = g.Name is "rt.retain" or "rt.release";
                bool helper = !obj && (IsCountFn(g.Name) || g.Name == "rt.retain_contents");
                if (!obj && !helper) continue;
                int id = sites.Count;
                string cause = b[i].Cause > 0 && b[i].Cause <= Emitter.RcCauses.Count ? Emitter.RcCauses[b[i].Cause - 1] : "";
                var (role, cat) = Classify(f, b, i, cause, owner);
                string kind = g.Name == "rt.retain" ? "retain" : g.Name == "rt.release" ? "release" : "call " + g.Name;
                sites.Add(new SiteInfo(kind, owner, role, cat, f.Name, cause.StartsWith("tag ", StringComparison.Ordinal) ? "" : cause, Context(f, b, i)));
                var tmp = new Emitter(new WasmFunction("tmp", f.Type));
                if (obj) { tmp.I32(id); tmp.Call(g.Name == "rt.retain" ? _retainHit : _releaseHit); }
                else { tmp.I32(id); tmp.Call(_callHit); } // (the helper's operand stays below on the stack)
                b.InsertRange(i, tmp.Fn.Body);
                i += tmp.Fn.Body.Count;
            }
        }
        _sitesFn.Body.Clear();
        new Emitter(_sitesFn).I32(sites.Count);
        if (_live != null) CountObjectsPerType(fns, methods);
        if (sites.Count > _capacity) Console.Error.WriteLine($"rcstats: {sites.Count} sites, the table has {_capacity}: those past it share its last counter (IL2WASM_COUNT_RC_MAX)");
        if (SitesFile != null)
        {
            using var w = new StreamWriter(SitesFile);
            w.WriteLine($"#rcstats\t1\tsites={sites.Count}\tcapacity={_capacity}");
            for (int i = 0; i < sites.Count; i++)
            {
                var s = sites[i];
                w.WriteLine($"{i}\t{s.Kind}\t{s.Owner}\t{s.Role}\t{s.Category}\t{Clean(s.Function)}\t{Clean(s.Cause)}\t{Clean(s.Context)}");
            }
            foreach (var (id, name) in _c.RcTypeNames()) w.WriteLine($"#type\t{id}\t{Clean(name)}");
        }
    }

    static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>Whose code a function is: the program's, the library's (CoreLib's collections apart: "containers"),
    /// the runtime's (Il2Wasm.Runtime's C#), or generated (the rt.* functions, destroy, dispatch, stubs).</summary>
    static string OwnerOf(WasmFunction f, MethodDesc? m)
    {
        if (m == null) return f.Name.StartsWith("rt.retain_struct ", StringComparison.Ordinal) || f.Name.StartsWith("rt.release_struct ", StringComparison.Ordinal) ? "helper" : "generated";
        var t = m.OwningType;
        while (t is MetadataType { ContainingType: { } outer }) t = outer;
        if (t is not MetadataType mt) return "library";
        string ns = mt.GetNamespace() ?? "";
        if (ns.StartsWith("Il2Wasm.Runtime", StringComparison.Ordinal)) return "runtime";
        string asm = mt.Module is IAssemblyDesc ad ? ad.GetName().Name ?? "" : "";
        bool lib = asm is "System.Private.CoreLib" or "netstandard" or "mscorlib" || asm.StartsWith("System.", StringComparison.Ordinal) || asm.StartsWith("Microsoft.", StringComparison.Ordinal);
        if (!lib) return "program";
        if (ns.StartsWith("System.Collections", StringComparison.Ordinal)) return "containers";
        return "library";
    }

    /// <summary>A site's role and category: the lowering's tag, else the compiler method that emitted the call and the
    /// instructions before it.</summary>
    static (string Role, string Category) Classify(WasmFunction f, List<Instr> b, int i, string cause, string owner)
    {
        if (cause.StartsWith("tag ", StringComparison.Ordinal))
        {
            var parts = cause.Split(' ');
            return (parts.Length > 2 ? parts[2] : "other", parts[1]);
        }
        string first = cause.Split(" <- ")[0];
        if (owner is "generated" or "helper")
        {
            if (f.Name == "rt.destroy" || f.Name.StartsWith("destroy", StringComparison.Ordinal)) return ("destroy", "Destroy");
            return (owner == "helper" ? "struct" : "runtime", "untagged");
        }
        string role = first switch
        {
            "ReleaseVars" => "local",
            "Run" or "EmitTry" => "unwind",
            "PhiCopies" => "phi",
            "EmitTerminator" => "return",
            "StoreAt" or "StoreStruct" => IsStoreOld(b, i) ? "store-old" : "store-retain",
            "Call" or "AfterCall" or "PushCallArgs" or "PushCallArgs" or "NewObj" => "argument",
            _ => FromContext(f, b, i),
        };
        return (role, "untagged");
    }

    /// <summary>A store's release of the old value comes after the store; its retain of the new one before it.</summary>
    static bool IsStoreOld(List<Instr> b, int i) => b[i].Ref is WasmFunction { Name: "rt.release" };

    static string FromContext(WasmFunction f, List<Instr> b, int i)
    {
        if (i == 0) return "other";
        var p = b[i - 1];
        if (p.Op == WOp.I32Load)
        {
            int k = i - 2;
            while (k >= 0 && b[k].Op == WOp.Call && b[k].Ref is WasmFunction { Name: "rt.nonnull" }) k--;
            if (k >= 0 && b[k].Op == WOp.I32Const && b[k].Ref is IAddress) return "static";
            if (k >= 0 && b[k].Op == WOp.Call && b[k].Ref is WasmFunction ea && ea.Name.StartsWith("rt.elem_addr", StringComparison.Ordinal)) return "element";
            if (k >= 0 && b[k].Op is WOp.I32Add or WOp.I32Shl or WOp.I32Mul) return "element";
            return "field";
        }
        if (p.Op is WOp.LocalGet or WOp.LocalTee) return p.Imm < f.Type.Params.Length ? "parameter" : "local";
        if (p.Op == WOp.Call && p.Ref is WasmFunction g)
            return g.Name.StartsWith("rt.cast", StringComparison.Ordinal) || g.Name.StartsWith("rt.isinst", StringComparison.Ordinal) ? "cast" : "return";
        return "other";
    }

    static string Context(WasmFunction f, List<Instr> b, int i)
    {
        var parts = new List<string>();
        for (int k = Math.Max(0, i - 3); k <= Math.Min(b.Count - 1, i + 1); k++)
        {
            var x = b[k];
            string s = x.Op.ToString();
            if (x.Ref is WasmFunction g) s += " " + (g.Name.Length > 40 ? g.Name[..40] : g.Name);
            else if (x.Op is WOp.LocalGet or WOp.LocalSet or WOp.LocalTee) s += " " + (x.Imm < f.Type.Params.Length ? "p" : "l") + x.Imm;
            else if (x.Op is WOp.I32Load or WOp.I32Store) s += "+" + x.Imm;
            else if (x.Ref is IAddress) s += " addr";
            parts.Add(k == i ? "[" + s + "]" : s);
        }
        return string.Join(" ; ", parts);
    }

    /// <summary>IL2WASM_IL_COUNT_OBJECTS too: each allocation's type counted up at Rt.AllocObject's entry (its type id
    /// argument), each free's counted down at Rt.FreeObject's (the type id in the header, which a free leaves until
    /// it reuses the block); live per type at exit is the difference.</summary>
    void CountObjectsPerType(List<WasmFunction> fns, Dictionary<WasmFunction, MethodDesc> methods)
    {
        foreach (var f in fns)
        {
            if (!methods.TryGetValue(f, out var m) || m.OwningType is not MetadataType rt || rt.GetName() != "Rt" || rt.GetNamespace() != "Il2Wasm.Runtime") continue;
            bool alloc = m.GetName() == "AllocObject", free = m.GetName() is "FreeObject" or "FreeFixed";
            if (!alloc && !free) continue;
            var tmp = new Emitter(new WasmFunction("tmp", f.Type));
            int t = f.Type.Params.Length + f.Locals.Count;
            f.Locals.Add(ValType.I32);
            if (alloc) tmp.Get(1); else { tmp.Get(0); tmp.Load(WOp.I32Load, 4); }
            tmp.Set(t);
            // (an id past the table counts in its last slot)
            tmp.Get(t); tmp.I32(_liveSlots - 1); tmp.Get(t); tmp.I32(_liveSlots); tmp.Op(WOp.I32LtU); tmp.Op(WOp.Select);
            tmp.I32(3); tmp.Op(WOp.I32Shl); tmp.I32Addr(_live!); tmp.Op(WOp.I32Add);
            tmp.I64(alloc ? 1 : -1); tmp.Emit(WOp.I64AtomicRmwAdd, 0, 3); tmp.Op(WOp.Drop);
            f.Body.InsertRange(0, tmp.Fn.Body);
        }
    }
}

public sealed partial class IlCompiler
{
    /// <summary>RcStats: the methods' functions (to tell program, library and runtime code apart).</summary>
    internal IEnumerable<(MethodDesc, WasmFunction)> RcFunctionMap() => _functions.Select(p => (p.Key, p.Value));

    /// <summary>RcStats: how many types have ids (the per-type table's size, before the ids are final).</summary>
    internal int RcTypeCount() => _types.Count + 2;

    /// <summary>RcStats: each type's final id (the one in object headers) and name.</summary>
    internal IEnumerable<(int, string)> RcTypeNames()
    {
        if (_finalIds.Length != _types.Count) yield break;
        for (int i = 0; i < _types.Count; i++) yield return (_finalIds[i], _types[i].ToString() ?? "?");
    }
}
