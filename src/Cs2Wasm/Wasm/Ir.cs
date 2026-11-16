namespace Cs2Wasm.Wasm;

internal enum ValType : byte
{
    I32 = 0x7F,
    I64 = 0x7E,
    F32 = 0x7D,
    F64 = 0x7C,
    ExnRef = 0x69,
}

/// <summary>Opcodes. Single-byte opcodes are their own value; 0xFCxx and 0xFExx are prefixed.</summary>
internal enum Op : ushort
{
    Unreachable = 0x00, Nop = 0x01, Block = 0x02, Loop = 0x03, If = 0x04, Else = 0x05,
    Throw = 0x08, ThrowRef = 0x0A, End = 0x0B, Br = 0x0C, BrIf = 0x0D, BrTable = 0x0E, Return = 0x0F,
    Call = 0x10, CallIndirect = 0x11, Drop = 0x1A, Select = 0x1B, TryTable = 0x1F,
    LocalGet = 0x20, LocalSet = 0x21, LocalTee = 0x22, GlobalGet = 0x23, GlobalSet = 0x24,
    I32Load = 0x28, I64Load = 0x29, F32Load = 0x2A, F64Load = 0x2B,
    I32Load8S = 0x2C, I32Load8U = 0x2D, I32Load16S = 0x2E, I32Load16U = 0x2F,
    I64Load8S = 0x30, I64Load8U = 0x31, I64Load16S = 0x32, I64Load16U = 0x33, I64Load32S = 0x34, I64Load32U = 0x35,
    I32Store = 0x36, I64Store = 0x37, F32Store = 0x38, F64Store = 0x39,
    I32Store8 = 0x3A, I32Store16 = 0x3B, I64Store8 = 0x3C, I64Store16 = 0x3D, I64Store32 = 0x3E,
    MemorySize = 0x3F, MemoryGrow = 0x40,
    I32Const = 0x41, I64Const = 0x42, F32Const = 0x43, F64Const = 0x44,
    I32Eqz = 0x45, I32Eq, I32Ne, I32LtS, I32LtU, I32GtS, I32GtU, I32LeS, I32LeU, I32GeS, I32GeU,
    I64Eqz = 0x50, I64Eq, I64Ne, I64LtS, I64LtU, I64GtS, I64GtU, I64LeS, I64LeU, I64GeS, I64GeU,
    F32Eq = 0x5B, F32Ne, F32Lt, F32Gt, F32Le, F32Ge,
    F64Eq = 0x61, F64Ne, F64Lt, F64Gt, F64Le, F64Ge,
    I32Clz = 0x67, I32Ctz, I32Popcnt, I32Add, I32Sub, I32Mul, I32DivS, I32DivU, I32RemS, I32RemU,
    I32And, I32Or, I32Xor, I32Shl, I32ShrS, I32ShrU, I32Rotl, I32Rotr,
    I64Clz = 0x79, I64Ctz, I64Popcnt, I64Add, I64Sub, I64Mul, I64DivS, I64DivU, I64RemS, I64RemU,
    I64And, I64Or, I64Xor, I64Shl, I64ShrS, I64ShrU, I64Rotl, I64Rotr,
    F32Abs = 0x8B, F32Neg, F32Ceil, F32Floor, F32Trunc, F32Nearest, F32Sqrt, F32Add, F32Sub, F32Mul, F32Div,
    F32Min, F32Max, F32Copysign,
    F64Abs = 0x99, F64Neg, F64Ceil, F64Floor, F64Trunc, F64Nearest, F64Sqrt, F64Add, F64Sub, F64Mul, F64Div,
    F64Min, F64Max, F64Copysign,
    I32WrapI64 = 0xA7, I32TruncF32S, I32TruncF32U, I32TruncF64S, I32TruncF64U,
    I64ExtendI32S = 0xAC, I64ExtendI32U, I64TruncF32S, I64TruncF32U, I64TruncF64S, I64TruncF64U,
    F32ConvertI32S = 0xB2, F32ConvertI32U, F32ConvertI64S, F32ConvertI64U, F32DemoteF64,
    F64ConvertI32S = 0xB7, F64ConvertI32U, F64ConvertI64S, F64ConvertI64U, F64PromoteF32,
    I32ReinterpretF32 = 0xBC, I64ReinterpretF64, F32ReinterpretI32, F64ReinterpretI64,
    I32Extend8S = 0xC0, I32Extend16S, I64Extend8S, I64Extend16S, I64Extend32S,

    I32TruncSatF32S = 0xFC00, I32TruncSatF32U, I32TruncSatF64S, I32TruncSatF64U,
    I64TruncSatF32S, I64TruncSatF32U, I64TruncSatF64S, I64TruncSatF64U,
    MemoryInit = 0xFC08, DataDrop = 0xFC09, MemoryCopy = 0xFC0A, MemoryFill = 0xFC0B,

