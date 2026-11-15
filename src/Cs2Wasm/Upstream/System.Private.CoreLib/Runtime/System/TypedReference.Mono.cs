// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over cs2wasm's runtime; those it has no equivalent for yet throw
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

        public static unsafe object? ToObject(TypedReference value)
            => InternalToObject(&value);

                internal static unsafe object InternalToObject(void* value) => throw Cs2Wasm.Runtime.TypeInfo.NotYet("TypedReference");
    }
}
