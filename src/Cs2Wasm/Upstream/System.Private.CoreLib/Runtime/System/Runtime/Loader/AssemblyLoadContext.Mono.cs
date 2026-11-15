// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over cs2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Mono;

namespace System.Runtime.Loader
{
    public partial class AssemblyLoadContext
    {
        internal IntPtr NativeALC
        {
            get
            {
                return _nativeAssemblyLoadContext;
            }
        }

        [DynamicDependency(nameof(_nativeAssemblyLoadContext))]
        private IntPtr InitializeAssemblyLoadContext(IntPtr thisHandlePtr, bool representsTPALoadContext, bool isCollectible)
        {
            // cs2wasm: no native context (nothing is loaded at run time; GetLoadContext answers Default)
            return IntPtr.Zero;
        }

        // Keep the type alive since instances are created by the runtime
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(LoaderAllocator))]
        private static void KeepLoaderAllocator()
        {
        }

        // cs2wasm: no native context to release (InitializeAssemblyLoadContext makes none)
        private static void PrepareForAssemblyLoadContextRelease(IntPtr nativeAssemblyLoadContext, IntPtr assemblyLoadContextStrong) { }

        [RequiresUnreferencedCode("Types and members the loaded assembly depends on might be removed")]
        [System.Security.DynamicSecurityMethod] // Methods containing StackCrawlMark local var has to be marked DynamicSecurityMethod
        private Assembly InternalLoadFromPath(string? assemblyPath)
        {
            StackCrawlMark stackMark = StackCrawlMark.LookForMyCaller;

            assemblyPath = assemblyPath?.Replace('\\', Path.DirectorySeparatorChar);
            return InternalLoadFile(NativeALC, assemblyPath, ref stackMark);
        }

        [RequiresUnreferencedCode("Types and members the loaded assembly depends on might be removed")]
        internal Assembly InternalLoad(ReadOnlySpan<byte> arrAssembly, ReadOnlySpan<byte> arrSymbols)
        {
            unsafe
            {
                fixed (byte* ptrAssembly = arrAssembly, ptrSymbols = arrSymbols)
                {
                    return InternalLoadFromStream(NativeALC, new IntPtr(ptrAssembly), arrAssembly.Length,
                                       new IntPtr(ptrSymbols), arrSymbols.Length);
                }
            }
        }

        internal static Assembly[] GetLoadedAssemblies()
        {
            return InternalGetLoadedAssemblies();
        }

                [Cs2Wasm.Runtime.MissingCapability("loading assemblies at run time")]        private static extern IntPtr GetLoadContextForAssembly(RuntimeAssembly rtAsm);

        // Returns the load context in which the specified assembly has been loaded
        public static AssemblyLoadContext? GetLoadContext(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);

            // cs2wasm: every assembly is the module's, in the default context (nothing is loaded at run time)
            return assembly is RuntimeAssembly ? Default : null;
        }

        public void SetProfileOptimizationRoot(string directoryPath)
        {
        }

        public void StartProfileOptimization(string? profile)
        {
        }

        [RequiresUnreferencedCode("Types and members the loaded assembly depends on might be removed")]
                [Cs2Wasm.Runtime.MissingCapability("loading assemblies at run time")]        private static extern Assembly InternalLoadFile(IntPtr nativeAssemblyLoadContext, string? assemblyFile, ref StackCrawlMark stackMark);

                [Cs2Wasm.Runtime.MissingCapability("loading assemblies at run time")]        private static extern IntPtr InternalInitializeNativeALC(IntPtr thisHandlePtr, IntPtr name, bool representsTPALoadContext, bool isCollectible);

        [RequiresUnreferencedCode("Types and members the loaded assembly depends on might be removed")]
                [Cs2Wasm.Runtime.MissingCapability("loading assemblies at run time")]        private static extern Assembly InternalLoadFromStream(IntPtr nativeAssemblyLoadContext, IntPtr assm, int assmLength, IntPtr symbols, int symbolsLength);

                private static Assembly[] InternalGetLoadedAssemblies() => System.Array.Empty<System.Reflection.Assembly>();

        // Invoked by Mono to resolve using the load method.
        private static RuntimeAssembly? MonoResolveUsingLoad(IntPtr gchALC, string assemblyName)
        {
            return Resolve(gchALC, new AssemblyName(assemblyName));
        }

        // Invoked by Mono to resolve using the Resolving event after
        // trying the Load override and default load context without
        // success.
        private static RuntimeAssembly? MonoResolveUsingResolvingEvent(IntPtr gchALC, string assemblyName)
        {
            AssemblyLoadContext context = GetAssemblyLoadContext(gchALC);
            return context.ResolveUsingEvent(new AssemblyName(assemblyName));
        }

        // Invoked by Mono to resolve requests to load satellite assemblies.
        private static RuntimeAssembly? MonoResolveUsingResolveSatelliteAssembly(IntPtr gchALC, string assemblyName)
        {
            AssemblyLoadContext context = GetAssemblyLoadContext(gchALC);
            return context.ResolveSatelliteAssembly(new AssemblyName(assemblyName));
        }

        private static AssemblyLoadContext GetAssemblyLoadContext(IntPtr gchAssemblyLoadContext)
        {
            AssemblyLoadContext context;
            // This check exists because the function can be called early in startup, before the default ALC is initialized
            if (gchAssemblyLoadContext == IntPtr.Zero)
                context = Default;
            else
                context = (AssemblyLoadContext)(GCHandle.FromIntPtr(gchAssemblyLoadContext).Target)!;
            return context;
        }

        private static void MonoResolveUnmanagedDll(string unmanagedDllName, IntPtr gchAssemblyLoadContext, ref IntPtr dll)
        {
            AssemblyLoadContext context = GetAssemblyLoadContext(gchAssemblyLoadContext);
            dll = context.LoadUnmanagedDll(unmanagedDllName);
        }

        private static void MonoResolveUnmanagedDllUsingEvent(string unmanagedDllName, Assembly assembly, IntPtr gchAssemblyLoadContext, ref IntPtr dll)
        {
            AssemblyLoadContext context = GetAssemblyLoadContext(gchAssemblyLoadContext);
            dll = context.GetResolvedUnmanagedDll(assembly, unmanagedDllName);
        }

        private static RuntimeAssembly? GetRuntimeAssembly(Assembly? asm)
        {
            return
                asm == null ? null :
                asm is RuntimeAssembly rtAssembly ? rtAssembly :
                asm is System.Reflection.Emit.AssemblyBuilder ab ? Unsafe.As<RuntimeAssembly>(ab) : // Mono AssemblyBuilder is also a RuntimeAssembly, see AssemblyBuilder.Mono.cs
                null;
        }
    }
}
