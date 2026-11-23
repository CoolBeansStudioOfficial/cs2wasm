#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Types;
using Il2Wasm.Wasm;
using Internal.IL;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

/// <summary>An address fixed when the module is laid out.</summary>
sealed class LateAddr(Func<int> f) : IAddress { public int Address => f(); }

/// <summary>The IL front end's whole-program driver: from an entry method, every method reached is imported, counted
/// (naively) and lowered to the back end's WasmModule; then the whole-program parts are made (type ids, the dispatch
/// stubs over the constructed types, type tests, the destroy function, statics and literals, init and main), and the
/// module is encoded by the existing back end.</summary>
public sealed class IlCompiler
{
    readonly IlContext _ctx;
    internal readonly WasmModule Module = new();
    internal readonly RtFns Rt;
    internal readonly WasmTag ExceptionTag = new("cs_exception", new FuncType([ValType.I32], []));
    internal readonly WasmGlobal StackPointer = new("__stack_pointer", ValType.I32, true, 0) { Export = "__stack_pointer" };
    /// <summary>The shadow stack's size (the operation-tree front end's default).</summary>
    public int StackSize = 1 << 20;
    readonly Dictionary<MethodDesc, WasmFunction> _functions = new();
    readonly Queue<(MethodDesc, WasmFunction)> _queue = new();
    readonly Dictionary<TypeDesc, int> _typeIds = new();
    readonly List<TypeDesc> _types = [];
    readonly HashSet<TypeDesc> _constructed = [];
    readonly List<TypeDesc> _constructedOrder = [];
    readonly Dictionary<MethodDesc, WasmFunction> _stubs = new();
    readonly HashSet<(TypeDesc, MethodDesc)> _resolved = [];
    readonly Dictionary<(TypeDesc, MethodDesc), MethodDesc?> _impl = new();
    readonly Dictionary<TypeDesc, WasmFunction> _isinst = new(), _cast = new(), _unbox = new(), _init = new();
    readonly Dictionary<string, (IAddress addr, int index)> _literals = new();
    readonly List<string> _literalOrder = [];
    readonly Dictionary<(TypeDesc, bool), int> _staticBlockAt = new();
    readonly List<(TypeDesc t, bool gc, int size, int align)> _staticBlocks = [];
    readonly Dictionary<TypeDesc, IAddress> _initFlags = new();
    int _dataEnd, _staticsBase, _staticsEnd, _heapBase;
    readonly int[] _literalAt;
    IAddress? _nameTable;
    int[] _finalIds = [];
    public readonly List<string> Warnings = [];
    /// <summary>A static method of no arguments returning an int, exported as il2wasm_live (the tests' leak check: the
    /// blocks the allocator holds).</summary>
    public MethodDesc? LiveCounter;
    /// <summary>IL2WASM_IL_DUMP=name: the IR of the methods whose name contains it, on stderr.</summary>
    static readonly string? s_dump = Environment.GetEnvironmentVariable("IL2WASM_IL_DUMP") is { Length: > 0 } d ? d : null;

    public IlCompiler(IlContext ctx)
    {
        _ctx = ctx;
        Module.Tags.Add(ExceptionTag);
        Module.Globals.Add(StackPointer);
        Rt = new RtFns(this);
        _literalAt = [];
        // (string's type id is fixed at 1: the CoreLib's string allocation writes it)
        Constructed(_ctx.GetWellKnownType(WellKnownType.String));
    }

    internal TypeSystemContext Context => _ctx;

    internal MethodDesc CoreMethod(string ns, string type, string name)
    {
        var t = _ctx.SystemModule.GetType(Encoding.UTF8.GetBytes(ns), Encoding.UTF8.GetBytes(type), throwIfNotFound: true);
        return t.GetMethods().FirstOrDefault(m => m.GetName() == name) ?? throw new InvalidOperationException($"CoreLib has no {ns}.{type}.{name}");
    }

    MethodDesc? TryCoreMethod(string ns, string type, string name)
    {
        var t = _ctx.SystemModule.GetType(Encoding.UTF8.GetBytes(ns), Encoding.UTF8.GetBytes(type), throwIfNotFound: false);
        return t?.GetMethods().FirstOrDefault(m => m.GetName() == name);
    }

    // ------------------------------------------------------------------ the whole program

    public byte[] Compile(MethodDesc entry)
    {
        var main = FunctionFor(entry);
        for (; ; )
        {
            while (_queue.Count > 0) { var (m, f) = _queue.Dequeue(); CompileMethod(m, f); }
            if (!ResolveDispatch()) break;
        }
        // the runtime's whole-program functions
        Finish(main, entry);
        // the generated parts may reach more code (a destroy reaches FreeObject): compile it too
        for (; ; )
        {
            while (_queue.Count > 0) { var (m, f) = _queue.Dequeue(); CompileMethod(m, f); }
            if (!ResolveDispatch()) break;
        }
        GenerateLate();
        Layout();
        Module.TypeIdMap = id => _finalIds[id];
        Module.ValidateAlways = true;
        // (IL2WASM_IL_BODY=name: the WASM bodies of the functions whose name contains it, before the back end's passes)
        if (Environment.GetEnvironmentVariable("IL2WASM_IL_BODY") is { Length: > 0 } wb)
            foreach (var f in Module.Functions.Where(f => f.Name.Contains(wb)))
            {
                Console.Error.WriteLine($"== {f.Name} ({string.Join(" ", f.Locals)})");
                int depth = 0;
                for (int i = 0; i < f.Body.Count; i++)
                {
                    var ins = f.Body[i];
                    if (ins.Op is WOp.End or WOp.Else) depth--;
                    Console.Error.WriteLine($"{i,4}: {new string(' ', 2 * Math.Max(0, depth))}{ins.Op} {ins.Imm} {(ins.Ref is int[] t ? string.Join(",", t) : ins.Ref)}");
                    if (ins.Op is WOp.Block or WOp.Loop or WOp.If or WOp.TryTable or WOp.Else) depth++;
                }
            }
        WasmModule.Encoded enc;
        try { enc = Module.Encode(optimize: Environment.GetEnvironmentVariable("IL2WASM_IL_NO_OPT") != "1"); }
        catch when (Environment.GetEnvironmentVariable("IL2WASM_IL_BODY") is { Length: > 0 } wb2)
        {
            foreach (var f in Module.Functions.Where(f => f.Name.Contains(wb2))) { Console.Error.WriteLine("== after: " + f.Name); for (int i = 0; i < f.Body.Count; i++) Console.Error.WriteLine($"{i,4}: {f.Body[i].Op} {f.Body[i].Imm} {f.Body[i].Ref}"); }
            throw;
        }
        if (Module.ValidationProblems.Count > 0) throw new InvalidOperationException("invalid module:\n" + string.Join("\n", Module.ValidationProblems.Take(10)));
        return enc.Bytes;
    }

