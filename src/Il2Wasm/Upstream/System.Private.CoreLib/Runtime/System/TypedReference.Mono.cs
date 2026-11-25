// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's internal calls are implemented over il2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Reflection;
using System.Runtime.CompilerServices;

namespace System
{
    public ref partial struct TypedReference
    {
        #region sync with object-internals.h
        #pragma warning disable CA1823 // used by runtime
        private readonly RuntimeTypeHandle type;
        private readonly ref byte _value;
        private readonly IntPtr _type;
        #pragma warning restore CA1823
        #endregion

        private TypedReference(ref byte target, RuntimeType targetType)
        {
            type = new RuntimeTypeHandle(targetType);
            _value = ref target;
            _type = RuntimeTypeHandle.GetMonoClass(targetType);
        }

        // il2wasm: the referenced value as an object, as an array element is (Il2WasmArrays.BoxAt); _type is the type's id
        public static unsafe object? ToObject(TypedReference value)
        {
            int e = (int)value._type;
            int addr = (int)Unsafe.AsPointer(ref value._value);
            return Il2Wasm.Runtime.Il2WasmArrays.BoxAt(e, addr, Il2Wasm.Runtime.TypeInfo.Size(e), "TypedReference of a struct with references");
        }
    }
}
