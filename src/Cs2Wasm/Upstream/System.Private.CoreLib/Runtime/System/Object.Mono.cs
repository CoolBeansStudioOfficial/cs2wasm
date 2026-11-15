// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over the compiler's type tables (Cs2Wasm.Runtime.TypeInfo, by type id).

using System.Runtime.CompilerServices;

namespace System
{
    public partial class Object
    {
        [Intrinsic]
        public Type GetType() => GetType();

                protected object MemberwiseClone()
        {
            // cs2wasm: a new object of the same type with the same contents; the references it copies are counted
            int id = Cs2Wasm.Runtime.Rt.TypeId(this);
            if (this is string) return this;
            if (this is Array arr)
            {
                if (arr.Rank != 1) throw Cs2Wasm.Runtime.TypeInfo.NotYet("cloning a multidimensional array");
                int size = Cs2Wasm.Runtime.Cs2WasmArrays.ElementSize(arr);
                var copy = Cs2Wasm.Runtime.Rt.Take<Array>(Cs2Wasm.Runtime.Rt.AllocArray(arr.Length, size, id));
                Cs2Wasm.Runtime.Cs2WasmArrays.CopyElements(arr, 0, copy, 0, arr.Length, size);
                return copy;
            }
            if ((Cs2Wasm.Runtime.TypeInfo.Flags2(id) & 1) != 0) throw Cs2Wasm.Runtime.TypeInfo.NotYet("MemberwiseClone of an object with reference fields");
            int bytes = (Cs2Wasm.Runtime.TypeInfo.Flags(id) & 1) != 0 ? Cs2Wasm.Runtime.TypeInfo.BoxOffset(id) + Cs2Wasm.Runtime.TypeInfo.Size(id) : Cs2Wasm.Runtime.TypeInfo.Size(id);
            int o = Cs2Wasm.Runtime.Rt.AllocObject(bytes, id);
            Cs2Wasm.Runtime.Mem.Copy(o + 12, Cs2Wasm.Runtime.Rt.AddressOf(this) + 12, bytes - 12);
            return Cs2Wasm.Runtime.Rt.Take<object>(o);
        }

    }
}
