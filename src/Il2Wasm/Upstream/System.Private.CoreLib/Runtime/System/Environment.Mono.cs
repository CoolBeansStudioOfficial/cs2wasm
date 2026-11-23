// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's internal calls are implemented over il2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

namespace System
{
    public partial class Environment
    {
        public static int CurrentManagedThreadId => Thread.CurrentThread.ManagedThreadId;

        // il2wasm: kept for the engine to read; there is no process to exit
        public static int ExitCode { get; set; }

                internal static int GetProcessorCount() => Il2Wasm.Runtime.Engine.ProcessorCount();

                [DoesNotReturn]
        [Il2Wasm.Runtime.MissingCapability("processes")]        public static extern void Exit(int exitCode);

                // il2wasm: the host's (Boundary/Interop.Env.cs)
                public static string[] GetCommandLineArgs() => Il2Wasm.Runtime.CommandLine.Args();

        [DoesNotReturn]
        public static void FailFast(string? message)
        {
            FailFast(message, null, null);
        }

        [DoesNotReturn]
        public static void FailFast(string? message, Exception? exception)
        {
            FailFast(message, exception, null);
        }

                [DoesNotReturn]
        internal static void FailFast(string? message, Exception? exception, string? errorSource)
        {
            // the engine is told, as for an exception that escapes an export, and the module traps
            Il2Wasm.Runtime.Rt.ReportUnhandled(exception ?? new Exception(message));
            Il2Wasm.Runtime.Rt.Unreachable();
        }
    }
}
