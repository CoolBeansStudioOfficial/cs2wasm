// il2wasm: the internal calls of the stock browser-wasm libraries above CoreLib (natives.txt, `internalcall` lines), and
// CoreLib's own hybrid-globalization one. Mono's browser runtime implements these in C and in its JavaScript
// (dotnet.runtime.js); here they are the runtime's, over host imports where they reach JavaScript.
using System;

namespace Il2Wasm.Runtime.Natives
{
    /// <summary>System.Console's ConsolePal.Clear (browser): the console's own clear, as Mono's
    /// mono_wasm_console_clear calls console.clear().</summary>
    internal static class ConsoleNative
    {
        [Import("il2wasm", "console_clear")] static extern void HostClear();

        public static void Clear() => HostClear();
    }

    /// <summary>System.Runtime.InteropServices.JavaScript's Interop.Runtime: the calls that the [JSImport] and
    /// [JSExport] code the JSImport generator writes makes into the runtime. The JavaScript side of Mono's interop
    /// (the JSMarshalerArgument buffers in linear memory, function bindings, promises, JS-held C# objects) is the
    /// host's: each call that reaches JavaScript is a host import with the same arguments, which a host implements as
    /// dotnet.runtime.js does. GC roots have no meaning without a collector: a registered root is a handle and nothing
    /// else.</summary>
    internal static unsafe class JsInteropNative
    {
        [Import("il2wasm", "js_bind_import")] static extern int HostBindJSImport(void* signature);
        [Import("il2wasm", "js_invoke_import")] static extern void HostInvokeJSImport(int functionHandle, int arguments);
        [Import("il2wasm", "js_invoke_function")] static extern void HostInvokeJSFunction(int functionHandle, int arguments);
        [Import("il2wasm", "js_cancel_promise")] static extern void HostCancelPromise(int taskHolderGCHandle);
        [Import("il2wasm", "js_resolve_or_reject_promise")] static extern void HostResolveOrRejectPromise(int arguments);
        [Import("il2wasm", "js_release_cs_owned_object")] static extern void HostReleaseCSOwnedObject(int jsHandle);

        public static int BindJSImportST(void* signature) => HostBindJSImport(signature);
        public static void InvokeJSImportST(int functionHandle, int arguments) => HostInvokeJSImport(functionHandle, arguments);
        public static void InvokeJSFunction(int functionHandle, int arguments) => HostInvokeJSFunction(functionHandle, arguments);
        public static void CancelPromise(int taskHolderGCHandle) => HostCancelPromise(taskHolderGCHandle);
        public static void ResolveOrRejectPromise(int arguments) => HostResolveOrRejectPromise(arguments);
        public static void ReleaseCSOwnedObject(int jsHandle) => HostReleaseCSOwnedObject(jsHandle);

        static int s_nextRoot;

        /// <summary>mono_wasm_register_root: a range kept from a collector. There is none: a handle, never 0.</summary>
        public static int RegisterGCRoot(void* start, int bytesSize, int name) => System.Threading.Interlocked.Increment(ref s_nextRoot);

        public static void DeregisterGCRoot(int handle) { }

        // [JSExport]: Mono finds the generated registration method by name at run time. That binding is the compiler's
        // to make (the exports are known at compile time); until it does, these say so.
        public static void BindAssemblyExports(int assemblyName) => throw new PlatformNotSupportedException("[JSExport] binding is not implemented in il2wasm yet");
        public static void GetAssemblyExport(int assemblyName, int namespaceName, int className, int methodName, int signatureHash, int* monoMethodPtrPtr) => throw new PlatformNotSupportedException("[JSExport] binding is not implemented in il2wasm yet");
        public static void AssemblyGetEntryPoint(int assemblyName, int auto_insert_breakpoint, void** monoMethodPtrPtr) => throw new PlatformNotSupportedException("[JSExport] binding is not implemented in il2wasm yet");
    }
}

internal static partial class Interop
{
    /// <summary>Hybrid globalization's call into the browser's Intl (Interop.Locale.Mono.cs, replaced). il2wasm's
    /// globalization is invariant (SPEC 9): CultureData never asks the browser, as .NET's invariant mode never does, and
    /// the answer, if it were asked, is the invariant culture's names, in the browser's "language##country" form, with
    /// no exception.</summary>
    internal static unsafe partial class JsGlobalization
    {
        internal static unsafe nint GetLocaleInfo(char* locale, int localeLength, char* culture, int cultureLength, char* buffer, int bufferLength, out int resultLength)
        {
            const string invariant = "Invariant Language##Invariant Country";
            int n = Math.Min(invariant.Length, bufferLength);
            for (int i = 0; i < n; i++) buffer[i] = invariant[i];
            resultLength = n;
            return 0;
        }
    }
}
