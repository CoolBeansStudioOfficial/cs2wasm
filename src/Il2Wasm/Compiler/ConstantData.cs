using Il2Wasm.Wasm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Il2Wasm.Compiler;

/// <summary>
/// Arrays initialized with constants of a primitive type, as C# emits them (static data, not one store per
/// element): `ReadOnlySpan&lt;byte&gt; Table => new byte[] { ... }` is a span over the data segment, with no allocation;
/// `new int[] { 1, 2, 3 }` copies its elements from the data segment.
/// </summary>
sealed partial class FunctionCompiler
{
    /// <summary>The bytes of a constant initializer of primitive elements (little-endian), or null.</summary>
    byte[]? ConstantInitializerBytes(IArrayCreationOperation ac, out ITypeSymbol elementType)
    {
        var at = (IArrayTypeSymbol)TypeOf(ac);
        elementType = T.Canonical(at.ElementType);
        if (at.Rank != 1 || ac.Initializer == null || ac.Initializer.ElementValues.Length < 4) return null;
        var p = Types.PrimOf(elementType);
        if (p == Prim.None) return null;
        int size = Types.PrimSize(p);
        var bytes = new byte[ac.Initializer.ElementValues.Length * size];
        int i = 0;
        foreach (var v in ac.Initializer.ElementValues)
        {
            if (v.ConstantValue is not { HasValue: true, Value: { } c }) return null;
            ulong bits;
            try
            {
                bits = c switch
                {
                    float f => BitConverter.SingleToUInt32Bits(f),
                    double d => BitConverter.DoubleToUInt64Bits(d),
                    bool b => b ? 1UL : 0,
                    char ch => ch,
                    _ => unchecked((ulong)Convert.ToInt64(c is ulong u ? unchecked((long)u) : c)),
                };
            }
            catch (Exception) { return null; }
            for (int k = 0; k < size; k++) bytes[i * size + k] = (byte)(bits >> (8 * k));
            i++;
        }
        return bytes;
    }

    /// <summary>A collection expression of constants as a ReadOnlySpan (`ReadOnlySpan&lt;byte&gt; X => [1, 2, 3]`): a span
    /// over the data segment.</summary>
    Own? TryEmitConstantSpan(ICollectionExpressionOperation ce)
    {
        var to = TypeOf(ce);
        if (to is not INamedTypeSymbol { Arity: 1 } span || Types.FullName(span) != "System.ReadOnlySpan`1") return null;
        var et = T.Canonical(span.TypeArguments[0]);
        var p = Types.PrimOf(et);
        if (p == Prim.None || ce.Elements.Length < 4) return null;
        int size = Types.PrimSize(p);
        var bytes = new byte[ce.Elements.Length * size];
        for (int i = 0; i < ce.Elements.Length; i++)
        {
            if (ce.Elements[i].ConstantValue is not { HasValue: true, Value: { } c } || ConstantBits(c) is not ulong bits) return null;
            for (int k = 0; k < size; k++) bytes[i * size + k] = (byte)(bits >> (8 * k));
        }
        return EmitDataSpan(to, bytes, ce.Elements.Length, size);
    }

    static ulong? ConstantBits(object c)
    {
        try
        {
            return c switch
            {
                float f => BitConverter.SingleToUInt32Bits(f),
                double d => BitConverter.DoubleToUInt64Bits(d),
                bool b => b ? 1UL : 0,
                char ch => ch,
                ulong u => u,
                _ => unchecked((ulong)Convert.ToInt64(c)),
            };
        }
        catch (Exception) { return null; }
    }

    Own? EmitDataSpan(ITypeSymbol spanType, byte[] bytes, int length, int align)
    {
        var l = T.LayoutOf(spanType);
        var refSlot = l.Fields.FirstOrDefault(f => f.Symbol.Name == "_reference");
        var lenSlot = l.Fields.FirstOrDefault(f => f.Symbol.Name == "_length");
        if (refSlot == null || lenSlot == null) return null;
        var addr = D.DataBlob(bytes, Math.Max(1, align));
        int off = TempStruct(spanType);
        E.Get(_fb); E.I32Addr(addr); E.Store(Op.I32Store, off + refSlot.Offset);
        E.Get(_fb); E.I32(length); E.Store(Op.I32Store, off + lenSlot.Offset);
        E.Get(_fb); E.I32(off); E.Op(Op.I32Add);
        return Own.None;
    }

    /// <summary>`(ReadOnlySpan&lt;T&gt;)new T[] { constants }`: a span over the data segment (an immutable view).</summary>
    Own? TryEmitConstantSpan(IConversionOperation c)
    {
        var to = TypeOf(c);
        if (to is not INamedTypeSymbol { Arity: 1 } span || Types.FullName(span) != "System.ReadOnlySpan`1") return null;
        var operand = c.Operand;
        while (operand is IConversionOperation { Conversion.IsIdentity: true } id) operand = id.Operand;
        if (operand is not IArrayCreationOperation ac || ConstantInitializerBytes(ac, out var et) is not { } bytes) return null;
        if (!SymbolEqualityComparer.Default.Equals(T.Canonical(span.TypeArguments[0]), et)) return null;
        return EmitDataSpan(to, bytes, ac.Initializer!.ElementValues.Length, Types.PrimSize(Types.PrimOf(et)));
    }
}
