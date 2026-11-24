#nullable enable
using System.Collections.Generic;
using System.Text;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Ir;

/// <summary>How a value is held: WASM's number types, a counted object reference, an uncounted address (a byref,
/// a pointer, a native int: i32 on wasm32), or a struct (a copy of its own in the frame, named by its address).</summary>
public enum VK : byte { Void, I32, I64, F32, F64, Ref, Ptr, Struct }

/// <summary>ECMA-335's evaluation stack types (III.1.5), for the typed stack validation.</summary>
public enum StackType : byte { None, Int32, Int64, NativeInt, Float, ObjRef, ByRef, ValueType }

public enum Opc : byte
{
    // values
    Const, Arg, Phi, Bin, Un, Cmp, Conv, LdNull, LdStr, LdToken, Call, NewObj, NewArr, LdLen, LdElem, StElem, LdElemA,
    LdFld, StFld, LdFldA, LdSFld, StSFld, LdSFldA, LdInd, StInd, CastClass, IsInst, Box, UnboxAny, VarLd, VarSt,
    CatchVal, Intrinsic, CheckFinite, Retain, Release, LocA, LdObj, InitObj, Unbox, SizeOf, LocAlloc, TypeOf, InitArray, CreateSpan, LdFtn, LdVirtFtn, CallI, InitBlk, CpBlk,
    // terminators
    Br, BrIf, Switch, Ret, Throw, Rethrow, Leave, EndFinally, EndFilter, Unreachable,
}

public enum BinOp : byte { Add, Sub, Mul, Div, DivUn, Rem, RemUn, And, Or, Xor, Shl, Shr, ShrUn }
public enum CmpOp : byte { Eq, Ne, Gt, GtUn, Ge, GeUn, Lt, LtUn, Le, LeUn }
public enum UnOp : byte { Neg, Not }

/// <summary>How a call is dispatched.</summary>
public enum CallKind : byte { Direct, Virtual, Interface }

/// <summary>An IR operation and the SSA value it defines (if any). Operands are other nodes; terminators are the last
/// node of a block.</summary>
public sealed class Node
{
    public Opc Op;
    public VK Kind;
    public StackType SType;
    public TypeDesc? Type;
    public Node[] A = [];
    /// <summary>The operation's member or constant: a MethodDesc, FieldDesc, TypeDesc, string, or an intrinsic's name.</summary>
    public object? X;
    public long I;
    public double D;
    /// <summary>BinOp, CmpOp, UnOp, CallKind, or a conversion's target (VK) by operation.</summary>
    public byte Sub;
    /// <summary>Overflow-checked (add.ovf, conv.ovf); for conversions and comparisons of unsigned operands too.</summary>
    public bool Ovf, Unsigned;
    /// <summary>A conversion's or an element's small type (1, 2 bytes; signedness in Unsigned), 0 when none.</summary>
    public byte Small;
    public Block Block = null!;
    public int Id;
    public int ILOffset = -1;
    /// <summary>Ownership (naive counting, Ownership.cs): operand i's count moves into this operation (bit i); the
    /// value is dead after it.</summary>
    public uint Moves;
    /// <summary>Phi: the incoming values, one per predecessor of the block (in Block.Preds' order).</summary>
    public bool IsPhi => Op == Opc.Phi;
    public bool IsTerminator => Op >= Opc.Br;
    public bool HasValue => Kind != VK.Void;
    public override string ToString() => "v" + Id;
}

/// <summary>An IL local or argument that is not an SSA value: in a WASM local, or in the method's frame on the shadow
/// stack when its address is taken.</summary>
public sealed class Var
{
    public VK Kind;
    public TypeDesc Type = null!;
    public bool InFrame;
    /// <summary>A struct argument: its storage is the caller's copy, whose address is this parameter (-1: none).</summary>
    public int ArgIndex = -1;
    /// <summary>Its offset in the frame (InFrame).</summary>
    public int Offset;
}

public enum RegionKind : byte { Root, Try, Catch, Filter, Finally, Fault }

