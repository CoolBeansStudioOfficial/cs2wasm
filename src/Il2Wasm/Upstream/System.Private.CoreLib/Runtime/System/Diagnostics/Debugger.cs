// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's internal calls are implemented over il2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Runtime.CompilerServices;

namespace System.Diagnostics
{
    public static partial class Debugger
    {
        public static bool IsAttached => IsAttached_internal();

                private static bool IsAttached_internal() => false;

        [Intrinsic]
        public static void Break()
        {
            // The JIT inserts a breakpoint on the caller.
        }

                public static bool IsLogging() => false;

        public static bool Launch()
        {
            throw new NotImplementedException();
        }

                private static void Log_icall(int level, ref string category, ref string message) { }

        public static void Log(int level, string? category, string? message)
        {
            category ??= string.Empty;
            message ??= string.Empty;
            Log_icall(level, ref category, ref message);
        }

        public static void NotifyOfCrossThreadDependency()
        {
        }
    }
}
