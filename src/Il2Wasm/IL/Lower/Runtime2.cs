#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Il2Wasm.IL.Ir;
using Il2Wasm.IL.Types;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

/// <summary>Bytes in the data segment, placed when the module is laid out; Relocs are addresses written into them.</summary>
sealed class Blob : IAddress
{
    public byte[] Bytes = [];
    public int Align = 4;
    public List<(int Off, IAddress Addr)> Relocs = [];
    public int Offset;
    public int Address => Offset;
    public bool Immortal => true;
}

/// <summary>The IL CoreLib's side of the runtime: the native libraries it provides (il2wasm.natives.txt), the intrinsics
/// of upstream's methods (by name, as the operation-tree compiler's Driver.IsUpstreamIntrinsic), the type tables
/// behind TypeInfo, delegates, and the array helpers.</summary>
public sealed partial class IlCompiler
{
    readonly List<Blob> _blobs = [];

    internal Blob DataBlob(byte[] bytes, int align, List<(int, IAddress)>? relocs = null)
    {
        var b = new Blob { Bytes = bytes, Align = align, Relocs = relocs ?? [] };
        _blobs.Add(b);
        return b;
    }

    WasmFunction? _trace;
    /// <summary>IL2WASM_IL_TRACE's import (a debugging aid): il2wasm_trace.enter(a, b, c).</summary>
    internal WasmFunction TraceImport => _trace ??= Module.AddImport("il2wasm_trace", "enter", "trace", new FuncType([ValType.I32, ValType.I32, ValType.I32], []));

    static readonly int? s_watch = int.TryParse(Environment.GetEnvironmentVariable("IL2WASM_IL_WATCH"), out int w) ? w : null;
    static readonly int s_watchAt = int.TryParse(Environment.GetEnvironmentVariable("IL2WASM_IL_WATCH_AT"), out int wa) ? wa : -1;
    WasmGlobal? _watchCount;

    /// <summary>IL2WASM_IL_WATCH=address (a debugging aid): each event on the object at that address logged through
    /// the trace import as (kind, its count word, the event's number); kind 1 retain, 2 release, 3 allocated, 4 freed.
    /// IL2WASM_IL_WATCH_AT=n traps at event n (its stack shows who).</summary>
    internal void WatchEvent(Emitter e, int kind, int addrLocal = 0)
    {
        if (s_watch is not int watch) return;
        if (_watchCount == null) { _watchCount = new WasmGlobal("il2wasm.watch", ValType.I32, true, 0); Module.Globals.Add(_watchCount); }
        e.Get(addrLocal); e.I32(watch); e.Op(WOp.I32Eq);
        e.If();
        e.GGet(_watchCount); e.I32(1); e.Op(WOp.I32Add); e.GSet(_watchCount);
        e.I32(kind * 1000); e.Get(addrLocal); e.Load(WOp.I32Load, 4); e.I32(65536); e.Op(WOp.I32Mul); e.Get(addrLocal); e.Load(WOp.I32Load); e.Op(WOp.I32Add); e.Op(WOp.I32Add); e.GGet(_watchCount); e.I32(0); e.Call(TraceImport);
        e.GGet(_watchCount); e.I32(s_watchAt); e.Op(WOp.I32Eq); e.If(); e.Op(WOp.Unreachable); e.End();
        e.End();
    }

    Blob? _emptyTable;
    /// <summary>A table of no entries (its count, 0, then zeros).</summary>
    internal Blob EmptyTable => _emptyTable ??= DataBlob(new byte[16], 4);

    readonly Dictionary<FieldDesc, Blob> _rva = new();

    /// <summary>A field's data (RuntimeHelpers.InitializeArray, CreateSpan): read-only, in the data segment.</summary>
    internal Blob FieldData(FieldDesc f)
    {
        if (_rva.TryGetValue(f, out var b)) return b;
        var ef = (EcmaField)f.GetTypicalFieldDefinition();
        b = DataBlob(ef.GetFieldRvaData(), 8);
        _rva[f] = b;
        return b;
    }

    // ------------------------------------------------------------------ native libraries (docs/CORELIB.md 7)