    // threads proposal (0xFE prefix)
    MemoryAtomicNotify = 0xFE00, MemoryAtomicWait32 = 0xFE01, MemoryAtomicWait64 = 0xFE02, AtomicFence = 0xFE03,
    I32AtomicLoad = 0xFE10, I64AtomicLoad = 0xFE11, I32AtomicStore = 0xFE17, I64AtomicStore = 0xFE18,
    I32AtomicRmwAdd = 0xFE1E, I64AtomicRmwAdd = 0xFE1F, I32AtomicRmwSub = 0xFE25, I64AtomicRmwSub = 0xFE26,
    I32AtomicRmwXchg = 0xFE41, I64AtomicRmwXchg = 0xFE42, I32AtomicRmw8XchgU = 0xFE43, I32AtomicRmw16XchgU = 0xFE44,
    I32AtomicRmwCmpxchg = 0xFE48, I64AtomicRmwCmpxchg = 0xFE49, I32AtomicRmw8CmpxchgU = 0xFE4A, I32AtomicRmw16CmpxchgU = 0xFE4B,
}

internal sealed class FuncType : IEquatable<FuncType>
{
    public readonly ValType[] Params;
    public readonly ValType[] Results;
    public FuncType(ValType[] p, ValType[] r) { Params = p; Results = r; }
    public bool Equals(FuncType? o) => o is not null && Params.AsSpan().SequenceEqual(o.Params) && Results.AsSpan().SequenceEqual(o.Results);
    public override bool Equals(object? obj) => Equals(obj as FuncType);
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var p in Params) h.Add(p);
        h.Add(-1);
        foreach (var r in Results) h.Add(r);
        return h.ToHashCode();
    }
}

/// <summary>A value whose address is fixed only when the module is laid out (statics, literals, tables).</summary>
internal interface IAddress { int Address { get; } bool Immortal => false; }

/// <summary>An i32.const that is a type id: the compiler numbers the types the module needs only at the end
/// (Driver.AssignTypeIds), and the encoder writes that number.</summary>
internal sealed class TypeIdRef
{
    public readonly int Id;
    TypeIdRef(int id) { Id = id; }
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TypeIdRef> s_all = new();
    public static TypeIdRef Of(int id) => s_all.GetOrAdd(id, i => new TypeIdRef(i));
    public override string ToString() => "type#" + Id;
}

internal sealed class FixedAddress(int a) : IAddress { public int Address { get; } = a; }

internal readonly record struct SourcePos(int File, int Line);

internal struct Instr
{
    public Op Op;
    public long Imm;
    public long Imm2;
    public object? Ref;
    public int Pos; // index into the line table's position list, -1 = none
    /// <summary>CS2WASM_COUNT_RC: reference values the source pushes just before this instruction (the baseline a scheme
    /// without ownership analysis would count up and down).</summary>
    public ushort Probes;

    public override readonly string ToString() => $"{Op} {Imm} {Imm2} {Ref}";
}

internal sealed class CatchClause
{
    public byte Kind; // 0 catch, 1 catch_ref, 2 catch_all, 3 catch_all_ref
    public WasmTag? Tag;
    public int Label;
}

internal sealed class TryTableInfo
{
    public object? BlockType;
    public List<CatchClause> Catches = [];
}

internal sealed class WasmFunction
{
    public string Name;
    public FuncType Type;
    public List<ValType> Locals = [];
    public List<Instr> Body = [];
    public string? ImportModule, ImportName;
    public List<string> Exports = [];
    public int Index = -1;
    public int Order; // creation order, for reproducible tie-breaking
    public bool InTable; public int TableIndex = -1;
    /// <summary>In the table because data refers to it (reflection thunks, attribute factories): always live.</summary>
    public bool TableRoot;
    public bool IsImport => ImportName != null;
    /// <summary>If set, every call to this function is redirected to the target (pure forwarders).</summary>
    public WasmFunction? ForwardTo;
    /// <summary>The frame as .NET's StackTrace prints it; null for compiler-internal functions (hidden from traces).</summary>
    public string? FrameName;
    /// <summary>Compiled from the program's own code (named in the name section; the library's frames are named in cs2wasm.frames).</summary>
    public bool ProgramCode;
    /// <summary>The variables in the function's frame on the shadow stack (offset from the frame's base, size), for
    /// FrameSlots.cs; null: no frame, or one that pass must leave alone.</summary>
    public List<(int off, int size)>? FrameSlots;
    public bool NoFramePromotion;
    public WasmFunction(string name, FuncType type) { Name = name; Type = type; }
    public override string ToString() => Name;
}

internal sealed class WasmGlobal
{
    public string Name; public ValType Type; public bool Mutable; public long Init; public int Index = -1;
    public string? Export;
    public WasmGlobal(string name, ValType t, bool mutable, long init) { Name = name; Type = t; Mutable = mutable; Init = init; }
}

internal sealed class WasmTag
{
    public FuncType Type; public int Index = -1; public string Name;
    public WasmTag(string name, FuncType t) { Name = name; Type = t; }
}

internal sealed class DataSegment
{
    public List<byte> Bytes = [];
    public int Index;
}

internal sealed class CustomSection(string name, byte[] data)
{
    public string Name { get; } = name;
    public byte[] Data { get; } = data;
}