    static FuncType Sig(MethodDesc m)
    {
        var ps = new List<ValType>();
        if (!m.Signature.IsStatic) ps.Add(ValType.I32);
        for (int i = 0; i < m.Signature.Length; i++) ps.Add(FunctionLowering.ValOf(Kinds.Of(m.Signature[i])));
        var r = m.Signature.ReturnType;
        // (a struct result: the caller's buffer, the last parameter)
        if (!r.IsVoid && Kinds.Of(r) == VK.Struct) { ps.Add(ValType.I32); return new FuncType(ps.ToArray(), []); }
        return new FuncType(ps.ToArray(), r.IsVoid ? [] : [FunctionLowering.ValOf(Kinds.Of(r))]);
    }

    internal WasmFunction FunctionFor(MethodDesc m)
    {
        if (_functions.TryGetValue(m, out var f)) return f;
        if (m.GetTypicalMethodDefinition() is EcmaMethod em && em.GetDecodedCustomAttribute("Il2Wasm.Runtime", "ImportAttribute") is { } imp)
        {
            f = Module.AddImport((string)imp.FixedArguments[0].Value!, (string)imp.FixedArguments[1].Value!, m.ToString(), Sig(m));
            _functions[m] = f;
            return f;
        }
        f = Module.AddFunction(m.ToString(), Sig(m));
        _functions[m] = f;
        _queue.Enqueue((m, f));
        return f;
    }

    void CompileMethod(MethodDesc m, WasmFunction f)
    {
        var typical = m.GetTypicalMethodDefinition() as EcmaMethod;
        MethodIL? il = typical != null ? EcmaMethodIL.Create(typical) : null;
        if (il != null && m != typical) il = new InstantiatedMethodIL(m, il);
        if (il == null)
        {
            Warnings.Add($"{m} has no IL body: it traps");
            new Emitter(f).Op(WOp.Unreachable);
            return;
        }
        if (typical!.IsAggressiveInlining) f.InlineHint = true;
        if (typical.IsNoInlining) f.NoInline = true;
        try
        {
            var body = Importer.Import(m, il);
            var own = Ownership.Run(body);
            if (s_dump != null && m.ToString().Contains(s_dump)) Console.Error.WriteLine($"== {m}\n{body.Dump()}");
            new FunctionLowering(this, body, own, f).Run();
        }
        catch (Exception e) when (e is NotSupportedException or InvalidProgramException)
        {
            throw new InvalidOperationException($"{m}: {e.Message}", e);
        }
    }

    // ------------------------------------------------------------------ types

    internal int TypeId(TypeDesc t)
    {
        if (_typeIds.TryGetValue(t, out int id)) return id;
        id = _types.Count;
        _typeIds[t] = id;
        _types.Add(t);
        return id;
    }

    /// <summary>A type whose objects the program makes (dispatch and destroy cover these).</summary>
    internal void Constructed(TypeDesc t)
    {
        TypeId(t);
        if (_constructed.Add(t)) _constructedOrder.Add(t);
    }

    // ------------------------------------------------------------------ dispatch

    internal WasmFunction DispatchFor(MethodDesc slot)
    {
        if (_stubs.TryGetValue(slot, out var f)) return f;
        if (slot.HasInstantiation) throw new NotSupportedException($"the generic virtual method {slot}");
        f = Module.AddFunction("dispatch " + slot, Sig(slot));
        _stubs[slot] = f;
        return f;
    }

    MethodDesc? Implementation(TypeDesc x, MethodDesc slot)
    {
        if (_impl.TryGetValue((x, slot), out var r)) return r;
        MethodDesc? impl = null;
        if (x.CanCastTo(slot.OwningType))
        {
            if (slot.OwningType.IsInterface)
            {
                // (the type or the nearest base that implements the interface; then the override on the type)
                for (var c = x; c != null && impl == null; c = c.BaseType) impl = c.ResolveInterfaceMethodToVirtualMethodOnType(slot) ?? c.ResolveVariantInterfaceMethodToVirtualMethodOnType(slot);
                if (impl == null)
                {
                    var res = _ctx.GetVirtualMethodAlgorithmForType(x).ResolveInterfaceMethodToDefaultImplementationOnType(slot, x, out var dim);
                    if (res == DefaultInterfaceMethodResolution.DefaultImplementation) impl = dim;
                }
                else if (impl.IsVirtual && !x.IsArray) impl = x.FindVirtualFunctionTargetMethodOnObjectType(impl) ?? impl;
            }
            else if (x.IsArray) impl = slot.OwningType.IsObject ? slot : null;
            else impl = x.FindVirtualFunctionTargetMethodOnObjectType(slot);
            if (impl != null && impl.IsAbstract) impl = null;
        }
        _impl[(x, slot)] = impl;
        if (Environment.GetEnvironmentVariable("IL2WASM_IL_DISPATCH") == "1") Console.Error.WriteLine($"dispatch {slot} on {x}: {impl?.ToString() ?? "none"}");
        return impl;
    }

