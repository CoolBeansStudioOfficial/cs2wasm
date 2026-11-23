#nullable enable
using System;
using Internal.TypeSystem;

namespace Il2Wasm.IL.Ir;

/// <summary>How the IR holds a value of each type, and its evaluation stack type.</summary>
public static class Kinds
{
    public static VK Of(TypeDesc t)
    {
        switch (t.Category)
        {
            case TypeFlags.Void: return VK.Void;
            case TypeFlags.Boolean: case TypeFlags.Char: case TypeFlags.SByte: case TypeFlags.Byte:
            case TypeFlags.Int16: case TypeFlags.UInt16: case TypeFlags.Int32: case TypeFlags.UInt32:
                return VK.I32;
            case TypeFlags.Int64: case TypeFlags.UInt64: return VK.I64;
            case TypeFlags.IntPtr: case TypeFlags.UIntPtr: case TypeFlags.Pointer: case TypeFlags.FunctionPointer: case TypeFlags.ByRef:
                return VK.Ptr;
            case TypeFlags.Single: return VK.F32;
            case TypeFlags.Double: return VK.F64;
            case TypeFlags.Enum: return Of(t.UnderlyingType);
            case TypeFlags.Class: case TypeFlags.Interface: case TypeFlags.Array: case TypeFlags.SzArray: return VK.Ref;
            case TypeFlags.ValueType: case TypeFlags.Nullable:
                throw new NotSupportedException($"value type {t} (structs are not imported yet)");
            default: throw new NotSupportedException($"type {t} ({t.Category})");
        }
    }

    public static StackType StackOf(TypeDesc t) => t.Category switch
    {
        TypeFlags.IntPtr or TypeFlags.UIntPtr or TypeFlags.Pointer or TypeFlags.FunctionPointer => StackType.NativeInt,
        TypeFlags.ByRef => StackType.ByRef,
        TypeFlags.Enum => StackOf(t.UnderlyingType),
        _ => Of(t) switch
        {
            VK.I32 => StackType.Int32, VK.I64 => StackType.Int64, VK.F32 or VK.F64 => StackType.Float, VK.Ref => StackType.ObjRef,
            _ => StackType.None,
        },
    };

    /// <summary>The bytes a value of the type takes in memory (a field, an element): small integers their own size.</summary>
    public static int SizeOf(TypeDesc t) => t.Category switch
    {
        TypeFlags.Boolean or TypeFlags.SByte or TypeFlags.Byte => 1,
        TypeFlags.Char or TypeFlags.Int16 or TypeFlags.UInt16 => 2,
        TypeFlags.Int64 or TypeFlags.UInt64 or TypeFlags.Double => 8,
        TypeFlags.Enum => SizeOf(t.UnderlyingType),
        TypeFlags.ValueType or TypeFlags.Nullable => ((DefType)t).InstanceFieldSize.AsInt,
        _ => 4,
    };

    public static bool IsSigned(TypeDesc t) => t.Category switch
    {
        TypeFlags.SByte or TypeFlags.Int16 or TypeFlags.Int32 or TypeFlags.Int64 or TypeFlags.IntPtr => true,
        TypeFlags.Enum => IsSigned(t.UnderlyingType),
        _ => false,
    };
}
