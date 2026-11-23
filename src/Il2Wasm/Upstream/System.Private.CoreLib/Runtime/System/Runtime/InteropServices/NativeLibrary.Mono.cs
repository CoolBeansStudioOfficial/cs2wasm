// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's internal calls are implemented over il2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Reflection;
using System.Runtime.CompilerServices;

namespace System.Runtime.InteropServices
{
    public partial class NativeLibrary
    {
        private static IntPtr LoadLibraryByName(string libraryName, Assembly assembly, DllImportSearchPath? searchPath, bool throwOnError)
        {
            return LoadByName(libraryName,
                               (RuntimeAssembly)assembly,
                               searchPath.HasValue,
                               (uint)searchPath.GetValueOrDefault(),
                               throwOnError);
        }

                [Il2Wasm.Runtime.MissingCapability("native libraries")]        private static extern IntPtr LoadFromPath(string libraryName, bool throwOnError);

                [Il2Wasm.Runtime.MissingCapability("native libraries")]        private static extern IntPtr LoadByName(string libraryName, RuntimeAssembly callingAssembly, bool hasDllImportSearchPathFlag, uint dllImportSearchPathFlag, bool throwOnError);

                [Il2Wasm.Runtime.MissingCapability("native libraries")]        private static extern void FreeLib(IntPtr handle);

                [Il2Wasm.Runtime.MissingCapability("native libraries")]        private static extern IntPtr GetSymbol(IntPtr handle, string symbolName, bool throwOnError);

        private static void MonoLoadLibraryCallbackStub(string libraryName, Assembly assembly, bool hasDllImportSearchPathFlags, uint dllImportSearchPathFlags, ref IntPtr dll)
        {
            dll = LoadLibraryCallbackStub(libraryName, assembly, hasDllImportSearchPathFlags, dllImportSearchPathFlags);
        }
    }
}
