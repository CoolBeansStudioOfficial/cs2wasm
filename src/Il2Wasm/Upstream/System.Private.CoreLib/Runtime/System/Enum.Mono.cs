// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's internal calls are implemented over the compiler's type tables (Il2Wasm.Runtime.TypeInfo, by type id).

using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace System
{
    public partial class Enum
    {
                private static void GetEnumValuesAndNames(QCallTypeHandle enumType, out ulong[] values, out string[] names)
        {
            // the compiler's table of the enum: [count, flags, size, unsigned], then (value: i64, name) per member, by unsigned value
            int t = Il2Wasm.Runtime.TypeInfo.EnumInfo(Il2Wasm.Runtime.TypeInfo.Id(enumType));
            int n = Il2Wasm.Runtime.Mem.LoadI32(t);
            values = new ulong[n];
            names = new string[n];
            for (int i = 0; i < n; i++)
            {
                values[i] = (ulong)Il2Wasm.Runtime.Mem.LoadI64(t + 16 + i * 16);
                names[i] = Il2Wasm.Runtime.Rt.Borrow<string>(Il2Wasm.Runtime.Mem.LoadI32(t + 16 + i * 16 + 8));
            }
        }

                private static CorElementType InternalGetCorElementType(QCallTypeHandle enumType)
        {
            int t = Il2Wasm.Runtime.TypeInfo.EnumInfo(Il2Wasm.Runtime.TypeInfo.Id(enumType));
            int size = Il2Wasm.Runtime.Mem.LoadI32(t + 8);
            bool unsigned = Il2Wasm.Runtime.Mem.LoadI32(t + 12) != 0;
            return size switch
            {
                1 => unsigned ? CorElementType.ELEMENT_TYPE_U1 : CorElementType.ELEMENT_TYPE_I1,
                2 => unsigned ? CorElementType.ELEMENT_TYPE_U2 : CorElementType.ELEMENT_TYPE_I2,
                8 => unsigned ? CorElementType.ELEMENT_TYPE_U8 : CorElementType.ELEMENT_TYPE_I8,
                _ => unsigned ? CorElementType.ELEMENT_TYPE_U4 : CorElementType.ELEMENT_TYPE_I4,
            };
        }

                private static void InternalGetUnderlyingType(QCallTypeHandle enumType, ObjectHandleOnStack res)
        {
            Type u = InternalGetCorElementType(enumType) switch
            {
                CorElementType.ELEMENT_TYPE_U1 => typeof(byte), CorElementType.ELEMENT_TYPE_I1 => typeof(sbyte),
                CorElementType.ELEMENT_TYPE_U2 => typeof(ushort), CorElementType.ELEMENT_TYPE_I2 => typeof(short),
                CorElementType.ELEMENT_TYPE_U8 => typeof(ulong), CorElementType.ELEMENT_TYPE_I8 => typeof(long),
                CorElementType.ELEMENT_TYPE_U4 => typeof(uint), _ => typeof(int),
            };
            Il2Wasm.Runtime.TypeInfo.Set(res, u);
        }

        private static unsafe CorElementType InternalGetCorElementType(RuntimeType rt)
        {
            Debug.Assert(rt.IsActualEnum);
            return InternalGetCorElementType(new QCallTypeHandle(ref rt));
        }

        private CorElementType InternalGetCorElementType()
        {
            RuntimeType this_type = (RuntimeType)GetType();
            return InternalGetCorElementType(new QCallTypeHandle(ref this_type));
        }

        internal static RuntimeType InternalGetUnderlyingType(RuntimeType enumType)
        {
            RuntimeType? res = null;
            InternalGetUnderlyingType(new QCallTypeHandle(ref enumType), ObjectHandleOnStack.Create(ref res));
            return res!;
        }

        private static unsafe EnumInfo<TStorage> GetEnumInfo<TStorage>(RuntimeType enumType, bool getNames = true)
            where TStorage : struct, INumber<TStorage>
        {
            Debug.Assert(
                typeof(TStorage) == typeof(byte) || typeof(TStorage) == typeof(ushort) || typeof(TStorage) == typeof(uint) || typeof(TStorage) == typeof(ulong) ||
                typeof(TStorage) == typeof(nuint) || typeof(TStorage) == typeof(float) || typeof(TStorage) == typeof(double) || typeof(TStorage) == typeof(char));

            EnumInfo<TStorage>? entry = enumType.Cache.EnumInfo as EnumInfo<TStorage>;
            Debug.Assert(entry is null || entry.Names is not null);

            if (entry == null)
            {
                GetEnumValuesAndNames(new QCallTypeHandle(ref enumType), out ulong[]? uint64Values, out string[]? names);
                Debug.Assert(names is not null);
                Debug.Assert(uint64Values is not null);

                TStorage[] values;
                if (typeof(TStorage) == typeof(ulong))
                {
                    values = (TStorage[])(object)uint64Values;
                }
                else
                {
                    values = new TStorage[uint64Values.Length];
                    switch (sizeof(TStorage))
                    {
                        case 1:
                            for (int i = 0; i < values.Length; i++)
                            {
                                byte value = (byte)uint64Values[i];
                                values[i] = *(TStorage*)(&value);
                            }
                            break;

                        case 2:
                            for (int i = 0; i < values.Length; i++)
                            {
                                ushort value = (ushort)uint64Values[i];
                                values[i] = *(TStorage*)(&value);
                            }
                            break;

                        case 4:
                            for (int i = 0; i < values.Length; i++)
                            {
                                uint value = (uint)uint64Values[i];
                                values[i] = *(TStorage*)(&value);
                            }
                            break;

                        case 8:
                            for (int i = 0; i < values.Length; i++)
                            {
                                ulong value = uint64Values[i];
                                values[i] = *(TStorage*)(&value);
                            }
                            break;
                    }
                }

                // il2wasm: [Flags] is in the compiler's table of the enum (no attribute reflection)
                bool hasFlagsAttribute = Il2Wasm.Runtime.Mem.LoadI32(Il2Wasm.Runtime.TypeInfo.EnumInfo(enumType.TypeId) + 4) != 0;
                entry = new EnumInfo<TStorage>(hasFlagsAttribute, values, names);
                enumType.Cache.EnumInfo = entry;
            }

            return entry;
        }
    }
}
