#nullable enable
using System;
using System.Collections.Generic;
using Il2Wasm.IL.Ir;
using Il2Wasm.Wasm;
using Internal.TypeSystem;
using WOp = Il2Wasm.Wasm.Op;

namespace Il2Wasm.IL.Lower;

/// <summary>Multi-dimensional arrays (T[,], T[*]): the layout the runtime's Array reads (Array.Mono.cs GetLengthOf,
/// Il2WasmArrays.Data): the header, the total length at 12, each dimension's length at 16 + 4 d, the elements from
/// (16 + 4 rank) rounded up to 8 (a rank-1 array: from 16). Lower bounds are always 0 (GetLowerBound answers 0), as
/// every C# array is; the constructor with lower bounds throws for any other. The array's Get, Set and Address methods
/// and its constructors are generated here (they have no IL: ECMA-335 II.14.2).</summary>
public sealed partial class IlCompiler
{
    /// <summary>The offset of element 0 in an array of the type.</summary>
    internal static int ArrayDataOffset(ArrayType a) => a.IsSzArray || a.Rank == 1 ? 16 : (16 + 4 * a.Rank + 7) / 8 * 8;

    readonly Dictionary<(ArrayType, int), WasmFunction> _mdCtors = new();
    readonly Dictionary<MethodDesc, WasmFunction> _mdMethods = new();

    /// <summary>newobj T[,]::.ctor(lengths) (n = rank) or .ctor(lower bound, length, ...) (n = 2 rank): the array.</summary>
    internal WasmFunction MdArrayCtor(ArrayType at, int nArgs)
    {
        if (_mdCtors.TryGetValue((at, nArgs), out var f)) return f;
        Constructed(at);
        int rank = at.Rank;
        bool bounds = nArgs == 2 * rank;
        if (!bounds && nArgs != rank) throw new NotSupportedException($"a constructor of {at} with {nArgs} arguments");
        var ps = new ValType[nArgs];
        Array.Fill(ps, ValType.I32);
        f = Module.AddFunction("rt.md_new " + at + (bounds ? " bounds" : ""), new FuncType(ps, [ValType.I32]));
        _mdCtors[(at, nArgs)] = f;
        var e = new Emitter(f);
        int es = SizeOfAny(at.ElementType);
        int total = e.AddLocal(ValType.I64), arr = e.AddLocal(ValType.I32);
        int Len(int d) => bounds ? 2 * d + 1 : d;
        // (each length checked as .NET does: a negative one is an OverflowException; a nonzero lower bound is not
        // supported by this runtime's arrays)
        e.I64(1); e.Set(total);
        for (int d = 0; d < rank; d++)
        {
            if (bounds) { e.Get(2 * d); e.If(); e.Call(FunctionFor(CoreMethod("Il2Wasm.Runtime", "ThrowHelpers", "PlatformNotSupported"))); e.End(); }
            e.Get(Len(d)); e.I32(0); e.Op(WOp.I32LtS); e.If(); e.Call(FunctionFor(CoreMethod("Il2Wasm.Runtime", "ThrowHelpers", "Overflow"))); e.End();
            e.Get(total); e.Get(Len(d)); e.Op(WOp.I64ExtendI32U); e.Op(WOp.I64Mul); e.Set(total);
            // (past the largest array: out of memory, before the product can wrap)
            e.Get(total); e.I64(0x7FFFFFFFL); e.Op(WOp.I64GtU); e.If(); e.Call(FunctionFor(CoreMethod("Il2Wasm.Runtime", "ThrowHelpers", "OutOfMemory"))); e.End();
        }
        e.Get(total); e.Op(WOp.I32WrapI64); e.I32(es); e.Tid(TypeId(at)); e.I32(ArrayDataOffset(at));
        e.Call(FunctionFor(CoreMethod("Il2Wasm.Runtime", "Rt", "AllocArrayWithHeader")));
        e.Set(arr);
        if (rank > 1) for (int d = 0; d < rank; d++) { e.Get(arr); e.Get(Len(d)); e.Store(WOp.I32Store, 16 + 4 * d); }
        e.Get(arr);
        return f;
    }

