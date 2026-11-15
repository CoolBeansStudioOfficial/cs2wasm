// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over cs2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System.Reflection
{
    public partial class MethodBase
    {
        public static MethodBase? GetMethodFromHandle(RuntimeMethodHandle handle)
        {
            if (handle.IsNullHandle())
                throw new ArgumentException(SR.Argument_InvalidHandle);

            MethodBase? m = RuntimeMethodInfo.GetMethodFromHandleInternalType(handle.Value, IntPtr.Zero);
            if (m == null)
                throw new ArgumentException(SR.Argument_InvalidHandle);

            Type? declaringType = m.DeclaringType;
            if (declaringType != null && declaringType.IsGenericType)
                throw new ArgumentException(SR.Format(SR.Argument_MethodDeclaringTypeGeneric,
                                                            m, declaringType.GetGenericTypeDefinition()));

            return m;
        }

        public static MethodBase? GetMethodFromHandle(RuntimeMethodHandle handle, RuntimeTypeHandle declaringType)
        {
            if (handle.IsNullHandle())
                throw new ArgumentException(SR.Argument_InvalidHandle);
            MethodBase m = RuntimeMethodInfo.GetMethodFromHandleInternalType(handle.Value, declaringType.Value);
            if (m == null)
                throw new ArgumentException(SR.Argument_InvalidHandle);
            return m;
        }

        [RequiresUnreferencedCode("Metadata for the method might be incomplete or removed")]
                public static MethodBase? GetCurrentMethod() => throw Cs2Wasm.Runtime.TypeInfo.NotYet("reflection (Mono's GetCurrentMethod): cs2wasm's reflection reads the compiler's metadata");

        internal virtual ReadOnlySpan<ParameterInfo> GetParametersAsSpan()
        {
            return GetParametersInternal();
        }

        internal virtual ParameterInfo[] GetParametersInternal()
        {
            throw new NotImplementedException();
        }

        internal virtual int GetParametersCount()
        {
            throw new NotImplementedException();
        }

        internal virtual Type GetParameterType(int pos)
        {
            throw new NotImplementedException();
        }

        internal virtual int get_next_table_index(int table, int count)
        {
            throw new NotImplementedException();
        }
    }
}