    readonly HashSet<string> _libraries = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string, string), (string type, string method)> _pinvokes = new();
    readonly Dictionary<(string, string, string), (string type, string method)> _internalCalls = new();

    /// <summary>Reads the binding table of the runtime-provided native libraries (the framework's il2wasm.natives.txt).</summary>
    public void LoadNatives(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var p = line.Split('\t');
            switch (p[0])
            {
                case "library": _libraries.Add(Strip(p[1])); break;
                case "pinvoke": _pinvokes[(Strip(p[1]), p[2])] = (p[3], p[4]); break;
                case "internalcall": _internalCalls[(p[1], p[2], p[3])] = (p[4], p[5]); break;
            }
        }
    }

    static string Strip(string lib) => lib.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || lib.EndsWith(".so") ? Path.GetFileNameWithoutExtension(lib) : lib;

    MethodDesc CoreByMetadataName(string type, string method)
    {
        var parts = type.Split('/');
        int dot = parts[0].LastIndexOf('.');
        var t = _ctx.SystemModule.GetType(Encoding.UTF8.GetBytes(dot < 0 ? "" : parts[0][..dot]), Encoding.UTF8.GetBytes(parts[0][(dot + 1)..]));
        foreach (var nested in parts.Skip(1)) t = t.GetNestedType(Encoding.UTF8.GetBytes(nested));
        return t.GetMethods().FirstOrDefault(m => m.GetName() == method) ?? throw new InvalidOperationException($"natives table: CoreLib has no {type}.{method}");
    }

    static string MetadataName(MetadataType t) =>
        t.ContainingType is MetadataType c ? MetadataName(c) + "/" + t.GetName() : (t.GetNamespace().Length > 0 ? t.GetNamespace() + "." : "") + t.GetName();

    /// <summary>A P/Invoke or an internal call: the runtime method that serves it, or the function that throws what
    /// .NET throws when the library or its entry point is missing (SPEC 8: a build warning names it).</summary>
    WasmFunction? NativeFunction(MethodDesc m, EcmaMethod em)
    {
        if (em.IsPInvoke)
        {
            var md = em.GetPInvokeMethodMetadata();
            var lib = Strip(md.Module);
            var entry = string.IsNullOrEmpty(md.Name) ? em.GetName() : md.Name;
            if (_pinvokes.TryGetValue((lib, entry), out var target)) return FunctionFor(CoreByMetadataName(target.type, target.method));
            if (_libraries.Contains(lib)) return Thrower(m, "System", "EntryPointNotFoundException", $"Unable to find an entry point named '{entry}' in shared library '{md.Module}'.");
            Warnings.Add($"P/Invoke into '{md.Module}' ({entry}, {m}): the library has no WASM build; the call throws DllNotFoundException");
            return Thrower(m, "System", "DllNotFoundException", $"Unable to load shared library '{md.Module}' or one of its dependencies.");
        }
        if (em.IsInternalCall)
        {
            var key = (((EcmaModule)em.Module).Assembly.GetName().Name!, MetadataName((MetadataType)em.OwningType), em.GetName());
            if (_internalCalls.TryGetValue(key, out var target)) return FunctionFor(CoreByMetadataName(target.type, target.method));
            return null;
        }
        return null;
    }

    WasmFunction Thrower(MethodDesc m, string ns, string type, string message)
    {
        var f = Module.AddFunction(m.ToString(), Sig(m));
        var t = (MetadataType)_ctx.SystemModule.GetType(Encoding.UTF8.GetBytes(ns), Encoding.UTF8.GetBytes(type));
        var ctor = t.GetMethods().First(x => x.IsConstructor && x.Signature.Length == 1 && x.Signature[0].IsString);
        Constructed(t);
        var e = new Emitter(f);
        int ex = e.AddLocal(ValType.I32);
        e.I32(IlContext.InstanceSize(t)); e.Tid(TypeId(t)); e.Call(Rt.AllocObject); e.Set(ex);
        e.Get(ex); e.I32Addr(StringLiteral(message)); e.Call(FunctionFor(ctor));
        e.Get(ex); e.Emit(WOp.Throw, 0, 0, ExceptionTag);
        return f;
    }

    // ------------------------------------------------------------------ upstream's intrinsics, by name

    /// <summary>Upstream methods the runtime implements (their IL is the JIT's placeholder), as the old compiler did.</summary>
    internal bool TryUpstreamIntrinsic(Emitter e, MethodDesc m, Node n, FunctionLowering fl)
    {
        if (m.OwningType is not MetadataType ot || ot.Module != _ctx.SystemModule) return false;
        if (TryActivation(e, m, n, fl)) return true;
        var type = (ot.GetNamespace().Length > 0 ? ot.GetNamespace() + "." : "") + ot.GetName();
        var name = m.GetName();
        TypeDesc? T0() => m.Instantiation.Length > 0 ? m.Instantiation[0] : null;
        switch (type + "." + name)
        {
            case "System.Runtime.CompilerServices.Unsafe.AsPointer":
            case "System.Runtime.CompilerServices.Unsafe.AsRef":
                fl.PushArgs(n); break;
            case "System.Runtime.CompilerServices.Unsafe.As":
                fl.PushArgs(n);
                // (As<T>(object): the reference as T, a count of its own)
                if (n.Kind == VK.Ref) e.Call(Rt.Retain);
                break;
            case "System.Runtime.CompilerServices.Unsafe.AreSame": fl.PushArgs(n); e.Op(WOp.I32Eq); break;
            case "System.Runtime.CompilerServices.Unsafe.IsAddressGreaterThan": fl.PushArgs(n); e.Op(WOp.I32GtU); break;
            case "System.Runtime.CompilerServices.Unsafe.IsAddressLessThan": fl.PushArgs(n); e.Op(WOp.I32LtU); break;
            case "System.Runtime.CompilerServices.Unsafe.AddByteOffset": fl.PushArgs(n); e.Op(WOp.I32Add); break;
            case "System.Runtime.CompilerServices.Unsafe.SubtractByteOffset": fl.PushArgs(n); e.Op(WOp.I32Sub); break;
            case "System.Runtime.CompilerServices.Unsafe.ByteOffset":
            {
                // (origin, target): target - origin
                fl.PushArgs(n);
                int t = e.AddLocal(ValType.I32);
                e.Set(t); e.I32(-1); e.Op(WOp.I32Mul); e.Get(t); e.Op(WOp.I32Add);
                break;
            }
            case "System.Runtime.CompilerServices.Unsafe.Copy":
                if (Kinds.Counted(T0()!)) throw new NotSupportedException($"Unsafe.Copy of {T0()} (counted)");
                fl.PushArgs(n); e.I32(SizeOfAny(T0()!)); e.Emit(WOp.MemoryCopy); break;
            case "System.Runtime.CompilerServices.Unsafe.CopyBlock":
            case "System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned":
                fl.PushArgs(n); e.Emit(WOp.MemoryCopy); break;
            case "System.Runtime.CompilerServices.Unsafe.InitBlock":
            case "System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned":
                fl.PushArgs(n); e.Emit(WOp.MemoryFill); break;
            case "System.Runtime.CompilerServices.Unsafe.SkipInit": fl.PushArgs(n); e.Op(WOp.Drop); break;
            case "System.Runtime.CompilerServices.Unsafe.Unbox": fl.PushArgs(n); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); break;
            case "System.Runtime.CompilerServices.Unsafe.SizeOf": e.I32(SizeOfAny(T0()!)); break;
            case "System.SpanHelpers.memmove": case "System.SpanHelpers.Memmove":
                if (m.HasInstantiation) return false;
                fl.PushArgs(n); e.Emit(WOp.MemoryCopy); break;
            case "System.SpanHelpers.memset": fl.PushArgs(n); e.Emit(WOp.MemoryFill); break;
            case "System.SpanHelpers.ClearWithoutReferences":
            {
                fl.PushArgs(n);
                int len = e.AddLocal(ValType.I32);
                e.Set(len); e.I32(0); e.Get(len); e.Emit(WOp.MemoryFill);
                break;
            }
            // (byte and ushort: upstream's "must expand intrinsic" calls itself; the 8- and 16-bit atomics)
            case "System.Threading.Interlocked.Exchange" when m.Signature[1].Category is TypeFlags.Byte or TypeFlags.UInt16:
                fl.PushArgs(n);
                e.Emit(m.Signature[1].Category == TypeFlags.Byte ? WOp.I32AtomicRmw8XchgU : WOp.I32AtomicRmw16XchgU, 0, m.Signature[1].Category == TypeFlags.Byte ? 0 : 1);
                break;
            case "System.Threading.Interlocked.CompareExchange" when m.Signature[1].Category is TypeFlags.Byte or TypeFlags.UInt16:
            {
                // (location, value, comparand) -> the instruction's (address, expected, replacement)
                fl.PushArgs(n);
                int cmp = e.AddLocal(ValType.I32), val = e.AddLocal(ValType.I32);
                e.Set(cmp); e.Set(val); e.Get(cmp); e.Get(val);
                e.Emit(m.Signature[1].Category == TypeFlags.Byte ? WOp.I32AtomicRmw8CmpxchgU : WOp.I32AtomicRmw16CmpxchgU, 0, m.Signature[1].Category == TypeFlags.Byte ? 0 : 1);
                break;
            }
            case "System.Threading.Interlocked.MemoryBarrier":
            case "System.Threading.Volatile.ReadBarrier":
            case "System.Threading.Volatile.WriteBarrier":
                break; // (one thread)
            case "System.Runtime.CompilerServices.RuntimeHelpers.GetRawData": fl.PushArgs(n); e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); break;
            case "System.Runtime.CompilerServices.RuntimeHelpers.IsBitwiseEquatable":
                e.I32(T0() is { } bt && (bt.IsPrimitive || bt.IsEnum || bt.IsPointer) && bt.Category is not (TypeFlags.Single or TypeFlags.Double) ? 1 : 0); break;
            case "System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences":
                e.I32(Kinds.Counted(T0()!) ? 1 : 0); break;
            case "System.Runtime.CompilerServices.RuntimeHelpers.ObjectHasComponentSize":
            {
                // a string or an array
                fl.PushArgs(n);
                e.Load(WOp.I32Load, 4); e.Call(HasComponentSizeFn()); break;
            }
            case "System.Object.GetType":
                fl.PushArgs(n);
                e.Call(Rt.NonNull); e.Load(WOp.I32Load, 4); e.Call(FunctionFor(CoreMethod("System", "Type", "FromId")));
                break;
            case "System.Double.ConvertToIntegerNative": case "System.Single.ConvertToIntegerNative":
            {
                fl.PushArgs(n);
                bool d = ot.GetName() == "Double";
                var to = T0()!;
                bool i64 = Kinds.Of(to) == VK.I64, un = !Kinds.IsSigned(to);
                e.Op(i64 ? (d ? (un ? WOp.I64TruncSatF64U : WOp.I64TruncSatF64S) : (un ? WOp.I64TruncSatF32U : WOp.I64TruncSatF32S))
                    : (d ? (un ? WOp.I32TruncSatF64U : WOp.I32TruncSatF64S) : (un ? WOp.I32TruncSatF32U : WOp.I32TruncSatF32S)));
                int sz = Kinds.SizeOf(to);
                if (sz < 4)
                {
                    // clamped to the narrow type's range
                    int t = e.AddLocal(ValType.I32);
                    int lo = un ? 0 : -(1 << (sz * 8 - 1)), hi = un ? (1 << (sz * 8)) - 1 : (1 << (sz * 8 - 1)) - 1;
                    e.Set(t);
                    e.Get(t); e.I32(lo); e.Get(t); e.I32(lo); e.Op(WOp.I32GtS); e.Op(WOp.Select); e.Set(t);
                    e.Get(t); e.I32(hi); e.Get(t); e.I32(hi); e.Op(WOp.I32LtS); e.Op(WOp.Select);
                }
                break;
            }
            case "System.Numerics.BitOperations.LeadingZeroCount":
            case "System.Numerics.BitOperations.TrailingZeroCount":
            case "System.Numerics.BitOperations.PopCount":
            case "System.Numerics.BitOperations.Log2":
            {
                fl.PushArgs(n);
                bool l = Kinds.Of(m.Signature[0]) == VK.I64;
                if (name == "Log2")
                {
                    // 31 - clz(v | 1) (63 for a 64-bit value)
                    if (l) { e.I64(1); e.Op(WOp.I64Or); e.Op(WOp.I64Clz); e.Op(WOp.I32WrapI64); e.I32(63); }
                    else { e.I32(1); e.Op(WOp.I32Or); e.Op(WOp.I32Clz); e.I32(31); }
                    int t = e.AddLocal(ValType.I32);
                    e.Set(t); e.Get(t); e.Op(WOp.I32Xor); // (31 - x == 31 ^ x for x in 0..31)
                    break;
                }
                e.Op(name switch
                {
                    "LeadingZeroCount" => l ? WOp.I64Clz : WOp.I32Clz,
                    "TrailingZeroCount" => l ? WOp.I64Ctz : WOp.I32Ctz,
                    _ => l ? WOp.I64Popcnt : WOp.I32Popcnt,
                });
                if (l) e.Op(WOp.I32WrapI64);
                break;
            }
            case "System.Numerics.BitOperations.RotateLeft":
            case "System.Numerics.BitOperations.RotateRight":
            {
                fl.PushArgs(n);
                bool l = Kinds.Of(m.Signature[0]) == VK.I64;
                if (l) e.Op(WOp.I64ExtendI32U);
                e.Op(name == "RotateLeft" ? (l ? WOp.I64Rotl : WOp.I32Rotl) : (l ? WOp.I64Rotr : WOp.I32Rotr));
                break;
            }
            default: return false;
        }
        fl.SetResult(n);
        return true;
    }

    /// <summary>The runtime's compile-time switches (Rt.No*, the *Break test hooks, the reports): IL2WASM_* at compile
    /// time, as the operation-tree compiler bakes them.</summary>
    static bool IsSwitch(string key, out int value)
    {
        value = 0;
        if (!key.StartsWith("Rt.")) return false;
        var name = key[3..];
        string Snake(string s) => string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : char.ToUpperInvariant(c).ToString()));
        string? Env(string s) => Environment.GetEnvironmentVariable("IL2WASM_" + s);
        if (name == "NoPlainParts")
        {
            value = Env("NO_PLAIN_PARTS") == "1" || Env("PLAIN_PARTS") != "1" && string.IsNullOrEmpty(Env("PLAIN_PARTS_BREAK")) ? 1 : 0;
            return true;
        }
        if (name is "NodePathBreak" or "PlainPartsBreak") { value = int.TryParse(Env(Snake(name)), out var v) ? v : 0; return true; }
        if (name.StartsWith("No") && name.Length > 2 && char.IsUpper(name[2]) || name is "SlotWalkBreak" or "DropStats" or "StaleHolds" or "ArenaReport")
        {
            value = Env(Snake(name)) == "1" ? 1 : 0;
            return true;
        }
        return false;
    }

    /// <summary>A generic definition named by a string literal operand ("System.IEquatable`1"), in CoreLib.</summary>
    internal MetadataType GenericDefinition(Node literal)
    {
        if (literal.Op != Opc.LdStr) throw new NotSupportedException("a generic definition named by a string that is not a literal");
        var name = (string)literal.X!;
        int dot = name.LastIndexOf('.');
        return _ctx.SystemModule.GetType(Encoding.UTF8.GetBytes(name[..dot]), Encoding.UTF8.GetBytes(name[(dot + 1)..]));
    }

    internal static int SizeOfAny(TypeDesc t) => Kinds.Of(t) == VK.Struct ? ((DefType)t).InstanceFieldSize.AsInt : Kinds.SizeOf(t);

    WasmFunction? _hasComponentSize;
    WasmFunction HasComponentSizeFn() => _hasComponentSize ??= Module.AddFunction("rt.has_component_size", new FuncType([ValType.I32], [ValType.I32]));

    // ------------------------------------------------------------------ type tables (TypeInfo.*)

    readonly Dictionary<string, WasmFunction> _typeTables = new();

    /// <summary>A table by type id, filled when the module's types are known.</summary>
    internal WasmFunction TypeTable(string kind) =>
        _typeTables.TryGetValue(kind, out var f) ? f : _typeTables[kind] = Module.AddFunction("rt.type_" + kind + "_by_id", new FuncType([ValType.I32], [ValType.I32]));

    internal bool TryTypeInfo(Emitter e, string name)
    {
        string? kind = name switch
        {
            "FullName" => "name", "Name" => "short_name", "Namespace" => "namespace", "Flags" => "flags", "Flags2" => "flags2",
            "BaseId" => "base", "ElementId" => "element", "Rank" => "rank", "CorElementType" => "cor", "Size" => "size",
            "BoxOffset" => "box_offset", "EnumInfo" => "enum_info", "Interfaces" => "interfaces", "DeclaringId" => "declaring",
            "Attributes" => "attrs", "GenericArgs" => "generic_args", "ArrayOf" => "array_of", "AssemblyOf" => "assembly",
            _ => null,
        };
        if (kind == null) return false;
        e.Call(TypeTable(kind));
        return true;
    }

    /// <summary>Every type the tables name has an id: bases, elements, interfaces, generic arguments, containing types.</summary>
    void CloseTypeIds()
    {
        if (_typeTables.Count == 0) return;
        for (int i = 0; i < _types.Count; i++)
        {
            var t = _types[i];
            if (_typeTables.ContainsKey("base") && t.BaseType is { } b) TypeId(b);
            if (_typeTables.ContainsKey("element") && t is ParameterizedType p) TypeId(p.ParameterType);
            if (_typeTables.ContainsKey("interfaces")) foreach (var it in t.RuntimeInterfaces) TypeId(it);
            if (_typeTables.ContainsKey("generic_args")) foreach (var a in t.Instantiation) TypeId(a);
            if (_typeTables.ContainsKey("declaring") && t is MetadataType { ContainingType: { } ct }) TypeId(ct);
        }
    }

    static string ShortName(TypeDesc t) => t switch
    {
        ArrayType a => ShortName(a.ElementType) + (a.IsSzArray ? "[]" : "[" + new string(',', a.Rank - 1) + "]"),
        PointerType p => ShortName(p.ParameterType) + "*",
        ByRefType r => ShortName(r.ParameterType) + "&",
        MetadataType m => m.GetName(),
        _ => t.ToString()!,
    };

    static string NamespaceOf(TypeDesc t)
    {
        while (t is ParameterizedType p) t = p.ParameterType;
        while (t is MetadataType { ContainingType: { } c }) t = c;
        return t is MetadataType m ? m.GetNamespace() : "";
    }

    static int CorElementType(TypeDesc t)
    {
        switch (t.Category)
        {
            case TypeFlags.SzArray: return 0x1D;
            case TypeFlags.Array: return 0x14;
            case TypeFlags.Pointer: return 0x0F;
            case TypeFlags.Boolean: return 0x02; case TypeFlags.Char: return 0x03; case TypeFlags.SByte: return 0x04; case TypeFlags.Byte: return 0x05;
            case TypeFlags.Int16: return 0x06; case TypeFlags.UInt16: return 0x07; case TypeFlags.Int32: return 0x08; case TypeFlags.UInt32: return 0x09;
            case TypeFlags.Int64: return 0x0A; case TypeFlags.UInt64: return 0x0B; case TypeFlags.Single: return 0x0C; case TypeFlags.Double: return 0x0D;
            case TypeFlags.IntPtr: return 0x18; case TypeFlags.UIntPtr: return 0x19; case TypeFlags.Void: return 0x01;
        }
        if (t.IsString) return 0x0E;
        if (t.IsObject) return 0x1C;
        if (t.HasInstantiation) return 0x15;
        return t.IsValueType ? 0x11 : 0x12;
    }

    static int Flags(TypeDesc t)
    {
        int f = 0;
        if (t.IsValueType) f |= 1;
        if (t.IsEnum) f |= 2;
        if (t.IsArray) f |= 4;
        if (t.IsInterface) f |= 8;
        if (t.HasInstantiation) f |= 16;
        if (t.IsPrimitive && !t.IsEnum) f |= 32;
        return f;
    }

    static int Flags2(TypeDesc t)
    {
        int f = 0;
        bool counted = t switch
        {
            ArrayType a => Kinds.Counted(a.ElementType),
            MetadataType { IsValueType: false } c => ((DefType)c).ContainsGCPointers,
            _ => t.IsValueType && Kinds.Counted(t),
        };
        if (counted) f |= 1;
        if (t.IsByRefLike) f |= 2;
        if (t.HasInstantiation) f |= 4;
        if (t is MetadataType { ContainingType: not null }) f |= 8;
        if (t.IsDelegate) f |= 16;
        if (t.IsPointer) f |= 32;
        if (t.IsGenericDefinition) f |= 64;
        return f;
    }

    static int Attributes(TypeDesc t)
    {
        if (t is ParameterizedType) return 0x1 | 0x100 | 0x2000;
        if (t is not EcmaType et) return t is MetadataType m2 && m2.GetTypeDefinition() is EcmaType d2 ? (int)d2.Attributes : 0;
        return (int)et.Attributes;
    }

    /// <summary>The tables' bodies: per kind, a table in the data read by the final type id (an id out of range reads
    /// type 0's entry; a by-ref type, Meta.ByRef, its element's).</summary>
    void EmitTypeTables(Func<TypeDesc, int> final, int count, TypeDesc?[] byFinal)
    {
        var lists = new Dictionary<string, Blob>();
        Blob Ids(IEnumerable<TypeDesc> types)
        {
            var ids = types.Where(x => _typeIds.ContainsKey(x)).Select(final).ToList();
            var key = string.Join(",", ids);
            if (!lists.TryGetValue(key, out var a))
                lists[key] = a = DataBlob([.. BitConverter.GetBytes(ids.Count), .. ids.SelectMany(BitConverter.GetBytes)], 4);
            return a;
        }
        int IdOr(TypeDesc? t) => t != null && _typeIds.ContainsKey(t) ? final(t) : -1;
        foreach (var (kind, fn) in _typeTables)
        {
            Func<TypeDesc, (IAddress?, int)> entry = kind switch
            {
                "name" => t => (StringLiteral(TypeName(t)), 0),
                "short_name" => t => (StringLiteral(ShortName(t)), 0),
                "namespace" => t => (StringLiteral(NamespaceOf(t)), 0),
                "flags" => t => (null, Flags(t)),
                "flags2" => t => (null, Flags2(t)),
                "base" => t => (null, IdOr(t.BaseType)),
                "element" => t => (null, IdOr((t as ParameterizedType)?.ParameterType)),
                "rank" => t => (null, t is ArrayType a ? a.Rank : 0),
                "cor" => t => (null, CorElementType(t)),
                "size" => t => (null, t is DefType dt && !t.IsInterface ? (t.IsValueType ? SizeOfAny(t) : IlContext.InstanceSize(dt)) : 0),
                "box_offset" => t => (null, t.IsValueType ? IlContext.ObjectHeader : 0),
                "interfaces" => t => (Ids(t.RuntimeInterfaces), 0),
                "generic_args" => t => (Ids(Enumerable.Range(0, t.Instantiation.Length).Select(i => t.Instantiation[i])), 0),
                "declaring" => t => (null, IdOr((t as MetadataType)?.ContainingType)),
                "attrs" => t => (null, Attributes(t)),
                "array_of" => t => (null, IdOr(_typeIds.Keys.FirstOrDefault(x => x is ArrayType { IsSzArray: true } a && a.ElementType == t))),
                "assembly" => t => (null, 0),
                "enum_info" => t => (t.IsEnum ? EnumInfo(t) : null, 0),
                _ => throw new NotSupportedException("type table " + kind),
            };
            var vals = new (IAddress? a, int v)[count];
            // (an open generic type, typeof(List<>), has no layout)
            for (int i = 0; i < count; i++) vals[i] = byFinal[i] is { IsGenericDefinition: true } && kind is "size" or "flags2" or "box_offset" ? (null, kind == "flags2" ? 64 | 4 : 0)
                : byFinal[i] is { } t ? entry(t) : (kind is "name" or "short_name" or "namespace" ? (StringLiteral(""), 0) : kind is "interfaces" or "generic_args" ? (Ids([]), 0) : (null, kind is "base" or "element" or "declaring" or "array_of" ? -1 : 0));
            var blob = new byte[count * 4];
            var relocs = new List<(int, IAddress)>();
            for (int i = 0; i < count; i++)
            {
                if (vals[i].a != null) relocs.Add((i * 4, vals[i].a!));
                else BitConverter.GetBytes(vals[i].v).CopyTo(blob, i * 4);
            }
            var table = DataBlob(blob, 4, relocs);
            var e = new Emitter(fn);
            int id = e.AddLocal(ValType.I32);
            e.Get(0); e.Set(id);
            // (a by-ref type: its element's entry, except what differs for a by-ref)
            e.Get(0); e.I32(0x40000000); e.Op(WOp.I32And);
            e.If();
            switch (kind)
            {
                case "cor": e.I32(0x10); e.Op(WOp.Return); break;
                case "element": e.Get(0); e.I32(0x3FFFFFFF); e.Op(WOp.I32And); e.Op(WOp.Return); break;
                case "flags2" or "attrs" or "rank" or "size" or "box_offset" or "flags": e.I32(0); e.Op(WOp.Return); break;
                case "base" or "declaring" or "array_of": e.I32(-1); e.Op(WOp.Return); break;
                default: e.Get(0); e.I32(0x3FFFFFFF); e.Op(WOp.I32And); e.Set(id); break;
            }
            e.End();
            e.Get(id); e.I32(count); e.Op(WOp.I32GeU); e.If(); e.I32(0); e.Set(id); e.End();
            e.Get(id); e.I32(2); e.Op(WOp.I32Shl); e.I32Addr(table); e.Op(WOp.I32Add); e.Load(WOp.I32Load);
        }
    }

    readonly Dictionary<TypeDesc, Blob> _enumInfo = new();

    /// <summary>An enum's table: [count][flags][size][unsigned] then 16-byte entries (value as i64, name).</summary>
    Blob EnumInfo(TypeDesc et)
    {
        if (_enumInfo.TryGetValue(et, out var b)) return b;
        var def = (EcmaType)et.GetTypeDefinition();
        bool unsigned = !Kinds.IsSigned(et.UnderlyingType);
        var dedup = new List<(long value, string name)>();
        foreach (var f in def.GetFields())
        {
            if (!f.IsLiteral) continue;
            var ef = (EcmaField)f;
            var reader = ef.MetadataReader;
            var c = reader.GetConstant(reader.GetFieldDefinition(ef.Handle).GetDefaultValue());
            var br = reader.GetBlobReader(c.Value);
            long v = c.TypeCode switch
            {
                System.Reflection.Metadata.ConstantTypeCode.SByte => br.ReadSByte(), System.Reflection.Metadata.ConstantTypeCode.Byte => br.ReadByte(),
                System.Reflection.Metadata.ConstantTypeCode.Int16 => br.ReadInt16(), System.Reflection.Metadata.ConstantTypeCode.UInt16 => br.ReadUInt16(),
                System.Reflection.Metadata.ConstantTypeCode.Int32 => br.ReadInt32(), System.Reflection.Metadata.ConstantTypeCode.UInt32 => br.ReadUInt32(),
                System.Reflection.Metadata.ConstantTypeCode.Int64 => br.ReadInt64(), System.Reflection.Metadata.ConstantTypeCode.UInt64 => (long)br.ReadUInt64(),
                System.Reflection.Metadata.ConstantTypeCode.Char => br.ReadChar(), System.Reflection.Metadata.ConstantTypeCode.Boolean => br.ReadBoolean() ? 1 : 0,
                _ => 0,
            };
            if (!dedup.Any(x => x.value == v)) dedup.Add((v, f.GetName()));
        }
        dedup.Sort((a, c) => ((ulong)a.value).CompareTo((ulong)c.value));
        bool flags = def.HasCustomAttribute("System", "FlagsAttribute");
        var blob = new List<byte>();
        var relocs = new List<(int, IAddress)>();
        void I32b(int v) => blob.AddRange(BitConverter.GetBytes(v));
        I32b(dedup.Count); I32b(flags ? 1 : 0); I32b(Kinds.SizeOf(et.UnderlyingType)); I32b(unsigned ? 1 : 0);
        foreach (var (value, name) in dedup)
        {
            blob.AddRange(BitConverter.GetBytes(value));
            relocs.Add((blob.Count, StringLiteral(name))); I32b(0); I32b(0);
        }
        b = DataBlob([.. blob], 8, relocs);
        _enumInfo[et] = b;
        return b;
    }

    // ------------------------------------------------------------------ arrays (Il2WasmArrays.*)

    readonly Dictionary<string, WasmFunction> _arrayFns = new();

    /// <summary>A function over an array object, by its type: rank, elem_size, counted, copy, clear.</summary>
    internal WasmFunction ArrayFn(string kind) => _arrayFns.TryGetValue(kind, out var f) ? f : _arrayFns[kind] = Module.AddFunction("rt.array_" + kind, kind switch
    {
        "copy" => new FuncType([ValType.I32, ValType.I32, ValType.I32, ValType.I32, ValType.I32, ValType.I32], []),
        "clear" => new FuncType([ValType.I32, ValType.I32, ValType.I32, ValType.I32], []),
        _ => new FuncType([ValType.I32], [ValType.I32]),
    });

    void EmitArrayFns(Func<TypeDesc, int> final)
    {
        var arrays = _constructedOrder.OfType<ArrayType>().ToList();
        foreach (var (kind, fn) in _arrayFns)
        {
            var e = new Emitter(fn);
            int tid = e.AddLocal(ValType.I32);
            // (copy: the destination's type, its third argument)
            e.Get(kind == "copy" ? 2 : 0); e.Load(WOp.I32Load, 4); e.Set(tid);
            foreach (var a in arrays)
            {
                var et = a.ElementType;
                int es = SizeOfAny(et);
                e.Get(tid); e.I32(final(a)); e.Op(WOp.I32Eq); e.If();
                switch (kind)
                {
                    case "rank": e.I32(a.Rank); e.Op(WOp.Return); break;
                    case "elem_size": e.I32(es); e.Op(WOp.Return); break;
                    case "counted": e.I32(Kinds.Counted(et) ? 1 : 0); e.Op(WOp.Return); break;
                    case "copy":
                        // (src, si, dst, di, length, size): the source's elements counted, the destination's old ones
                        // released, then the bytes moved (overlapping ranges are fine)
                        if (Kinds.Counted(et))
                        {
                            Elem(e, 0, 1, es, ArrayDataOffset(a)); e.Get(4); e.Call(Rt.Range(et, true));
                            Elem(e, 2, 3, es, ArrayDataOffset(a)); e.Get(4); e.Call(Rt.Range(et, false));
                        }
                        Elem(e, 2, 3, es, ArrayDataOffset(a)); Elem(e, 0, 1, es, ArrayDataOffset(a)); e.Get(4); e.I32(es); e.Op(WOp.I32Mul); e.Emit(WOp.MemoryCopy);
                        e.Op(WOp.Return);
                        break;
                    case "clear":
                        if (Kinds.Counted(et)) { Elem(e, 0, 1, es, ArrayDataOffset(a)); e.Get(2); e.Call(Rt.Range(et, false)); }
                        Elem(e, 0, 1, es, ArrayDataOffset(a)); e.I32(0); e.Get(2); e.I32(es); e.Op(WOp.I32Mul); e.Emit(WOp.MemoryFill);
                        e.Op(WOp.Return);
                        break;
                }
                e.End();
            }
            if (kind is "copy" or "clear") e.Op(WOp.Unreachable); else e.I32(0);
        }
        if (_hasComponentSize != null)
        {
            var e = new Emitter(_hasComponentSize);
            var ids = arrays.Select(final).Append(1).ToList();
            int t = e.AddLocal(ValType.I32);
            e.Get(0); e.Set(t);
            EmitIdTest(e, t, ids);
        }
    }

    /// <summary>The address of element (local i) of the array in local a.</summary>
    static void Elem(Emitter e, int a, int i, int es, int data = 16) { e.Get(a); e.I32(data); e.Op(WOp.I32Add); e.Get(i); e.I32(es); e.Op(WOp.I32Mul); e.Op(WOp.I32Add); }

    // ------------------------------------------------------------------ delegates

    readonly Dictionary<MethodDesc, WasmFunction> _invokes = new(), _thunks = new();

    internal static FieldDesc DelegateFieldOf(TypeDesc d, string name) => DelegateField(d, name);

    static FieldDesc DelegateField(TypeDesc d, string name)
    {
        for (var t = d; t != null; t = t.BaseType)
            foreach (var f in t.GetFields()) if (!f.IsStatic && f.GetName() == name) return f;
        throw new InvalidOperationException($"the delegate type {d} has no field {name}");
    }

    /// <summary>A delegate type's Invoke: each function of its invocation list in order (the last one's result), or its
    /// one function, called with the target first.</summary>
    internal WasmFunction DelegateInvoke(MethodDesc invoke)
    {
        if (_invokes.TryGetValue(invoke, out var f)) return f;
        var sig = Sig(invoke);
        f = Module.AddFunction("delegate invoke " + invoke, sig);
        _invokes[invoke] = f;
        var d = invoke.OwningType;
        int target = IlContext.FieldOffset(DelegateField(d, "_target")), fn = IlContext.FieldOffset(DelegateField(d, "_fn")), list = IlContext.FieldOffset(DelegateField(d, "_list"));
        var e = new Emitter(f);
        int l = e.AddLocal(ValType.I32), i = e.AddLocal(ValType.I32), n = e.AddLocal(ValType.I32);
        e.Get(0); e.Call(Rt.NonNull); e.Load(WOp.I32Load, list); e.Set(l);
        e.Get(l);
        e.If();
        {
            // the list: all but the last for their effects, the last for the result
            e.Get(l); e.Load(WOp.I32Load, 12); e.I32(1); e.Op(WOp.I32Sub); e.Set(n);
            var brk = e.Block(); var loop = e.Loop();
            e.Get(i); e.Get(n); e.Op(WOp.I32GeU); e.BrIf(brk);
            e.Get(l); e.Get(i); e.I32(2); e.Op(WOp.I32Shl); e.Op(WOp.I32Add); e.Load(WOp.I32Load, 16);
            for (int p = 1; p < sig.Params.Length; p++) e.Get(p);
            e.Call(f);
            foreach (var _ in sig.Results) e.Op(WOp.Drop);
            e.Get(i); e.I32(1); e.Op(WOp.I32Add); e.Set(i); e.Br(loop);
            e.End(); e.End();
            e.Get(l); e.Get(n); e.I32(2); e.Op(WOp.I32Shl); e.Op(WOp.I32Add); e.Load(WOp.I32Load, 16);
            for (int p = 1; p < sig.Params.Length; p++) e.Get(p);
            e.Call(f);
            e.Op(WOp.Return);
        }
        e.End();
        e.Get(0); e.Load(WOp.I32Load, target);
        for (int p = 1; p < sig.Params.Length; p++) e.Get(p);
        e.Get(0); e.Load(WOp.I32Load, fn);
        e.Emit(WOp.CallIndirect, 0, 0, sig);
        return f;
    }

    /// <summary>The function a delegate of this method holds (called with the target first, as Invoke calls it): the
    /// method itself for an instance method of a class; a thunk that drops the target for a static method, or that
    /// passes a boxed struct's data.</summary>
    internal WasmFunction DelegateTargetFn(MethodDesc m, bool virtualCall, TypeDesc? delegateType = null)
    {
        if (!m.Signature.IsStatic && !m.OwningType.IsValueType) return virtualCall && m.IsVirtual && !m.IsFinal ? DispatchFor(m) : FunctionFor(m);
        // (a static method closed over its first argument, an extension method's delegate: the target is that argument)
        if (m.Signature.IsStatic && delegateType != null && delegateType.GetMethods().FirstOrDefault(x => x.GetName() == "Invoke") is { } inv
            && m.Signature.Length == inv.Signature.Length + 1)
            return FunctionFor(m);
        if (_thunks.TryGetValue(m, out var f)) return f;
        var inner = Sig(m);
        var ps = new List<ValType>();
        if (m.Signature.IsStatic) ps.Add(ValType.I32);
        ps.AddRange(inner.Params);
        f = Module.AddFunction("delegate thunk " + m, new FuncType(ps.ToArray(), inner.Results));
        _thunks[m] = f;
        var e = new Emitter(f);
        for (int p = m.Signature.IsStatic ? 1 : 0; p < ps.Count; p++)
        {
            e.Get(p);
            if (p == 0 && !m.Signature.IsStatic) { e.I32(IlContext.ObjectHeader); e.Op(WOp.I32Add); }
        }
        e.Call(FunctionFor(m));
        return f;
    }

    WasmFunction? _delegateNullThis;
    /// <summary>Throws what the CLR's delegate constructor throws for an instance method with a null target.</summary>
    internal WasmFunction DelegateNullThis()
    {
        if (_delegateNullThis != null) return _delegateNullThis;
        var f = _delegateNullThis = Module.AddFunction("rt.delegate_null_this", new FuncType([], []));
        var at = (DefType)_ctx.SystemModule.GetType("System"u8, "ArgumentException"u8);
        var ctor = at.GetMethods().First(x => x.IsConstructor && x.Signature.Length == 1 && x.Signature[0].IsString);
        Constructed(at);
        var e = new Emitter(f);
        int o = e.AddLocal(ValType.I32);
        e.I32(IlContext.InstanceSize(at)); e.Tid(TypeId(at)); e.Call(Rt.AllocObject); e.Set(o);
        e.Get(o); e.I32Addr(StringLiteral("Delegate to an instance method cannot have null 'this'.")); e.Call(FunctionFor(ctor));
        e.Get(o); e.Emit(WOp.Throw, 0, 0, ExceptionTag);
        return f;
    }

    internal static bool IsDelegateInvoke(MethodDesc m) => m.OwningType.IsDelegate && m.GetName() == "Invoke" && !m.Signature.IsStatic;
}