    /// <summary>Every (constructed type, called slot) pair gets its implementation compiled; true when anything new was
    /// queued.</summary>
    bool ResolveDispatch()
    {
        bool any = false;
        foreach (var slot in _stubs.Keys.ToList())
            foreach (var x in _constructedOrder.ToList())
            {
                if (!_resolved.Add((x, slot))) continue;
                if (Implementation(x, slot) is { } impl && !_functions.ContainsKey(impl)) { FunctionFor(impl); any = true; }
            }
        return any || _queue.Count > 0;
    }

    // ------------------------------------------------------------------ type tests

    internal WasmFunction IsInstFn(TypeDesc t)
    {
        if (_isinst.TryGetValue(t, out var f)) return f;
        f = Module.AddFunction("rt.isinst " + t, new FuncType([ValType.I32], [ValType.I32]));
        _isinst[t] = f;
        return f;
    }

    internal WasmFunction CastFn(TypeDesc t)
    {
        if (_cast.TryGetValue(t, out var f)) return f;
        f = Module.AddFunction("rt.cast " + t, new FuncType([ValType.I32], [ValType.I32]));
        _cast[t] = f;
        var e = new Emitter(f);
        e.Get(0); e.Op(WOp.I32Eqz); e.If(); e.I32(0); e.Op(WOp.Return); e.End();
        e.Get(0); e.Call(IsInstFn(t)); e.Op(WOp.I32Eqz);
        e.If(); e.Get(0); e.I32Addr(StringLiteral(TypeName(t))); e.Call(FunctionFor(CoreMethod("Il2Wasm.Runtime", "ThrowHelpers", "InvalidCast"))); e.Op(WOp.Unreachable); e.End();
        e.Get(0);
        return f;
    }

    internal WasmFunction UnboxFn(TypeDesc t)
    {
        if (_unbox.TryGetValue(t, out var f)) return f;
        f = Module.AddFunction("rt.unbox " + t, new FuncType([ValType.I32], [ValType.I32]));
        _unbox[t] = f;
        var e = new Emitter(f);
        e.Get(0); e.Call(Rt.NonNull); e.Call(IsInstFn(t)); e.Op(WOp.I32Eqz);
        e.If(); e.Get(0); e.I32Addr(StringLiteral(TypeName(t))); e.Call(FunctionFor(CoreMethod("Il2Wasm.Runtime", "ThrowHelpers", "InvalidCast"))); e.Op(WOp.Unreachable); e.End();
        e.Get(0);
        return f;
    }

    // ------------------------------------------------------------------ statics and initializers

    IAddress StaticBlock(TypeDesc owner, bool gc)
    {
        if (!_staticBlockAt.TryGetValue((owner, gc), out int i))
        {
            var dt = (DefType)owner;
            int size = gc ? dt.GCStaticFieldSize.AsInt : dt.NonGCStaticFieldSize.AsInt;
            int align = gc ? 4 : Math.Max(4, dt.NonGCStaticFieldAlignment.AsInt);
            i = _staticBlocks.Count;
            _staticBlockAt[(owner, gc)] = i;
            _staticBlocks.Add((owner, gc, size, align));
        }
        int idx = i;
        return new LateAddr(() => _blockOffsets[idx]);
    }

    int[] _blockOffsets = [];

    internal IAddress StaticAddress(FieldDesc f)
    {
        if (f.IsLiteral) throw new NotSupportedException($"a load of the literal field {f}");
        var block = StaticBlock(f.OwningType, f.HasGCStaticBase);
        int off = f.Offset.AsInt;
        return new LateAddr(() => block.Address + off);
    }

    IAddress InitFlag(TypeDesc t)
    {
        if (_initFlags.TryGetValue(t, out var a)) return a;
        int i = _staticBlocks.Count;
        _staticBlocks.Add((t, false, 4, 4));
        a = new LateAddr(() => _blockOffsets[i]);
        _initFlags[t] = a;
        return a;
    }

    /// <summary>The type's static constructor runs before this access, once (single-threaded for now: SPEC 3's
    /// thread-safe initialization needs the runtime's per-type lock, not yet here).</summary>
    internal void CctorCheck(Emitter e, TypeDesc t)
    {
        if (t.GetStaticConstructor() is not { } cctor) return;
        if (!_init.TryGetValue(t, out var f))
        {
            f = Module.AddFunction("init " + t, new FuncType([], []));
            _init[t] = f;
            var flag = InitFlag(t);
            var b = new Emitter(f);
            b.I32Addr(flag); b.Load(WOp.I32Load); b.If(); b.Op(WOp.Return); b.End();
            b.I32Addr(flag); b.I32(1); b.Store(WOp.I32Store);
            b.Call(FunctionFor(cctor));
        }
        e.I32Addr(InitFlag(t)); e.Load(WOp.I32Load); e.Op(WOp.I32Eqz); e.If(); e.Call(f); e.End();
    }

    /// <summary>A static method or a constructor of a type without beforefieldinit triggers its initializer.</summary>
    internal void CctorCheckForCall(Emitter e, MethodDesc m)
    {
        if (m.IsStaticConstructor) return;
        if (m.OwningType is MetadataType { IsBeforeFieldInit: false } t) CctorCheck(e, t);
    }

