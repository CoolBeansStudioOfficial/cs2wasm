// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over cs2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Diagnostics.Tracing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;

namespace System.Reflection
{
    public partial class Assembly
    {
        [Obsolete("Assembly.LoadWithPartialName has been deprecated. Use Assembly.Load() instead.")]
        [System.Security.DynamicSecurityMethod] // Methods containing StackCrawlMark local var has to be marked DynamicSecurityMethod
        public static Assembly? LoadWithPartialName(string partialName)
        {
            ArgumentNullException.ThrowIfNull(partialName);

            if (partialName.Length == 0 || partialName[0] == '\0')
                throw new ArgumentException(SR.Format_StringZeroLength, nameof(partialName));

            try
            {
                StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
                return InternalLoad(partialName, ref stackMark, IntPtr.Zero);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        [System.Security.DynamicSecurityMethod] // Methods containing StackCrawlMark local var has to be marked DynamicSecurityMethod
        public static Assembly GetExecutingAssembly()
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return GetExecutingAssembly(ref stackMark);
        }

                internal static RuntimeAssembly GetExecutingAssembly(ref StackCrawlMark stackMark) => throw Cs2Wasm.Runtime.TypeInfo.NotYet("reflection (Mono's GetExecutingAssembly): cs2wasm's reflection reads the compiler's metadata");

        [System.Security.DynamicSecurityMethod] // Methods doing stack walks has to be marked DynamicSecurityMethod
        public static Assembly GetCallingAssembly()
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCallersCaller;
            return GetCallingAssembly(ref stackMark);
        }

        // cs2wasm: no stack walk over assemblies: the program's (callers in a library calling back are not told apart)
        internal static RuntimeAssembly GetCallingAssembly(ref StackCrawlMark stackMark) => RuntimeAssembly.OfIndex(0);

                internal static Assembly GetEntryAssemblyNative() => RuntimeAssembly.OfIndex(0);

        private static Assembly? GetEntryAssemblyInternal()
        {
            return GetEntryAssemblyNative();
        }

        [System.Security.DynamicSecurityMethod] // Methods containing StackCrawlMark local var has to be marked DynamicSecurityMethod
        public static Assembly Load(string assemblyString)
        {
            ArgumentNullException.ThrowIfNull(assemblyString);

            var name = new AssemblyName(assemblyString);
            // TODO: trigger assemblyFromResolveEvent

            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return Load(name, ref stackMark, AssemblyLoadContext.CurrentContextualReflectionContext);
        }

        [System.Security.DynamicSecurityMethod] // Methods containing StackCrawlMark local var has to be marked DynamicSecurityMethod
        public static Assembly Load(AssemblyName assemblyRef)
        {
            ArgumentNullException.ThrowIfNull(assemblyRef);

            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;
            return Load(assemblyRef, ref stackMark, AssemblyLoadContext.CurrentContextualReflectionContext);
        }

        internal static Assembly Load(AssemblyName assemblyRef, ref StackCrawlMark stackMark, AssemblyLoadContext? assemblyLoadContext)
        {
            // TODO: pass AssemblyName
            Assembly? assembly = InternalLoad(assemblyRef.FullName, ref stackMark, assemblyLoadContext != null ? assemblyLoadContext.NativeALC : IntPtr.Zero);
            if (assembly == null)
                throw new FileNotFoundException(null, assemblyRef.Name);
            return assembly;
        }

        internal static uint GetAssemblyCount()
        {
            return (uint)EventPipeInternal.GetRuntimeCounterValue(EventPipeInternal.RuntimeCounters.ASSEMBLY_COUNT);
        }

        // cs2wasm: the assemblies are those in the compiler's table (Compiler/Reflection.cs), all loaded from the start: the one
        // of that simple name (compared as .NET's loader does, ignoring case), or none (Load throws FileNotFoundException)
        internal static Assembly? InternalLoad(string assemblyName, ref StackCrawlMark stackMark, IntPtr ptrLoadContextBinder)
        {
            string? simple = new AssemblyName(assemblyName).Name;
            int n = Cs2Wasm.Runtime.Meta.I(Cs2Wasm.Runtime.TypeInfo.AssemblyNames(), 0);
            for (int i = 0; i < n; i++)
            {
                var a = RuntimeAssembly.OfIndex(i);
                if (string.Equals(a.GetSimpleName(), simple, StringComparison.OrdinalIgnoreCase)) return a;
            }
            return null;
        }

        // cs2wasm: a type of the assembly by its full name (nested types with '+')
        internal Type InternalGetType(Module? module, string name, bool throwOnError, bool ignoreCase)
        {
            var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var t in ((RuntimeAssembly)this).TypesOf())
                if (string.Equals(t.FullName, name, cmp)) return t;
            if (throwOnError) throw new TypeLoadException("Could not load type '" + name + "' from assembly '" + FullName + "'.");
            return null!;
        }
    }
}