/// <summary>An exception region: a try body or one of its handlers (a filter's test is a region of its own, before its
/// handler). Blocks belong to their innermost region.</summary>
public sealed class Region
{
    public RegionKind Kind;
    public Region? Parent;
    public Block Entry = null!;
    /// <summary>A try's handlers, in clause order.</summary>
    public List<Region> Handlers = [];
    /// <summary>A handler's try.</summary>
    public Region? Try;
    /// <summary>A catch's exception type (null: catch everything); a filter handler's filter region.</summary>
    public TypeDesc? CatchType;
    public Region? Filter;
    public int Start, End;
    public readonly List<Region> Children = [];
    public readonly List<Block> Blocks = [];
    public bool IsHandler => Kind is RegionKind.Catch or RegionKind.Filter or RegionKind.Finally or RegionKind.Fault;
    public bool Contains(Region r) { for (var x = r; x != null; x = x.Parent) if (x == this) return true; return false; }
    public override string ToString() => $"{Kind}@{Start:x}";
}

public sealed class Block
{
    public int Id;
    public int ILStart;
    public Region Region = null!;
    public readonly List<Node> Phis = [];
    public readonly List<Node> Code = [];
    public readonly List<Block> Succs = [];
    public readonly List<Block> Preds = [];
    public Node? Term => Code.Count > 0 && Code[^1].IsTerminator ? Code[^1] : null;
    public override string ToString() => "b" + Id;
}

/// <summary>A method's body in the IR: SSA values in blocks, with explicit exception regions.</summary>
public sealed class Body
{
    public MethodDesc Method = null!;
    public readonly List<Block> Blocks = [];
    public Region Root = null!;
    public readonly List<Region> Regions = [];
    public Node[] Args = [];
    /// <summary>The IL locals and arguments that are not SSA values (written in a protected region or a finally, or
    /// whose address is taken): variables of their own, read and written by VarLd and VarSt.</summary>
    public readonly List<Var> Vars = [];
    /// <summary>The method allocates on the shadow stack (localloc): it has a frame.</summary>
    public bool UsesLocAlloc;
    int _nextId;
    public int NextId() => _nextId++;
    public int ValueCount => _nextId;

    public string Dump()
    {
        var sb = new StringBuilder();
        foreach (var b in Blocks)
        {
            sb.Append($"{b} [{b.Region}] preds({string.Join(",", b.Preds)}) succs({string.Join(",", b.Succs)})\n");
            foreach (var n in b.Phis) sb.Append("  ").Append(Format(n)).Append('\n');
            foreach (var n in b.Code) sb.Append("  ").Append(Format(n)).Append('\n');
        }
        return sb.ToString();
    }

    public static string Format(Node n)
    {
        var sb = new StringBuilder();
        if (n.HasValue) sb.Append($"v{n.Id}:{n.Kind} = ");
        sb.Append(n.Op);
        switch (n.Op)
        {
            case Opc.Bin: sb.Append('.').Append((BinOp)n.Sub); break;
            case Opc.Cmp: sb.Append('.').Append((CmpOp)n.Sub); break;
            case Opc.Un: sb.Append('.').Append((UnOp)n.Sub); break;
            case Opc.Conv: sb.Append('.').Append((VK)n.Sub); break;
            case Opc.Call: sb.Append('.').Append((CallKind)n.Sub); break;
        }
        if (n.Ovf) sb.Append(".ovf");
        if (n.Unsigned) sb.Append(".un");
        if (n.Op == Opc.Const) sb.Append(' ').Append(n.Kind is VK.F32 or VK.F64 ? n.D.ToString(System.Globalization.CultureInfo.InvariantCulture) : n.I.ToString());
        if (n.Op is Opc.Arg or Opc.VarLd or Opc.VarSt) sb.Append(' ').Append(n.I);
        if (n.X != null) sb.Append(" <").Append(n.X).Append('>');
        foreach (var a in n.A) sb.Append(' ').Append(a);
        if (n.Moves != 0) sb.Append(" moves=").Append(n.Moves);
        return sb.ToString();
    }
}