    // ------------------------------------------------------------------ literals

    internal IAddress StringLiteral(string s)
    {
        if (_literals.TryGetValue(s, out var l)) return l.addr;
        int i = _literalOrder.Count;
        _literalOrder.Add(s);
        var a = new LateAddr(() => _literalOffsets[i]);
        _literals[s] = (a, i);
        return a;
    }

    int[] _literalOffsets = [];

    internal static string TypeName(TypeDesc t)
    {
        switch (t)
        {
            case ArrayType a: return TypeName(a.ElementType) + (a.IsSzArray ? "[]" : "[" + new string(',', a.Rank - 1) + "]");
            case PointerType p: return TypeName(p.ParameterType) + "*";
            case ByRefType r: return TypeName(r.ParameterType) + "&";
            case MetadataType m:
            {
                var def = (MetadataType)m.GetTypeDefinition();
                var name = def.ContainingType != null ? TypeName(def.ContainingType) + "+" + def.GetName() : (def.GetNamespace().Length > 0 ? def.GetNamespace() + "." : "") + def.GetName();
                if (m.HasInstantiation) name += "[" + string.Join(",", Enumerable.Range(0, m.Instantiation.Length).Select(i => TypeName(m.Instantiation[i]))) + "]";
                return name;
            }
            default: return t.ToString()!;
        }
    }

    // ------------------------------------------------------------------ intrinsics

    internal bool TryIntrinsic(Emitter e, MethodDesc m, Node n, FunctionLowering fl)
    {
        if (m.GetTypicalMethodDefinition() is not EcmaMethod em || !em.HasCustomAttribute("Il2Wasm.Runtime", "IntrinsicAttribute")) return false;
        var key = ((MetadataType)m.OwningType).GetName() + "." + m.GetName();
        fl.PushArgs(n);
        switch (key)
        {
            case "Mem.LoadI32": e.Load(WOp.I32Load); break;
            case "Mem.LoadU8": e.Load(WOp.I32Load8U); break;
            case "Mem.LoadI8": e.Load(WOp.I32Load8S); break;
            case "Mem.LoadU16": e.Load(WOp.I32Load16U); break;
            case "Mem.LoadI16": e.Load(WOp.I32Load16S); break;
            case "Mem.LoadI64": e.Load(WOp.I64Load); break;
            case "Mem.LoadF32": e.Load(WOp.F32Load); break;
            case "Mem.LoadF64": e.Load(WOp.F64Load); break;
            case "Mem.StoreI32": e.Store(WOp.I32Store); break;
            case "Mem.StoreU8": e.Store(WOp.I32Store8); break;
            case "Mem.StoreU16": e.Store(WOp.I32Store16); break;
            case "Mem.StoreI64": e.Store(WOp.I64Store); break;
            case "Mem.StoreF32": e.Store(WOp.F32Store); break;
            case "Mem.StoreF64": e.Store(WOp.F64Store); break;
            case "Mem.Copy": e.Emit(WOp.MemoryCopy); break;
            case "Mem.Fill": e.Emit(WOp.MemoryFill); break;
            case "Mem.Size": e.Emit(WOp.MemorySize); break;
            case "Mem.Grow": e.Emit(WOp.MemoryGrow); break;
            case "Rt.AddressOf": break;
            case "Rt.Take": if (m.Instantiation[0] is { IsValueType: false, IsInterface: false, IsObject: false } tk && tk is not MetadataType { IsAbstract: true }) Constructed(tk); break;
            case "Rt.Borrow": e.Call(Rt.Retain); break;
            case "Rt.TypeId": e.Call(Rt.NonNull); e.Load(WOp.I32Load, 4); break;
            case "Rt.TypeIdOf": e.Tid(TypeId(m.Instantiation[0])); break;
            case "Rt.HeapBase": e.I32Addr(new LateAddr(() => _heapBase)); break;
            case "Rt.Unreachable": e.Op(WOp.Unreachable); break;
            case "Rt.TypeNameById":
                _nameTable ??= StaticNameTable();
                e.I32(2); e.Op(WOp.I32Shl); e.I32Addr(_nameTable); e.Op(WOp.I32Add); e.Load(WOp.I32Load);
                break;
            default: throw new NotSupportedException($"the intrinsic {key}");
        }
        fl.SetResult(n);
        return true;
    }

    IAddress StaticNameTable() => new LateAddr(() => _nameTableAt);
    int _nameTableAt;

    // ------------------------------------------------------------------ the generated whole-program parts

    WasmFunction? _mainExport;

    void Finish(WasmFunction main, MethodDesc entry)
    {
        // main: the entry, with an exception that escapes it reported to the host
        var mf = Module.AddFunction("main", new FuncType([], []));
        mf.Exports.Add("main");
        _mainExport = mf;
        var e = new Emitter(mf);
        var caught = e.Block(ValType.I32);
        e.TryTable(null, (0, ExceptionTag, caught));
        if (entry.Signature.Length == 1) e.I32(0);
        e.Call(main);
        if (!entry.Signature.ReturnType.IsVoid) e.Op(WOp.Drop);
        e.Op(WOp.Return);
        e.End();
        e.Op(WOp.Unreachable);
        e.End();
        // (the exception is released once reported: the program has ended)
        int ex = e.AddLocal(ValType.I32);
        e.Set(ex);
        if (TryCoreMethod("Il2Wasm.Runtime", "Rt", "ReportUnhandled") is { } report) { e.Get(ex); e.Call(FunctionFor(report)); }
        e.Get(ex); e.Call(Rt.Release);

        if (LiveCounter != null)
        {
            var lf = Module.AddFunction("il2wasm_live", new FuncType([], [ValType.I32]));
            lf.Exports.Add("il2wasm_live");
            new Emitter(lf).Call(FunctionFor(LiveCounter));
        }
        // the destroy function's callee
        Rt.Free = FunctionFor(CoreMethod("Il2Wasm.Runtime", "Rt", "FreeObject"));
        _ = Rt.NonNull;
        // the names, for TypeNameById (made now: they are literals)
        foreach (var t in _types.ToList()) StringLiteral(TypeName(t));
    }

