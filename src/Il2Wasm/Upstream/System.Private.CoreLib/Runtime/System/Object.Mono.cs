// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's internal calls are implemented over the compiler's type tables (Il2Wasm.Runtime.TypeInfo, by type id).

using System.Runtime.CompilerServices;

namespace System
{
    public partial class Object
    {
        [Intrinsic]
        public Type GetType() => GetType();

        // il2wasm: a copy of an object (the IL interpreter's copies of value-type values)
        internal static object CloneOf(object o) => o.MemberwiseClone();

                protected object MemberwiseClone()
        {
            // il2wasm: a new object of the same type with the same contents; the references it copies are counted
            int id = Il2Wasm.Runtime.Rt.TypeId(this);
            if (this is string) return this;
            if (this is Array arr)
            {

                int size = Il2Wasm.Runtime.Il2WasmArrays.ElementSize(arr);
                // (an array of more dimensions has their lengths before its elements)
                int rank = arr.Rank, from = Il2Wasm.Runtime.Rt.AddressOf(arr);
                int made = rank == 1 ? Il2Wasm.Runtime.Rt.AllocArray(arr.Length, size, id)
                    : Il2Wasm.Runtime.Rt.AllocArrayWithHeader(arr.Length, size, id, Il2Wasm.Runtime.Il2WasmArrays.Data(arr) - from);
                if (rank != 1) Il2Wasm.Runtime.Mem.Copy(made + 16, from + 16, 4 * rank);
                var copy = Il2Wasm.Runtime.Rt.Take<Array>(made);
                Il2Wasm.Runtime.Il2WasmArrays.CopyElements(arr, 0, copy, 0, arr.Length, size);
                return copy;
            }
            int bytes = (Il2Wasm.Runtime.TypeInfo.Flags(id) & 1) != 0 ? Il2Wasm.Runtime.TypeInfo.BoxOffset(id) + Il2Wasm.Runtime.TypeInfo.Size(id) : Il2Wasm.Runtime.TypeInfo.Size(id);
            int o = Il2Wasm.Runtime.Rt.AllocObject(bytes, id);
            // (threads: the fields are copied and counted with no store able to come in between)
            Il2Wasm.Runtime.Rt.BulkEnter();
            Il2Wasm.Runtime.Mem.Copy(o + 12, Il2Wasm.Runtime.Rt.AddressOf(this) + 12, bytes - 12);
            var r = Il2Wasm.Runtime.Rt.Take<object>(o);
            if ((Il2Wasm.Runtime.TypeInfo.Flags2(id) & 1) != 0) Il2Wasm.Runtime.Rt.RetainContents(r);
            Il2Wasm.Runtime.Rt.BulkExit();
            return r;
        }

    }
}