    /// <summary>An array type's Get, Set or Address (this, indices[, value]): bounds-checked, row-major.</summary>
    WasmFunction MdArrayMethod(ArrayMethod am)
    {
        if (_mdMethods.TryGetValue(am, out var f)) return f;
        var at = (ArrayType)am.OwningType;
        if (am.Kind is not (ArrayMethodKind.Get or ArrayMethodKind.Set or ArrayMethodKind.Address))
            throw new NotSupportedException($"the array method {am} ({am.Kind})");
        f = Module.AddFunction("rt.md " + am.Kind + " " + at, Sig(am));
        _mdMethods[am] = f;
        _functions[am] = f;
        var e = new Emitter(f);
        int rank = at.Rank;
        var et = at.ElementType;
        int es = SizeOfAny(et);
        int lin = e.AddLocal(ValType.I32), addr = e.AddLocal(ValType.I32);
        e.Get(0); e.Call(Rt.NonNull); e.Op(WOp.Drop);
        e.I32(0); e.Set(lin);
        for (int d = 0; d < rank; d++)
        {
            // (one dimension's index, unsigned against its length: a negative one is out of range too)
            int len = rank == 1 ? 12 : 16 + 4 * d;
            e.Get(1 + d); e.Get(0); e.Load(WOp.I32Load, len); e.Op(WOp.I32GeU);
            e.If(); e.Call(FunctionFor(CoreMethod("Il2Wasm.Runtime", "ThrowHelpers", "IndexOutOfRange"))); e.End();
            if (d > 0) { e.Get(lin); e.Get(0); e.Load(WOp.I32Load, len); e.Op(WOp.I32Mul); e.Get(1 + d); e.Op(WOp.I32Add); e.Set(lin); }
            else { e.Get(1); e.Set(lin); }
        }
        e.Get(0); e.I32(ArrayDataOffset(at)); e.Op(WOp.I32Add); e.Get(lin); e.I32(es); e.Op(WOp.I32Mul); e.Op(WOp.I32Add); e.Set(addr);
        var k = Kinds.Of(et);
        switch (am.Kind)
        {
            case ArrayMethodKind.Address:
                // (a reference element's address is not given out of an array of another type: string[,] seen as object[,],
                // ArrayTypeMismatchException, as an address of a covariant element is not a store that can be checked)
                if (k == VK.Ref)
                {
                    e.Get(0); e.Load(WOp.I32Load, 4); e.Tid(TypeId(at)); e.Op(WOp.I32Ne);
                    e.If(); e.Call(FunctionFor(CoreMethod("System", "ThrowHelper", "ThrowArrayTypeMismatchException"))); e.End();
                }
                e.Get(addr);
                break;
            case ArrayMethodKind.Get:
                if (k == VK.Struct)
                {
                    // (into the caller's buffer, the last parameter, with counts of its own)
                    int buf = f.Type.Params.Length - 1;
                    e.Get(buf); e.Get(addr); e.I32(es); e.Emit(WOp.MemoryCopy);
                    if (Kinds.Counted(et)) { e.Get(buf); e.Call(Rt.RetainStruct(et)); }
                }
                else
                {
                    e.Get(addr); LoadOf(e, et);
                    if (k == VK.Ref) e.Call(Rt.Retain);
                }
                break;
            case ArrayMethodKind.Set:
            {
                int v = 1 + rank;
                if (k == VK.Struct)
                {
                    // (the value is the caller's copy: copied in with counts of the array's own; the old released)
                    if (Kinds.Counted(et)) { e.Get(addr); e.Call(Rt.ReleaseStruct(et)); }
                    e.Get(addr); e.Get(v); e.I32(es); e.Emit(WOp.MemoryCopy);
                    if (Kinds.Counted(et)) { e.Get(addr); e.Call(Rt.RetainStruct(et)); }
                }
                else if (k == VK.Ref)
                {
                    // (a reference into an array of another type, string[,] seen as object[,]: the covariance check first)
                    e.Get(0); e.Load(WOp.I32Load, 4); e.Tid(TypeId(at)); e.Op(WOp.I32Ne);
                    e.If(); e.Get(0); e.Get(v); e.Call(FunctionFor(CoreMethod("Il2Wasm.Runtime", "Rt", "StoreCheck"))); e.End();
                    int old = e.AddLocal(ValType.I32);
                    e.Get(addr); e.Load(WOp.I32Load); e.Set(old);
                    e.Get(addr); e.Get(v); e.Call(Rt.Retain); e.Store(WOp.I32Store);
                    e.Get(old); e.Call(Rt.Release);
                }
                else { e.Get(addr); e.Get(v); StoreOf(e, et); }
                break;
            }
        }
        return f;
    }

    internal static void LoadOf(Emitter e, TypeDesc t, int offset = 0)
    {
        switch (t.Category)
        {
            case TypeFlags.Boolean: case TypeFlags.Byte: e.Load(WOp.I32Load8U, offset); break;
            case TypeFlags.SByte: e.Load(WOp.I32Load8S, offset); break;
            case TypeFlags.Char: case TypeFlags.UInt16: e.Load(WOp.I32Load16U, offset); break;
            case TypeFlags.Int16: e.Load(WOp.I32Load16S, offset); break;
            case TypeFlags.Int64: case TypeFlags.UInt64: e.Load(WOp.I64Load, offset); break;
            case TypeFlags.Single: e.Load(WOp.F32Load, offset); break;
            case TypeFlags.Double: e.Load(WOp.F64Load, offset); break;
            case TypeFlags.Enum: LoadOf(e, t.UnderlyingType, offset); break;
            default: e.Load(WOp.I32Load, offset); break;
        }
    }

    internal static void StoreOf(Emitter e, TypeDesc t, int offset = 0)
    {
        switch (t.Category)
        {
            case TypeFlags.Boolean: case TypeFlags.Byte: case TypeFlags.SByte: e.Store(WOp.I32Store8, offset); break;
            case TypeFlags.Char: case TypeFlags.UInt16: case TypeFlags.Int16: e.Store(WOp.I32Store16, offset); break;
            case TypeFlags.Int64: case TypeFlags.UInt64: e.Store(WOp.I64Store, offset); break;
            case TypeFlags.Single: e.Store(WOp.F32Store, offset); break;
            case TypeFlags.Double: e.Store(WOp.F64Store, offset); break;
            case TypeFlags.Enum: StoreOf(e, t.UnderlyingType, offset); break;
            default: e.Store(WOp.I32Store, offset); break;
        }
    }
}
