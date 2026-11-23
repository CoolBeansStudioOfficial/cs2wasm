// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    [StructLayout(LayoutKind.Sequential)]
    public partial class Exception
    {
        internal static uint GetExceptionCount()
        {
            return (uint)EventPipeInternal.GetRuntimeCounterValue(EventPipeInternal.RuntimeCounters.EXCEPTION_COUNT);
        }

        internal readonly struct DispatchState
        {
            public readonly MonoStackFrame[]? StackFrames;

            public DispatchState(MonoStackFrame[]? stackFrames)
            {
                StackFrames = stackFrames;
            }
        }

        // il2wasm: the fields the shared Exception.cs uses, and the stack trace as text. The trace is captured when the
        // exception is thrown (_fullTrace, from the throwing frame outward, and the stack depth there) and cut at the
        // catching frame (_stackTraceString), as .NET reports it (Il2Wasm.Runtime.Rt.OnThrow/OnCatch).
        internal string? _message;
        private IDictionary? _data;
        private readonly Exception? _innerException;
        private string? _helpURL;
        private object? _traceIPs;
        internal string? _stackTraceString;
        private string? _remoteStackTraceString;
        private int _HResult;
        private string? _source;
        internal MonoStackFrame[]? foreignExceptionsFrames;
        internal string? _fullTrace;
        internal int _throwDepth;
        // the method that threw when traces hide it ([StackTraceHidden]), as a note of Rt.TakeThrowSite; 0 otherwise
        internal int _throwSite;
        // the trace ends at an async method's frame (its MoveNext took the exception: Rt.OnAsyncCatch), and so did
        // the one ExceptionDispatchInfo captured: .NET writes no "End of stack trace" line after an async frame
        internal bool _asyncTop, _capturedAsync;

        private bool HasBeenThrown => _fullTrace != null || _stackTraceString != null;

        public MethodBase? TargetSite
        {
            [RequiresUnreferencedCode("Metadata for the method might be incomplete or removed")]
            get
            {
                // il2wasm: the method that threw, which a trace leaves out when it is hidden (ThrowHelper's), as .NET's
                if (_throwSite != 0 && Il2Wasm.Runtime.Meta.ThrowSiteMethod(_throwSite) is { } hidden)
                    return hidden;
                StackTrace st = new StackTrace(this, true);
                if (st.FrameCount > 0)
                    return st.GetFrame(0)?.GetMethod();

                return null;
            }
        }

        // il2wasm: traces are text (Boundary/Runtime.cs): ExceptionDispatchInfo keeps the trace so far, and a
        // throw of the captured exception shows it before its own, as .NET's does
        internal string? _capturedTrace;
        // (a throw of the captured exception keeps the method that threw it first: its TargetSite, as .NET's, whose
        // restored trace starts there)
        internal int _capturedSite;
        internal bool _dispatching;

        internal DispatchState CaptureDispatchState()
        {
            _capturedTrace = StackTrace;
            _capturedAsync = _asyncTop;
            _capturedSite = _throwSite;
            return default;
        }

        internal DispatchState CaptureDispatchStateMono()
        {
            MonoStackFrame[]? stackFrames;

            if (_traceIPs != null)
            {
                Exception self = this;
                MonoStackFrame[]? frames = null;
                Diagnostics.StackTrace.GetTrace(ObjectHandleOnStack.Create(ref self), ObjectHandleOnStack.Create(ref frames), 0, true);
                stackFrames = frames!;
                if (stackFrames.Length > 0)
                    stackFrames[stackFrames.Length - 1].isLastFrameFromForeignException = true;

                // Make sure foreignExceptionsFrames does not change at this point.
                // Otherwise, the Array.Copy into combinedStackFrames can fail due to size differences
                //
                // See https://github.com/dotnet/runtime/issues/70081
                MonoStackFrame[]? feFrames = foreignExceptionsFrames;

                if (feFrames != null)
                {
                    var combinedStackFrames = new MonoStackFrame[stackFrames.Length + feFrames.Length];
                    Array.Copy(feFrames, 0, combinedStackFrames, 0, feFrames.Length);
                    Array.Copy(stackFrames, 0, combinedStackFrames, feFrames.Length, stackFrames.Length);

                    stackFrames = combinedStackFrames;
                }
            }
            else
            {
                stackFrames = foreignExceptionsFrames;
            }

            return new DispatchState(stackFrames);
        }

        internal void RestoreDispatchState(in DispatchState state)
        {
            if (_capturedTrace is { Length: > 0 } t)
                _remoteStackTraceString = _capturedAsync ? t + Environment.NewLineConst
                    : t + Environment.NewLineConst + SR.Exception_EndStackTraceFromPreviousThrow + Environment.NewLineConst;
            if (_capturedTrace is { Length: > 0 }) _dispatching = true;
            _stackTraceString = null;
            _fullTrace = null;
        }

        // Returns true if setting the _remoteStackTraceString field is legal, false if not (immutable exception).
        // A false return value means the caller should early-exit the operation.
        // Can also throw InvalidOperationException if a stack trace is already set or if object has been thrown.
        private bool CanSetRemoteStackTrace()
        {
            if (_traceIPs != null || _stackTraceString != null || _remoteStackTraceString != null)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }

            return true; // mono runtime doesn't have immutable agile exceptions, always return true
        }

        private static ListDictionaryInternal CreateDataContainer() => new ListDictionaryInternal();

        private static string? SerializationWatsonBuckets => null;
    }
}
