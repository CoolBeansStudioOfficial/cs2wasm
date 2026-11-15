// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over cs2wasm's runtime; those it has no equivalent for yet throw
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

        public static extern int ExitCode
        {
            [MethodImplAttribute(MethodImplOptions.InternalCall)]
            get;
            [MethodImplAttribute(MethodImplOptions.InternalCall)]
            set;
        }

                internal static int GetProcessorCount() => Cs2Wasm.Runtime.Engine.ProcessorCount();

                [DoesNotReturn]
        [Cs2Wasm.Runtime.MissingCapability("processes")]        public static extern void Exit(int exitCode);

                public static string[] GetCommandLineArgs() => new string[] { "" };

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
            Cs2Wasm.Runtime.Rt.ReportUnhandled(exception ?? new Exception(message));
            Cs2Wasm.Runtime.Rt.Unreachable();
        }
    }
}
