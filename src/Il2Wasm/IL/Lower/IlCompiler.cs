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
public sealed partial class IlCompiler
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
    readonly Dictionary<(TypeDesc, int), int> _staticBlockAt = new();
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
    /// <summary>Count the objects the runtime allocates and frees (Rt.AllocObject, FreeObject, FreeFixed), exported as
    /// il2wasm_live: the tests' leak check on the IL CoreLib (IL2WASM_IL_COUNT_OBJECTS=1).</summary>
    public bool CountObjects = Environment.GetEnvironmentVariable("IL2WASM_IL_COUNT_OBJECTS") == "1";
    bool _countWrapping;
    IAddress? _counted;
    /// <summary>What is not compiled yet traps where it is reached (each one a warning), instead of failing the build:
    /// to measure how far a program gets (IL2WASM_IL_LENIENT=1).</summary>
    public bool Lenient = Environment.GetEnvironmentVariable("IL2WASM_IL_LENIENT") == "1";
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
        if (m is ArrayMethod am) return MdArrayMethod(am);
        // (a delegate type's Invoke has no IL: the generated invoke, also as a function value, a delegate of a
        // delegate's Invoke)
        if (IsDelegateInvoke(m) && m.GetTypicalMethodDefinition() is EcmaMethod { IsRuntimeImplemented: true }) return _functions[m] = DelegateInvoke(m);
        if (m.GetTypicalMethodDefinition() is EcmaMethod em && em.GetDecodedCustomAttribute("Il2Wasm.Runtime", "ImportAttribute") is { } imp)
        {
            f = Module.AddImport((string)imp.FixedArguments[0].Value!, (string)imp.FixedArguments[1].Value!, m.ToString(), Sig(m));
            _functions[m] = f;
            return f;
        }
        // (CountObjects: the runtime's allocation and free of objects counted, il2wasm_live reads the count)
        if (CountObjects && m.OwningType is MetadataType { Name: var _ } rtt && rtt.GetName() == "Rt" && rtt.GetNamespace() == "Il2Wasm.Runtime"
            && m.GetName() is "AllocObject" or "FreeObject" or "FreeFixed" && !_countWrapping)
        {
            _countWrapping = true;
            var inner = FunctionFor(m);
            _countWrapping = false;
            var w = Module.AddFunction("counted " + m, inner.Type);
            _functions[m] = w;
            _counted ??= NamedStatic("il2wasm.live", 4, 4);
            var e = new Emitter(w);
            // (IL2WASM_IL_WATCH=address: a free of that block traps, so its stack shows who)
            if (m.GetName() != "AllocObject") WatchEvent(e, 4);
            for (int i = 0; i < inner.Type.Params.Length; i++) e.Get(i);
            e.Call(inner);
            if (m.GetName() == "AllocObject") { int res = e.AddLocal(ValType.I32); e.Tee(res); e.Set(res); WatchEvent(e, 3, res); e.Get(res); }
            e.I32Addr(_counted); e.I32Addr(_counted); e.Load(WOp.I32Load); e.I32(m.GetName() == "AllocObject" ? 1 : -1); e.Op(WOp.I32Add); e.Store(WOp.I32Store);
            return w;
        }
        // a P/Invoke or an internal call: the runtime's method that serves it (or what .NET throws)
        if (m.GetTypicalMethodDefinition() is EcmaMethod nm && (nm.IsPInvoke || nm.IsInternalCall) && NativeFunction(m, nm) is { } native)
        {
            _functions[m] = native;
            return native;
        }
        f = Module.AddFunction(m.ToString(), Sig(m));
        _functions[m] = f;
        _queue.Enqueue((m, f));
        _why[m] = _compiling;
        return f;
    }

    readonly Dictionary<MethodDesc, MethodDesc?> _why = new();
    MethodDesc? _compiling;

    /// <summary>For an error's message: the chain of methods whose code first reached this one.</summary>
    string WhyReached(MethodDesc m)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<MethodDesc>();
        for (var x = _why.GetValueOrDefault(m); x != null && seen.Add(x) && seen.Count < 25; x = _why.GetValueOrDefault(x)) sb.Append("\n    ").Append(x);
        return sb.ToString();
    }

    void CompileMethod(MethodDesc m, WasmFunction f)
    {
        _compiling = m;
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
            if (!Lenient) throw new InvalidOperationException($"{m}: {e.Message}\n  reached from: {WhyReached(m)}", e);
            Warnings.Add("not compiled: " + e.Message);
            f.Body.Clear(); f.Locals.Clear(); new Emitter(f).Op(WOp.Unreachable);
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
        // (a generic virtual method: a stub per instantiation called, every program method instantiated; each case the
        // override of the method's definition on the receiver's type, instantiated as called: NativeAOT's resolution)
        f = Module.AddFunction("dispatch " + slot, Sig(slot));
        _stubs[slot] = f;
        return f;
    }

    MethodDesc? Implementation(TypeDesc x, MethodDesc slot)
    {
        if (_impl.TryGetValue((x, slot), out var r)) return r;
        if (slot.HasInstantiation && !slot.IsGenericMethodDefinition)
        {
            // a generic virtual method: the override of its definition (on the slot's instantiated type), then that
            // method instantiated with the call's method arguments
            var def = Implementation(x, slot.GetMethodDefinition());
            r = def == null ? null : _ctx.GetInstantiatedMethod(def.GetMethodDefinition(), slot.Instantiation);
            _impl[(x, slot)] = r;
            if (Environment.GetEnvironmentVariable("IL2WASM_IL_DISPATCH") == "1") Console.Error.WriteLine($"dispatch {slot} on {x}: {r?.ToString() ?? "none"}");
            return r;
        }
        MethodDesc? impl = null;
        if (x is ArrayType arr && x.CanCastTo(slot.OwningType))
        {
            // an array: the generic collection interfaces through SZArrayHelper (with the interface's element type, so
            // IList<Base> on a Derived[] works), the rest through System.Array's own methods
            var owner = slot.OwningType;
            if (owner.IsInterface && owner.HasInstantiation && arr.IsSzArray)
            {
                var helper = _ctx.SystemModule.GetType("System"u8, "SZArrayHelper"u8, throwIfNotFound: false);
                var name = slot.GetName();
                var hm = helper?.GetMethods().FirstOrDefault(h => h.GetName() == name && h.Signature.Length == slot.Signature.Length + 1);
                if (hm != null) impl = _ctx.GetInstantiatedMethod(hm, new Instantiation(owner.Instantiation[0]));
            }
            else if (owner.IsInterface)
            {
                for (var c = x.BaseType; c != null && impl == null; c = c.BaseType) impl = c.ResolveInterfaceMethodToVirtualMethodOnType(slot);
                if (impl != null && impl.IsVirtual) impl = x.BaseType!.FindVirtualFunctionTargetMethodOnObjectType(impl) ?? impl;
            }
            else impl = x.BaseType!.FindVirtualFunctionTargetMethodOnObjectType(slot);
            if (impl != null && impl.IsAbstract) impl = null;
        }
        else if (x.CanCastTo(slot.OwningType))
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
        if (RequestFinalizers()) any = true;
        if (RequestActivation()) any = true;
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

    /// <summary>A type's block of statics: 0 plain, 1 references, 2 thread-static plain, 3 thread-static references
    /// (one thread for now: a thread static is a static of its own block).</summary>
    IAddress StaticBlock(TypeDesc owner, int kind)
    {
        if (!_staticBlockAt.TryGetValue((owner, kind), out int i))
        {
            var dt = (DefType)owner;
            int size = kind switch
            {
                0 => dt.NonGCStaticFieldSize.AsInt, 1 => dt.GCStaticFieldSize.AsInt,
                2 => dt.ThreadNonGcStaticFieldSize.AsInt, _ => dt.ThreadGcStaticFieldSize.AsInt,
            };
            int align = kind switch
            {
                0 => Math.Max(4, dt.NonGCStaticFieldAlignment.AsInt), 2 => Math.Max(8, dt.ThreadNonGcStaticFieldAlignment.AsInt), _ => 8,
            };
            i = _staticBlocks.Count;
            _staticBlockAt[(owner, kind)] = i;
            _staticBlocks.Add((owner, kind != 0, size, align));
        }
        int idx = i;
        return new LateAddr(() => _blockOffsets[idx]);
    }

    int[] _blockOffsets = [];

    internal IAddress StaticAddress(FieldDesc f)
    {
        if (f.IsLiteral) throw new NotSupportedException($"a load of the literal field {f}");
        // (a field with data in the image: "..."u8, array initializers: the data itself)
        if (f.HasRva) return FieldData(f);
        var block = StaticBlock(f.OwningType, (f.IsThreadStatic ? 2 : 0) + (f.HasGCStaticBase ? 1 : 0));
        int off = f.Offset.AsInt;
        return new LateAddr(() => block.Address + off);
    }

    readonly Dictionary<string, IAddress> _named = new();

    /// <summary>A zeroed static block of the runtime's own, by name.</summary>
    internal IAddress NamedStatic(string name, int size, int align)
    {
        if (_named.TryGetValue(name, out var a)) return a;
        int i = _staticBlocks.Count;
        _staticBlocks.Add((null!, false, size, align));
        a = new LateAddr(() => _blockOffsets[i]);
        _named[name] = a;
        return a;
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
        if (m.GetTypicalMethodDefinition() is not EcmaMethod em) return false;
        if (!em.HasCustomAttribute("Il2Wasm.Runtime", "IntrinsicAttribute")) return TryUpstreamIntrinsic(e, m, n, fl);
        var key = ((MetadataType)m.OwningType).GetName() + "." + m.GetName();
        fl.PushArgs(n);
        if (key.StartsWith("TypeInfo.") && TryTypeInfo(e, key[9..])) { fl.SetResult(n); return true; }
        switch (key)
        {
            // (reflection's tables, empty for now: no assembly, resource, name or instance records are kept yet; a
            // type has no reflection record and no generic parameter is a type with an id)
            case "TypeInfo.AssemblyNames": case "TypeInfo.ManifestResources": case "TypeInfo.NameTable": case "TypeInfo.Instances": case "TypeInfo.ProgramTypes":
                e.I32Addr(EmptyTable); fl.SetResult(n); return true;
            case "TypeInfo.Nested": case "TypeInfo.GenericConstraints": e.Op(WOp.Drop); e.I32Addr(EmptyTable); fl.SetResult(n); return true;
            case "TypeInfo.GenericParam": e.Op(WOp.Drop); e.I32(-1); fl.SetResult(n); return true;
            case "TypeInfo.GenericParamMethod": case "TypeInfo.Meta": e.Op(WOp.Drop); e.I32(0); fl.SetResult(n); return true;
            case "TypeInfo.Invoke": case "TypeInfo.ResourceInit": case "TypeInfo.ResourceUnpack":
                // (reached only through a reflection record or a resource, which the module has none of yet)
                e.Op(WOp.Unreachable); return true;
        }
        switch (key)
        {
            case "Rt.Sqrt": e.Op(WOp.F64Sqrt); break;
            case "Rt.Floor": e.Op(WOp.F64Floor); break;
            case "Rt.Ceiling": e.Op(WOp.F64Ceil); break;
            case "Rt.Truncate": e.Op(WOp.F64Trunc); break;
            case "Rt.RoundEven": e.Op(WOp.F64Nearest); break;
            case "Rt.Abs": e.Op(WOp.F64Abs); break;
            case "Rt.Min": e.Op(WOp.F64Min); break;
            case "Rt.Max": e.Op(WOp.F64Max); break;
            case "Rt.CopySign": e.Call(Rt.CanonF64); e.Op(WOp.F64Copysign); break;
            case "Rt.SqrtF": e.Op(WOp.F32Sqrt); break;
            case "Rt.FloorF": e.Op(WOp.F32Floor); break;
            case "Rt.CeilingF": e.Op(WOp.F32Ceil); break;
            case "Rt.TruncateF": e.Op(WOp.F32Trunc); break;
            case "Rt.RoundEvenF": e.Op(WOp.F32Nearest); break;
            case "Rt.AbsF": e.Op(WOp.F32Abs); break;
            case "Rt.MinF": e.Op(WOp.F32Min); break;
            case "Rt.MaxF": e.Op(WOp.F32Max); break;
            case "Rt.F64Bits": e.Op(WOp.I64ReinterpretF64); break;
            case "Rt.F64FromBits": e.Op(WOp.F64ReinterpretI64); break;
            case "Rt.F32Bits": e.Op(WOp.I32ReinterpretF32); break;
            case "Rt.F32FromBits": e.Op(WOp.F32ReinterpretI32); break;
            case "Rt.I64Clz": e.Op(WOp.I64Clz); break;
            case "Rt.I64Ctz": e.Op(WOp.I64Ctz); break;
            case "Rt.I64Popcnt": e.Op(WOp.I64Popcnt); break;
            case "Rt.I32Rotl": e.Op(WOp.I32Rotl); break;
            case "Rt.I32Rotr": e.Op(WOp.I32Rotr); break;
            case "Rt.I64Rotl": e.Op(WOp.I64Rotl); break;
            case "Rt.I64Rotr": e.Op(WOp.I64Rotr); break;
            case "Rt.Retain": e.Call(Rt.Retain); e.Op(WOp.Drop); break;
            case "Rt.Release": e.Call(Rt.Release); break;
            case "Rt.RetainContents": e.Call(Rt.RetainContents); break;
            case "Rt.IsInstanceOf": e.Call(IsInstFn(m.Instantiation[0])); e.I32(0); e.Op(WOp.I32Ne); break;
            case "Rt.BoxDataOffset": e.I32(IlContext.ObjectHeader); break;
            case "Rt.NullRef": e.I32(0); break;
            case "Rt.Share": e.Op(WOp.Drop); break; // (one thread: nothing is shared)
            case "Rt.Rethrow": e.Call(Rt.Retain); e.Emit(WOp.Throw, 0, 0, ExceptionTag); break;
            case "Rt.TlsBase": case "Rt.TlsSize": case "Rt.TakeThrowSite": case "Rt.FirstChanceWanted": case "Rt.FramesWanted": case "Rt.WeakAny": case "Rt.DeadlockDump":
                e.I32(0); break;
            case "Rt.FusedDrop": e.I32(1); break;
            // (a delegate's Target: the object it holds; lambdas' singletons are .NET's targets too, as the IL has them)
            case "Delegate.TargetOf": { int t = e.AddLocal(ValType.I32); e.Set(t); e.Op(WOp.Drop); e.Get(t); e.Call(Rt.Retain); break; }
            case "Rt.DelegateMethodEntry": e.Op(WOp.Drop); e.I32(0); break;
            // (decided by the instantiation: the string is a literal naming a generic definition in CoreLib)
            case "Generics.IsNullable": e.I32(m.Instantiation[0].IsNullable ? 1 : 0); break;
            case "Generics.IsEnum": e.I32(m.Instantiation[0].IsEnum ? 1 : 0); break;
            case "Generics.Implements":
            {
                e.Op(WOp.Drop);
                var gi = GenericDefinition(n.A[0]);
                var t = m.Instantiation[0];
                e.I32(t.CanCastTo(gi.MakeInstantiatedType(t)) ? 1 : 0);
                break;
            }
            case "Generics.New": case "Generics.NewOfNullableArg":
            {
                e.Op(WOp.Drop);
                var gd = GenericDefinition(n.A[0]);
                var args = Enumerable.Range(0, m.Instantiation.Length).Select(i => m.Instantiation[i]).ToArray();
                // (an instantiation the code's own test rules out, IsNullable<T>, is never reached)
                if (key == "Generics.NewOfNullableArg")
                {
                    if (!args[0].IsNullable) { e.Op(WOp.Unreachable); break; }
                    args = [args[0].Instantiation[0]];
                }
                var t = (DefType)gd.MakeInstantiatedType(args);
                if (t.GetDefaultConstructor() is not { } ctor) { e.Op(WOp.Unreachable); break; }
                Constructed(t);
                int o = e.AddLocal(ValType.I32);
                e.I32(IlContext.InstanceSize(t)); e.Tid(TypeId(t)); e.Call(Rt.AllocObject); e.Set(o);
                e.Get(o); e.Call(FunctionFor(ctor));
                e.Get(o);
                break;
            }
            case "Rt.PlaceLockWord":
                // (the lock word of a place: (addr >> 2 & 63) * 64 in a table of 64 words, each on a line of its own)
                e.I32(2); e.Op(WOp.I32ShrU); e.I32(63); e.Op(WOp.I32And); e.I32(6); e.Op(WOp.I32Shl);
                e.I32Addr(NamedStatic("rt.ref.locks", 64 * 64, 64)); e.Op(WOp.I32Add);
                break;
            // (no groups yet: a store through an address is a plain one)
            case "Rt.LinkAt": e.Op(WOp.Drop); break;
            case "Rt.UnlinkAt": e.Op(WOp.Drop); e.Call(Rt.Release); break;
            case "Rt.ArenaIsSlot": e.Op(WOp.Drop); e.Op(WOp.Drop); e.I32(0); break;
            // (no type is cycle-capable yet: no object is ever a group's member, and these are never reached at run time)
            case "Rt.ArenaClearObject": case "Rt.ArenaFinalizeObject": case "Rt.ArenaFreeObject": case "Rt.ArenaVisitObject": e.Op(WOp.Drop); break;
            case "Rt.ArenaFinType": case "Rt.ArenaSlots": e.Op(WOp.Drop); e.I32(0); break;
            case "Rt.ThrowSites": case "Rt.FrameMethods": e.I32(0); break;
            case "Rt.ArrayGrow": e.Op(WOp.Drop); e.Op(WOp.Drop); e.Op(WOp.Drop); e.Op(WOp.Drop); e.I32(0); break;
            case "Rt.ReleaseThreadStatics": case "Rt.RefLock": case "Rt.RefUnlock": break;
            case "Rt.WeakDying": case "Rt.FirstChance": case "Rt.MemStatsRecord": e.Op(WOp.Drop); if (n.HasValue) e.I32(0); break;
            case var sw when IsSwitch(sw, out int value): e.I32(value); break;
            case "Rt.SccOf": e.Op(WOp.Drop); e.I32(0); break; // (no type is cycle-capable yet: no groups)
            case "Rt.MemStatsHash": e.Op(WOp.Drop); break;
            case "Rt.ArrayMoveMode": e.I32(0); break;
            case "Rt.MarkConstructed": Constructed(m.Instantiation[0]); e.Tid(TypeId(m.Instantiation[0])); break;
            case "Rt.ArrayRankAt": case "Il2WasmArrays.RankOf": e.Call(ArrayFn("rank")); break;
            case "Rt.ArrayElementSizeAt": case "Il2WasmArrays.ElementSize": e.Call(ArrayFn("elem_size")); break;
            case "Il2WasmArrays.HasCountedElements": e.Call(ArrayFn("counted")); break;
            case "Il2WasmArrays.CopyElements": e.Call(ArrayFn("copy")); break;
            case "Il2WasmArrays.ClearElements": e.Call(ArrayFn("clear")); break;
            case "Il2WasmArrays.BoxesElements": break;
            case "TextScan.Eq16":
            {
                int v = e.AddLocal(ValType.I32);
                e.Set(v); e.Emit(WOp.V128Load); e.Get(v); e.Op(WOp.I16x8Splat); e.Op(WOp.I16x8Eq); e.Op(WOp.I16x8Bitmask);
                break;
            }
            case "TextScan.Upper16": case "TextScan.Lower16":
            {
                // eight chars: the ASCII letters' case changed (x -/+ 32 where x - base <u 26) into dst; the lanes above
                // 127 of src as a mask
                bool up = key == "TextScan.Upper16";
                int d = e.AddLocal(ValType.I32), s = e.AddLocal(ValType.I32);
                e.Set(d); e.Set(s);
                e.Get(d);
                e.Get(s); e.Emit(WOp.V128Load);
                e.Get(s); e.Emit(WOp.V128Load); e.I32(up ? 'a' : 'A'); e.Op(WOp.I16x8Splat); e.Op(WOp.I16x8Sub);
                e.I32(26); e.Op(WOp.I16x8Splat); e.Op(WOp.I16x8LtU); e.I32(32); e.Op(WOp.I16x8Splat); e.Op(WOp.V128And);
                e.Op(up ? WOp.I16x8Sub : WOp.I16x8Add);
                e.Emit(WOp.V128Store);
                e.Get(s); e.Emit(WOp.V128Load); e.I32(127); e.Op(WOp.I16x8Splat); e.Op(WOp.I16x8GtU); e.Op(WOp.I16x8Bitmask);
                break;
            }
            case "TextScan.Eq16Any3":
            {
                int c = e.AddLocal(ValType.I32), b = e.AddLocal(ValType.I32), a = e.AddLocal(ValType.I32), ad = e.AddLocal(ValType.I32);
                e.Set(c); e.Set(b); e.Set(a); e.Set(ad);
                e.Get(ad); e.Emit(WOp.V128Load); e.Get(a); e.Op(WOp.I16x8Splat); e.Op(WOp.I16x8Eq);
                e.Get(ad); e.Emit(WOp.V128Load); e.Get(b); e.Op(WOp.I16x8Splat); e.Op(WOp.I16x8Eq); e.Op(WOp.V128Or);
                e.Get(ad); e.Emit(WOp.V128Load); e.Get(c); e.Op(WOp.I16x8Splat); e.Op(WOp.I16x8Eq); e.Op(WOp.V128Or);
                e.Op(WOp.I16x8Bitmask);
                break;
            }
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
            case "Mem.RefI32": break;
            case "Rt.I32Clz": e.Op(WOp.I32Clz); break;
            case "Rt.I32Ctz": e.Op(WOp.I32Ctz); break;
            case "Rt.I32Popcnt": e.Op(WOp.I32Popcnt); break;
            case "Rt.IsReference": e.I32(Kinds.Of(m.Instantiation[0]) == VK.Ref ? 1 : 0); break;
            case "Rt.ContainsReferences": e.I32(Kinds.Counted(m.Instantiation[0]) ? 1 : 0); break;
            case "Rt.SizeOf": e.I32(Kinds.Of(m.Instantiation[0]) == VK.Struct ? ((DefType)m.Instantiation[0]).InstanceFieldSize.AsInt : Kinds.SizeOf(m.Instantiation[0])); break;
            case "Rt.ReleaseElements": e.Call(Rt.Range(m.Instantiation[0], false)); break;
            case "Rt.RetainElements": e.Call(Rt.Range(m.Instantiation[0], true)); break;
            // (the program as compiled here: one thread, no groups, the allocator's narrow size steps; the runtime's
            // switches at their defaults)
            case "Rt.ThreadMode": case "Rt.ShareCheck": case "Rt.Checked": case "Rt.MemStatsOn": case "Rt.WideSizes": case "Rt.RuntimeLockHeld": e.I32(0); break;
            case "Rt.SingleThreaded": case "Rt.RipePages": case "Rt.HoldPages": e.I32(1); break;
            case "Rt.ThreadId": e.I32(1); break;
            case "Rt.BulkEnter": case "Rt.BulkExit": case "Rt.HoldEpoch": case "Rt.ReleaseEpoch": case "Rt.RuntimeLock": case "Rt.RuntimeUnlock": case "Rt.MemStatsGrow": break;
            case "Rt.PartWeak": e.Op(WOp.Drop); break;
            case "Rt.MoveRefs": e.Op(WOp.Drop); e.Op(WOp.Drop); e.Op(WOp.Drop); e.I32(0); break;
            // (the shared memory's atomic instructions: right with one thread, and with more)
            case "Atomic.Load32": e.Load(WOp.I32AtomicLoad); break;
            case "Atomic.Store32": e.Store(WOp.I32AtomicStore); break;
            case "Atomic.Add32": case "Atomic.CountAdd32": e.Load(WOp.I32AtomicRmwAdd); break;
            case "Atomic.Exchange32": e.Load(WOp.I32AtomicRmwXchg); break;
            case "Atomic.CompareExchange32": case "Atomic.CountCompareExchange32": e.Load(WOp.I32AtomicRmwCmpxchg); break;
            case "Atomic.Load64": e.Load(WOp.I64AtomicLoad); break;
            case "Atomic.Store64": e.Store(WOp.I64AtomicStore); break;
            case "Atomic.Add64": e.Load(WOp.I64AtomicRmwAdd); break;
            case "Atomic.Exchange64": e.Load(WOp.I64AtomicRmwXchg); break;
            case "Atomic.CompareExchange64": e.Load(WOp.I64AtomicRmwCmpxchg); break;
            case "Atomic.Fence": e.Emit(WOp.AtomicFence); break;
            case "Atomic.Wait32": e.Op(WOp.Drop); e.Op(WOp.Drop); e.Op(WOp.Drop); e.I32(2); break; // ("timed out": no other thread can change it)
            case "Atomic.Notify": e.Op(WOp.Drop); e.Op(WOp.Drop); e.I32(0); break;
            case "Rt.TypeName":
                e.Call(Rt.NonNull); e.Load(WOp.I32Load, 4);
                _nameTable ??= StaticNameTable();
                e.I32(2); e.Op(WOp.I32Shl); e.I32Addr(_nameTable); e.Op(WOp.I32Add); e.Load(WOp.I32Load);
                break;
            case "Rt.TypeNameById":
                _nameTable ??= StaticNameTable();
                e.I32(2); e.Op(WOp.I32Shl); e.I32Addr(_nameTable); e.Op(WOp.I32Add); e.Load(WOp.I32Load);
                break;
            default:
                if (!Lenient) throw new NotSupportedException($"the intrinsic {key}");
                Warnings.Add("missing intrinsic " + key);
                e.Op(WOp.Unreachable);
                return true;
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

        if (CountObjects)
        {
            var lf = Module.AddFunction("il2wasm_live", new FuncType([], [ValType.I32]));
            lf.Exports.Add("il2wasm_live");
            _counted ??= NamedStatic("il2wasm.live", 4, 4);
            var le = new Emitter(lf);
            le.I32Addr(_counted); le.Load(WOp.I32Load);
        }
        else if (LiveCounter != null)
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
        // il-runtime: the first pass's functions (before the type tests: they make some)
        EmitEhSupport();
        // type ids: 1 is string's (CoreLib's contract); the others in a preorder of the class hierarchy from 2
        CloseTypeIds();
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
        var byFinal = new TypeDesc?[_types.Count + 2];
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
            // (IL2WASM_IL_TRACE_DISPATCH=1: the type id no case had, the object's count and address, before the trap)
            if (Environment.GetEnvironmentVariable("IL2WASM_IL_TRACE_DISPATCH") == "1") { e.Get(tid); e.Get(0); e.Load(WOp.I32Load); e.Get(0); e.Call(TraceImport); }
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
                EmitFinalizeStep(e, x);
                if (x is ArrayType at)
                {
                    int es = Kinds.SizeOf(at.ElementType);
                    if (Kinds.Of(at.ElementType) == VK.Ref)
                    {
                        // the elements, first to last
                        e.Get(0); e.Load(WOp.I32Load, 12); e.Set(n); e.I32(0); e.Set(i);
                        var brk = e.Block(); var loop = e.Loop();
                        e.Get(i); e.Get(n); e.Op(WOp.I32GeU); e.BrIf(brk);
                        e.Get(0); e.Get(i); e.I32(2); e.Op(WOp.I32Shl); e.Op(WOp.I32Add); e.Load(WOp.I32Load, ArrayDataOffset(at)); e.Call(Rt.Release);
                        e.Get(i); e.I32(1); e.Op(WOp.I32Add); e.Set(i); e.Br(loop);
                        e.End(); e.End();
                    }
                    else if (Kinds.Counted(at.ElementType)) { e.Get(0); e.I32(ArrayDataOffset(at)); e.Op(WOp.I32Add); e.Get(0); e.Load(WOp.I32Load, 12); e.Call(Rt.Range(at.ElementType, false)); }
                    e.Get(0); e.Get(0); e.Load(WOp.I32Load, 12); e.I32(es); e.Op(WOp.I32Mul); e.I32(ArrayDataOffset(at)); e.Op(WOp.I32Add);
                }
                else if (x.IsString)
                {
                    e.Get(0); e.Get(0); e.Load(WOp.I32Load, 12); e.I32(1); e.Op(WOp.I32Shl); e.I32(18); e.Op(WOp.I32Add);
                }
                else
                {
                    for (TypeDesc? c = x.IsValueType ? null : x; c != null; c = c.BaseType) // (a box: its value's counts below)
                        foreach (var fd in c.GetFields())
                        {
                            if (fd.IsStatic) continue;
                            if (Kinds.Of(fd.FieldType) == VK.Ref) { e.Get(0); e.Load(WOp.I32Load, IlContext.FieldOffset(fd)); e.Call(Rt.Release); }
                            else if (Kinds.Of(fd.FieldType) == VK.Struct && Kinds.Counted(fd.FieldType)) { e.Get(0); e.I32(IlContext.FieldOffset(fd)); e.Op(WOp.I32Add); e.Call(Rt.ReleaseStruct(fd.FieldType)); }
                        }
                    if (Kinds.Of(x) == VK.Struct && Kinds.Counted(x)) { e.Get(0); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); e.Call(Rt.ReleaseStruct(x)); }
                    e.Get(0); e.I32(x.IsValueType ? IlContext.ObjectHeader + Math.Max(4, Kinds.SizeOf(x)) : IlContext.InstanceSize((DefType)x));
                }
                e.Call(Rt.Free); e.Op(WOp.Return);
                e.End();
            }
        }
        // retain_contents: each constructed type's references counted (an object copied whole: MemberwiseClone)
        if (Rt.RetainContentsIfMade is { } rcf)
        {
            var e = new Emitter(rcf);
            int tid = e.AddLocal(ValType.I32);
            e.Get(0); e.Load(WOp.I32Load, 4); e.Set(tid);
            foreach (var x in _constructedOrder)
            {
                if (x is ArrayType at)
                {
                    if (!Kinds.Counted(at.ElementType)) continue;
                    e.Get(tid); e.I32(Final(x)); e.Op(WOp.I32Eq); e.If();
                    e.Get(0); e.I32(ArrayDataOffset(at)); e.Op(WOp.I32Add); e.Get(0); e.Load(WOp.I32Load, 12); e.Call(Rt.Range(at.ElementType, true));
                    e.Op(WOp.Return); e.End();
                    continue;
                }
                if (x.IsValueType)
                {
                    if (!Kinds.Counted(x)) continue;
                    e.Get(tid); e.I32(Final(x)); e.Op(WOp.I32Eq); e.If();
                    e.Get(0); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); e.Call(Rt.RetainStruct(x)); e.Op(WOp.Return); e.End();
                    continue;
                }
                var refs = new List<FieldDesc>();
                for (TypeDesc? c = x; c != null; c = c.BaseType)
                    foreach (var fd in c.GetFields()) if (!fd.IsStatic && Kinds.Counted(fd.FieldType)) refs.Add(fd);
                if (refs.Count == 0) continue;
                e.Get(tid); e.I32(Final(x)); e.Op(WOp.I32Eq); e.If();
                foreach (var fd in refs)
                {
                    if (Kinds.Of(fd.FieldType) == VK.Ref) { e.Get(0); e.Load(WOp.I32Load, IlContext.FieldOffset(fd)); e.Call(Rt.Retain); e.Op(WOp.Drop); }
                    else { e.Get(0); e.I32(IlContext.FieldOffset(fd)); e.Op(WOp.I32Add); e.Call(Rt.RetainStruct(fd.FieldType)); }
                }
                e.Op(WOp.Return); e.End();
            }
        }
        // the type name table, in final id order
        _typeNamesByFinal = new string[_types.Count + 2];
        foreach (var t in _types) { _typeNamesByFinal[Final(t)] = TypeName(t); byFinal[Final(t)] = t; }
        EmitTypeTables(Final, _types.Count + 2, byFinal);
        EmitArrayFns(Final);
        EmitCreateInstance(Final);
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
        // the blobs (field data, type tables), then their addresses patched in
        foreach (var b in _blobs)
        {
            Align(Math.Max(1, b.Align));
            b.Offset = dataBase + data.Count;
            data.AddRange(b.Bytes);
        }
        foreach (var b in _blobs)
            foreach (var (off, a) in b.Relocs)
            {
                var v = BitConverter.GetBytes(a.Address);
                for (int k = 0; k < 4; k++) data[b.Offset - dataBase + off + k] = v[k];
            }
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
                c.WatchEvent(e, 1);
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
                // (IL2WASM_IL_WATCH=address, a debugging aid: each release of that object logged, and the one that
                // frees it traps, so its stack shows who)
                c.WatchEvent(e, 2);
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
                        // a * b overflows when a != 0 and res / a != b; signed, a = -1 overflows only for b = MinValue
                        // (and res / -1 would trap there: MinValue / -1)
                        e.Get(0); e.I64(0); e.Op(WOp.I64Ne);
                        e.If(ValType.I32);
                        if (!un)
                        {
                            e.Get(0); e.I64(-1); e.Op(WOp.I64Eq);
                            e.If(ValType.I32); e.Get(1); e.I64(long.MinValue); e.Op(WOp.I64Eq);
                            e.Else(); e.Get(res); e.Get(0); e.Op(WOp.I64DivS); e.Get(1); e.Op(WOp.I64Ne); e.End();
                        }
                        else { e.Get(res); e.Get(0); e.Op(WOp.I64DivU); e.Get(1); e.Op(WOp.I64Ne); }
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
                    // (the bound past the largest, exact as a double: 2^63 - 1 is not one, 2^63 is)
                    e.Get(d); e.Op(WOp.F64Trunc); e.F64((double)(hi + 1)); e.Op(WOp.F64Ge); e.Op(WOp.I32Or);
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

        /// <summary>(addr, count): the counts of count elements of T from addr (nothing for a T without references).</summary>
        public WasmFunction Range(TypeDesc t, bool retain) => Cached((retain ? "rt.retain_range " : "rt.release_range ") + t, FT([I, I], []), e =>
        {
            if (!Kinds.Counted(t)) return;
            int size = Kinds.SizeOf(t), i = e.AddLocal(I);
            var brk = e.Block(); var loop = e.Loop();
            e.Get(i); e.Get(1); e.Op(WOp.I32GeU); e.BrIf(brk);
            e.Get(0); e.Get(i); e.I32(size); e.Op(WOp.I32Mul); e.Op(WOp.I32Add);
            if (Kinds.Of(t) == VK.Ref) { e.Load(WOp.I32Load); if (retain) { e.Call(Retain); e.Op(WOp.Drop); } else e.Call(Release); }
            else e.Call(StructCounts(t, retain));
            e.Get(i); e.I32(1); e.Op(WOp.I32Add); e.Set(i); e.Br(loop);
            e.End(); e.End();
        });

        /// <summary>One thread: (location, value) -> old, location += value; (location, expected, replacement) -> old,
        /// replaced when it was the expected.</summary>
        public WasmFunction PlainRmw(bool cmpxchg) => cmpxchg
            ? Cached("rt.plain_cmpxchg", FT([I, I, I], [I]), e =>
            {
                int old = e.AddLocal(I);
                e.Get(0); e.Load(WOp.I32Load); e.Set(old);
                e.Get(old); e.Get(1); e.Op(WOp.I32Eq); e.If(); e.Get(0); e.Get(2); e.Store(WOp.I32Store); e.End();
                e.Get(old);
            })
            : Cached("rt.plain_add", FT([I, I], [I]), e =>
            {
                int old = e.AddLocal(I);
                e.Get(0); e.Load(WOp.I32Load); e.Set(old);
                e.Get(0); e.Get(old); e.Get(1); e.Op(WOp.I32Add); e.Store(WOp.I32Store);
                e.Get(old);
            });

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

        /// <summary>(f64) -> the same, a NaN made the canonical one (SPEC 7.3).</summary>
        public WasmFunction CanonF64 => Cached("rt.canon_f64", FT([ValType.F64], [ValType.F64]), e =>
        {
            e.Get(0); e.Get(0); e.Op(WOp.F64Ne);
            e.If(ValType.F64); e.I64(0x7FF8000000000000L); e.Op(WOp.F64ReinterpretI64); e.Else(); e.Get(0); e.End();
        });

        WasmFunction? _retainContents;
        /// <summary>(object): a count for each reference in it (its body: by type, when the program's types are known).</summary>
        public WasmFunction RetainContents => _retainContents ??= M.AddFunction("rt.retain_contents", FT([I], []));
        public WasmFunction? RetainContentsIfMade => _retainContents;

        public WasmFunction PlainAdd64 => Cached("rt.plain_add64", FT([I, L], [L]), e =>
        {
            int old = e.AddLocal(L);
            e.Get(0); e.Load(WOp.I64Load); e.Set(old);
            e.Get(0); e.Get(old); e.Get(1); e.Op(WOp.I64Add); e.Store(WOp.I64Store);
            e.Get(old);
        });

        /// <summary>C#'s floating remainder (fmod, exact): the runtime's Rt.FMod; a float's through double (exact: the
        /// remainder of two floats is a float).</summary>
        public WasmFunction FRem(bool d)
        {
            var fmod = c.FunctionFor(c.CoreMethod("Il2Wasm.Runtime", "Rt", "FMod"));
            if (d) return fmod;
            return Cached("rt.fmodf", FT([ValType.F32, ValType.F32], [ValType.F32]), e =>
            {
                e.Get(0); e.Op(WOp.F64PromoteF32); e.Get(1); e.Op(WOp.F64PromoteF32);
                e.Call(fmod);
                e.Op(WOp.F32DemoteF64);
            });
        }
    }
}