    /// <summary>The functions whose bodies need the whole program: dispatch stubs, type tests, destroy.</summary>
    void GenerateLate()
    {
        // type ids: 1 is string's (CoreLib's contract); the others in a preorder of the class hierarchy from 2
        foreach (var t in _types.ToList()) StringLiteral(TypeName(t));
        var order = new List<TypeDesc>();
        var children = new Dictionary<TypeDesc, List<TypeDesc>>();
        var roots = new List<TypeDesc>();
        foreach (var t in _types)
        {
            if (t.IsString) continue;
            var b = t.BaseType;
            if (b != null && _typeIds.ContainsKey(b) && !b.IsString) { if (!children.TryGetValue(b, out var l)) children[b] = l = []; l.Add(t); }
            else roots.Add(t);
        }
        void Pre(TypeDesc t) { order.Add(t); if (children.TryGetValue(t, out var l)) foreach (var c in l) Pre(c); }
        foreach (var r in roots) Pre(r);
        _finalIds = new int[_types.Count];
        _finalIds[_typeIds[_ctx.GetWellKnownType(WellKnownType.String)]] = 1;
        for (int i = 0; i < order.Count; i++) _finalIds[_typeIds[order[i]]] = i + 2;
        int Final(TypeDesc t) => _finalIds[_typeIds[t]];

        // dispatch stubs: a switch on the receiver's type id over the constructed types
        foreach (var (slot, f) in _stubs)
        {
            var e = new Emitter(f);
            int tid = e.AddLocal(ValType.I32);
            e.Get(0); e.Call(Rt.NonNull); e.Load(WOp.I32Load, 4); e.Set(tid);
            foreach (var g in _constructedOrder.Select(x => (x, impl: Implementation(x, slot))).Where(p => p.impl != null).GroupBy(p => p.impl!))
            {
                EmitIdTest(e, tid, g.Select(p => Final(p.x)));
                e.If();
                // (a struct's method on its box: this is the value, after the header)
                for (int i = 0; i < f.Type.Params.Length; i++) { e.Get(i); if (i == 0 && g.Key.OwningType.IsValueType) { e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); } }
                e.Call(FunctionFor(g.Key));
                e.Op(WOp.Return);
                e.End();
            }
            e.Op(WOp.Unreachable);
        }
        // type tests: the constructed types that cast to the target
        foreach (var (t, f) in _isinst)
        {
            var e = new Emitter(f);
            var ids = _constructedOrder.Where(x => x.CanCastTo(t)).Select(Final).ToList();
            e.Get(0); e.Op(WOp.I32Eqz); e.If(); e.I32(0); e.Op(WOp.Return); e.End();
            if (ids.Count == 0) { e.I32(0); continue; }
            int tid = e.AddLocal(ValType.I32);
            e.Get(0); e.Load(WOp.I32Load, 4); e.Set(tid);
            EmitIdTest(e, tid, ids);
            e.If(ValType.I32); e.Get(0); e.Else(); e.I32(0); e.End();
        }
        // destroy: each constructed type's references released, then its memory freed
        {
            var e = new Emitter(Rt.Destroy);
            int tid = e.AddLocal(ValType.I32), i = e.AddLocal(ValType.I32), n = e.AddLocal(ValType.I32);
            e.Get(0); e.Load(WOp.I32Load, 4); e.Set(tid);
            foreach (var x in _constructedOrder)
            {
                e.Get(tid); e.I32(Final(x)); e.Op(WOp.I32Eq); e.If();
                if (x is ArrayType at)
                {
                    int es = Kinds.SizeOf(at.ElementType);
                    if (Kinds.Of(at.ElementType) == VK.Ref)
                    {
                        // the elements, first to last
                        e.Get(0); e.Load(WOp.I32Load, 12); e.Set(n); e.I32(0); e.Set(i);
                        var brk = e.Block(); var loop = e.Loop();
                        e.Get(i); e.Get(n); e.Op(WOp.I32GeU); e.BrIf(brk);
                        e.Get(0); e.Get(i); e.I32(2); e.Op(WOp.I32Shl); e.Op(WOp.I32Add); e.Load(WOp.I32Load, 16); e.Call(Rt.Release);
                        e.Get(i); e.I32(1); e.Op(WOp.I32Add); e.Set(i); e.Br(loop);
                        e.End(); e.End();
                    }
                    e.Get(0); e.Get(0); e.Load(WOp.I32Load, 12); e.I32(es); e.Op(WOp.I32Mul); e.I32(16); e.Op(WOp.I32Add);
                }
                else if (x.IsString)
                {
                    e.Get(0); e.Get(0); e.Load(WOp.I32Load, 12); e.I32(1); e.Op(WOp.I32Shl); e.I32(18); e.Op(WOp.I32Add);
                }
                else
                {
                    for (TypeDesc? c = x; c != null; c = c.BaseType)
                        foreach (var fd in c.GetFields())
                            if (!fd.IsStatic && Kinds.Of(fd.FieldType) == VK.Ref) { e.Get(0); e.Load(WOp.I32Load, IlContext.FieldOffset(fd)); e.Call(Rt.Release); }
                    if (Kinds.Of(x) == VK.Struct && Kinds.Counted(x)) { e.Get(0); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); e.Call(Rt.ReleaseStruct(x)); }
                    e.Get(0); e.I32(x.IsValueType ? IlContext.ObjectHeader + Math.Max(4, Kinds.SizeOf(x)) : IlContext.InstanceSize((DefType)x));
                }
                e.Call(Rt.Free); e.Op(WOp.Return);
                e.End();
            }
        }
        // the type name table, in final id order
        _typeNamesByFinal = new string[_types.Count + 2];
        foreach (var t in _types) _typeNamesByFinal[Final(t)] = TypeName(t);
    }

    string?[] _typeNamesByFinal = [];

    /// <summary>tid is one of the ids: ranges of consecutive ids, each one unsigned comparison.</summary>
    static void EmitIdTest(Emitter e, int tid, IEnumerable<int> idsIn)
    {
        var ids = idsIn.Distinct().OrderBy(x => x).ToList();
        bool first = true;
        for (int i = 0; i < ids.Count;)
        {
            int j = i;
            while (j + 1 < ids.Count && ids[j + 1] == ids[j] + 1) j++;
            if (ids[i] == ids[j]) { e.Get(tid); e.I32(ids[i]); e.Op(WOp.I32Eq); }
            else { e.Get(tid); e.I32(ids[i]); e.Op(WOp.I32Sub); e.I32(ids[j] - ids[i]); e.Op(WOp.I32LeU); }
            if (!first) e.Op(WOp.I32Or);
            first = false;
            i = j + 1;
        }
        if (first) e.I32(0);
    }

    /// <summary>The memory: data (literals, the name table) from 16, then the statics, a KiB for the allocator's
    /// free lists, then the heap.</summary>
    void Layout()
    {
        var data = new List<byte>();
        const int dataBase = 16;
        int stringId = 1;
        _literalOffsets = new int[_literalOrder.Count];
        void Align(int a) { while ((dataBase + data.Count) % a != 0) data.Add(0); }
        void I32(int v) => data.AddRange(BitConverter.GetBytes(v));
        for (int i = 0; i < _literalOrder.Count; i++)
        {
            Align(8);
            _literalOffsets[i] = dataBase + data.Count;
            var s = _literalOrder[i];
            I32(-1); I32(stringId); I32(0); I32(s.Length);
            foreach (var ch in s) data.AddRange(BitConverter.GetBytes((ushort)ch));
            data.Add(0); data.Add(0);
        }
        Align(4);
        _nameTableAt = dataBase + data.Count;
        foreach (var name in _typeNamesByFinal) I32(name != null ? _literalOffsets[_literals[name].index] : 0);
        _dataEnd = dataBase + data.Count;
        int at = (_dataEnd + 15) & ~15;
        _staticsBase = at;
        _blockOffsets = new int[_staticBlocks.Count];
        for (int i = 0; i < _staticBlocks.Count; i++)
        {
            var (_, _, size, align) = _staticBlocks[i];
            at = (at + align - 1) / align * align;
            _blockOffsets[i] = at;
            at += Math.Max(size, 0);
        }
        _staticsEnd = at;
        int stackTop = ((at + 15) & ~15) + StackSize;
        _heapBase = stackTop + 1024;
        var seg = new DataSegment();
        seg.Bytes.AddRange(data);
        Module.Data.Add(seg);
        // init: the data in place
        var init = Module.AddFunction("init", new FuncType([], []));
        init.Exports.Add("init");
        var e = new Emitter(init);
        e.I32(stackTop); e.GSet(StackPointer);
        if (data.Count > 0)
        {
            e.I32(dataBase); e.I32(0); e.I32(data.Count); e.Emit(WOp.MemoryInit, 0, 0, seg);
            e.Emit(WOp.DataDrop, 0, 0, seg);
        }
        if ((long)_heapBase + (1 << 20) > (long)Module.MemoryMinPages * 65536) Module.MemoryMinPages = (_heapBase + (1 << 20) + 65535) / 65536;
    }

    internal sealed class RtFns(IlCompiler c)
    {
        readonly WasmModule M = c.Module;
        readonly Dictionary<string, WasmFunction> _cache = new();
        WasmFunction? _retain, _release, _destroy, _nonNull;
        public WasmFunction Free = null!;

        static FuncType FT(ValType[] p, ValType[] r) => new(p, r);
        static readonly ValType I = ValType.I32, L = ValType.I64;

        public WasmFunction Destroy => _destroy ??= M.AddFunction("rt.destroy", FT([I], []));

        /// <summary>retain(p) -> p: count + 1 unless null or immortal (a count of 0 or below).</summary>
        public WasmFunction Retain
        {
            get
            {
                if (_retain != null) return _retain;
                _retain = M.AddFunction("rt.retain", FT([I], [I]));
                var e = new Emitter(_retain);
                int r = e.AddLocal(I);
                e.Get(0); e.If();
                e.Get(0); e.Load(WOp.I32Load); e.Tee(r); e.I32(0); e.Op(WOp.I32GtS);
                e.If(); e.Get(0); e.Get(r); e.I32(1); e.Op(WOp.I32Add); e.Store(WOp.I32Store); e.End();
                e.End();
                e.Get(0);
                return _retain;
            }
        }

        /// <summary>release(p): count - 1; at the last, the count is cleared and the object destroyed.</summary>
        public WasmFunction Release
        {
            get
            {
                if (_release != null) return _release;
                _release = M.AddFunction("rt.release", FT([I], []));
                var e = new Emitter(_release);
                int r = e.AddLocal(I);
                e.Get(0); e.If();
                e.Get(0); e.Load(WOp.I32Load); e.Tee(r); e.I32(1); e.Op(WOp.I32GtS);
                e.If(); e.Get(0); e.Get(r); e.I32(1); e.Op(WOp.I32Sub); e.Store(WOp.I32Store);
                e.Else();
                e.Get(r); e.I32(1); e.Op(WOp.I32Eq); e.If(); e.Get(0); e.I32(0); e.Store(WOp.I32Store); e.Get(0); e.Call(Destroy); e.End();
                e.End();
                e.End();
                return _release;
            }
        }

        WasmFunction Throw(string name) => c.FunctionFor(c.CoreMethod("Il2Wasm.Runtime", "ThrowHelpers", name));

        /// <summary>(p) -> p, throwing NullReferenceException when p is null (the back end's NullChecks knows this one).</summary>
        public WasmFunction NonNull
        {
            get
            {
                if (_nonNull != null) return _nonNull;
                _nonNull = M.AddFunction("rt.nonnull", FT([I], [I]));
                var e = new Emitter(_nonNull);
                e.Get(0); e.Op(WOp.I32Eqz); e.If(); e.Call(Throw("NullReference")); e.End(); e.Get(0);
                return _nonNull;
            }
        }

        public WasmFunction AllocObject => c.FunctionFor(c.CoreMethod("Il2Wasm.Runtime", "Rt", "AllocObject"));
        public WasmFunction AllocArray => c.FunctionFor(c.CoreMethod("Il2Wasm.Runtime", "Rt", "AllocArray"));

        WasmFunction Cached(string key, FuncType t, Action<Emitter> body)
        {
            if (_cache.TryGetValue(key, out var f)) return f;
            f = M.AddFunction(key, t);
            _cache[key] = f;
            body(new Emitter(f));
            return f;
        }

        /// <summary>(array, index) -> the element's address, after the null and bounds checks.</summary>
        public WasmFunction ElemAddr(int size) => Cached("rt.elem_addr " + size, FT([I, I], [I]), e =>
        {
            e.Get(0); e.Call(NonNull); e.Load(WOp.I32Load, 12); e.Get(1); e.Op(WOp.I32LeU);
            e.If(); e.Call(Throw("IndexOutOfRange")); e.End();
            e.Get(0); e.Get(1); e.I32(size); e.Op(WOp.I32Mul); e.Op(WOp.I32Add); e.I32(16); e.Op(WOp.I32Add);
        });

        /// <summary>Division and remainder (DivideByZeroException; OverflowException for MinValue / -1, as .NET
        /// throws it for both), and the overflow-checked add, subtract and multiply.</summary>
        public WasmFunction Checked(BinOp op, bool i64, bool ovf, bool un)
        {
            var t = i64 ? L : I;
            return Cached($"rt.{op}{(ovf ? ".ovf" : "")}{(un ? ".un" : "")} {(i64 ? "i64" : "i32")}", FT([t, t], [t]), e =>
            {
                void Const(long v) { if (i64) e.I64(v); else e.I32((int)v); }
                WOp O(WOp a32, WOp a64) => i64 ? a64 : a32;
                if (op is BinOp.Div or BinOp.DivUn or BinOp.Rem or BinOp.RemUn)
                {
                    e.Get(1); e.Op(O(WOp.I32Eqz, WOp.I64Eqz)); e.If(); e.Call(Throw("DivideByZero")); e.End();
                    if (op is BinOp.Div or BinOp.Rem)
                    {
                        e.Get(1); Const(-1); e.Op(O(WOp.I32Eq, WOp.I64Eq));
                        e.Get(0); Const(i64 ? long.MinValue : int.MinValue); e.Op(O(WOp.I32Eq, WOp.I64Eq));
                        e.Op(WOp.I32And); e.If(); e.Call(Throw("Overflow")); e.End();
                    }
                    e.Get(0); e.Get(1);
                    e.Op(op switch
                    {
                        BinOp.Div => O(WOp.I32DivS, WOp.I64DivS), BinOp.DivUn => O(WOp.I32DivU, WOp.I64DivU),
                        BinOp.Rem => O(WOp.I32RemS, WOp.I64RemS), _ => O(WOp.I32RemU, WOp.I64RemU),
                    });
                    return;
                }
                if (!i64)
                {
                    // in 64 bits, then the range test
                    int r = e.AddLocal(L);
                    e.Get(0); e.Op(un ? WOp.I64ExtendI32U : WOp.I64ExtendI32S);
                    e.Get(1); e.Op(un ? WOp.I64ExtendI32U : WOp.I64ExtendI32S);
                    e.Op(op switch { BinOp.Add => WOp.I64Add, BinOp.Sub => WOp.I64Sub, _ => WOp.I64Mul });
                    e.Set(r);
                    if (un) { e.Get(r); e.I64(0xFFFFFFFFL); e.Op(WOp.I64GtU); }
                    else { e.Get(r); e.I64(int.MinValue); e.Op(WOp.I64LtS); e.Get(r); e.I64(int.MaxValue); e.Op(WOp.I64GtS); e.Op(WOp.I32Or); }
                    e.If(); e.Call(Throw("Overflow")); e.End();
                    e.Get(r); e.Op(WOp.I32WrapI64);
                    return;
                }
                int res = e.AddLocal(L);
                e.Get(0); e.Get(1); e.Op(op switch { BinOp.Add => WOp.I64Add, BinOp.Sub => WOp.I64Sub, _ => WOp.I64Mul }); e.Set(res);
                switch (op)
                {
                    case BinOp.Add when un: e.Get(res); e.Get(0); e.Op(WOp.I64LtU); break;
                    case BinOp.Sub when un: e.Get(0); e.Get(1); e.Op(WOp.I64LtU); break;
                    case BinOp.Add: e.Get(0); e.Get(res); e.Op(WOp.I64Xor); e.Get(1); e.Get(res); e.Op(WOp.I64Xor); e.Op(WOp.I64And); e.I64(0); e.Op(WOp.I64LtS); break;
                    case BinOp.Sub: e.Get(0); e.Get(1); e.Op(WOp.I64Xor); e.Get(0); e.Get(res); e.Op(WOp.I64Xor); e.Op(WOp.I64And); e.I64(0); e.Op(WOp.I64LtS); break;
                    default:
                        // a * b overflows when a != 0 and res / a != b (and the one signed case the division cannot see)
                        e.Get(0); e.I64(0); e.Op(WOp.I64Ne);
                        e.If(ValType.I32);
                        e.Get(res); e.Get(0); e.Op(un ? WOp.I64DivU : WOp.I64DivS); e.Get(1); e.Op(WOp.I64Ne);
                        if (!un) { e.Get(0); e.I64(-1); e.Op(WOp.I64Eq); e.Get(1); e.I64(long.MinValue); e.Op(WOp.I64Eq); e.Op(WOp.I32And); e.Op(WOp.I32Or); }
                        e.Else(); e.I32(0); e.End();
                        break;
                }
                e.If(); e.Call(Throw("Overflow")); e.End();
                e.Get(res);
            });
        }

        /// <summary>Overflow-checked conversions: the value against the target's range, then converted.</summary>
        public WasmFunction ConvOvf(VK from, VK to, byte small, bool unsignedTarget, bool fromUn)
        {
            var ft = from switch { VK.I64 => L, VK.F32 => ValType.F32, VK.F64 => ValType.F64, _ => I };
            var tt = to == VK.I64 ? L : I;
            int bits = to == VK.I64 ? 64 : small * 8;
            return Cached($"rt.conv_ovf {from}{(fromUn ? ".un" : "")} {(unsignedTarget ? "u" : "i")}{bits}", FT([ft], [tt]), e =>
            {
                // the bounds, inclusive
                decimal lo = unsignedTarget ? 0 : -(decimal)Math.Pow(2, bits - 1);
                decimal hi = unsignedTarget ? (decimal)Math.Pow(2, bits) - 1 : (decimal)Math.Pow(2, bits - 1) - 1;
                if (from is VK.F32 or VK.F64)
                {
                    e.Get(0); if (from == VK.F32) e.Op(WOp.F64PromoteF32);
                    int d = e.AddLocal(ValType.F64); e.Set(d);
                    // (truncation first: -0.9 converts to 0)
                    e.Get(d); e.Op(WOp.F64Trunc); e.F64((double)lo); e.Op(WOp.F64Lt);
                    e.Get(d); e.Op(WOp.F64Trunc); e.F64((double)hi); e.Op(WOp.F64Gt); e.Op(WOp.I32Or);
                    e.Get(d); e.Get(d); e.Op(WOp.F64Ne); e.Op(WOp.I32Or);
                    e.If(); e.Call(Throw("Overflow")); e.End();
                    e.Get(d);
                    e.Op(to == VK.I64 ? (unsignedTarget ? WOp.I64TruncSatF64U : WOp.I64TruncSatF64S) : (unsignedTarget || bits == 32 && unsignedTarget ? WOp.I32TruncSatF64U : WOp.I32TruncSatF64S));
                    if (to != VK.I64 && bits == 32 && unsignedTarget) { }
                    return;
                }
                // integers: in 64 bits (an unsigned source zero-extended), compared signed unless the source is an
                // unsigned 64-bit value
                e.Get(0);
                if (from != VK.I64) e.Op(fromUn ? WOp.I64ExtendI32U : WOp.I64ExtendI32S);
                int v = e.AddLocal(L); e.Set(v);
                bool srcUnsigned64 = from == VK.I64 && fromUn;
                if (srcUnsigned64)
                {
                    e.Get(v); e.I64((long)Math.Min(hi, long.MaxValue)); e.Op(WOp.I64GtU);
                    if (hi > long.MaxValue) { e.Op(WOp.Drop); e.I32(0); }
                }
                else
                {
                    e.Get(v); e.I64((long)lo); e.Op(WOp.I64LtS);
                    if (hi <= long.MaxValue) { e.Get(v); e.I64((long)hi); e.Op(WOp.I64GtS); e.Op(WOp.I32Or); }
                }
                e.If(); e.Call(Throw("Overflow")); e.End();
                e.Get(v);
                if (to != VK.I64) e.Op(WOp.I32WrapI64);
            });
        }

        /// <summary>(address of a struct): each reference in it retained or released, nested structs too.</summary>
        public WasmFunction RetainStruct(TypeDesc t) => StructCounts(t, true);
        public WasmFunction ReleaseStruct(TypeDesc t) => StructCounts(t, false);

        WasmFunction StructCounts(TypeDesc t, bool retain) => Cached((retain ? "rt.retain_struct " : "rt.release_struct ") + t, FT([I], []), e =>
        {
            foreach (var f in t.GetFields())
            {
                if (f.IsStatic) continue;
                var ft = f.FieldType;
                var k = Kinds.Of(ft);
                if (k == VK.Ref) { e.Get(0); e.Load(WOp.I32Load, IlContext.FieldOffset(f)); if (retain) { e.Call(Retain); e.Op(WOp.Drop); } else e.Call(Release); }
                else if (k == VK.Struct && Kinds.Counted(ft)) { e.Get(0); e.I32(IlContext.FieldOffset(f)); e.Op(WOp.I32Add); e.Call(StructCounts(ft, retain)); }
            }
        });

        public WasmFunction FRem(bool d) => throw new NotSupportedException("the remainder of floating-point values");
    }
}
