// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// cs2wasm: Mono's internal calls are implemented over cs2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Diagnostics
{
    // Need our own stackframe class since the shared version has its own fields
    [StructLayout(LayoutKind.Sequential)]
    internal sealed class MonoStackFrame
    {
        #region Keep in sync with object-internals.h
        internal int ilOffset;
        internal int nativeOffset;
        // Unused
        internal long methodAddress;
        // Unused
        internal uint methodIndex;
        internal MethodBase? methodBase;
        internal string? fileName;
        internal int lineNumber;
        internal int columnNumber;
        // Unused
        internal string? internalMethodName;
        #endregion

        internal bool isLastFrameFromForeignException;
    }

    public partial class StackTrace
    {
        // cs2wasm: an exception's frames, from its trace's text ("   at M in file:line N"): the file and line, and the
        // method's name as text (no MethodBase: reflection metadata is kept only for what the program reflects on)
        internal static void GetTrace(ObjectHandleOnStack ex, ObjectHandleOnStack res, int skipFrames, bool needFileInfo)
        {
            var e = Cs2Wasm.Runtime.Cs2WasmArrays.Handle<Exception>(ex);
            var lines = (e.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var frames = new List<MonoStackFrame>();
            foreach (var raw in lines)
            {
                var l = raw.Trim();
                if (!l.StartsWith("at ", StringComparison.Ordinal)) continue;
                if (skipFrames > 0) { skipFrames--; continue; }
                var f = new MonoStackFrame { ilOffset = -1, nativeOffset = -1 };
                int i = l.LastIndexOf(" in ", StringComparison.Ordinal), ln = l.LastIndexOf(":line ", StringComparison.Ordinal);
                if (i > 0 && ln > i)
                {
                    f.internalMethodName = l.Substring(3, i - 3);
                    f.fileName = l.Substring(i + 4, ln - i - 4);
                    int.TryParse(l.AsSpan(ln + 6), out f.lineNumber);
                }
                else f.internalMethodName = l.Substring(3);
                frames.Add(f);
            }
            Cs2Wasm.Runtime.TypeInfo.Set(res, frames.ToArray());
        }

        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:RequiresUnreferencedCode",
            Justification = "StackFrame.GetMethod is getting compared to null but nothing else on it is touched.")]
        [MethodImplAttribute(MethodImplOptions.NoInlining)]
        private void InitializeForCurrentThread(int skipFrames, bool needFileInfo)
        {
            skipFrames += 2; // Current method + parent ctor

            StackFrame sf;
            var frames = new List<StackFrame>();
            while (skipFrames >= 0)
            {
                sf = new StackFrame(skipFrames, needFileInfo);
                if (sf.GetMethod() == null)
                {
                    break;
                }
                frames.Add(sf);
                skipFrames++;
            }

            _stackFrames = frames.ToArray();
            _numOfFrames = _stackFrames.Length;
        }

        private void InitializeForException(Exception e, int skipFrames, bool needFileInfo)
        {
            MonoStackFrame[]? frames = null;
            GetTrace(ObjectHandleOnStack.Create(ref e), ObjectHandleOnStack.Create(ref frames), skipFrames, needFileInfo);
            _numOfFrames = frames!.Length;

            int foreignFrames;
            MonoStackFrame[]? foreignExceptions = e.foreignExceptionsFrames;

            if (foreignExceptions != null)
            {
                foreignFrames = foreignExceptions.Length;
                _numOfFrames += foreignFrames;

                _stackFrames = new StackFrame[_numOfFrames];

                for (int i = 0; i < foreignExceptions.Length; ++i)
                {
                    _stackFrames[i] = new StackFrame(foreignExceptions[i], needFileInfo);
                }
            }
            else
            {
                _stackFrames = new StackFrame[_numOfFrames];
                foreignFrames = 0;
            }

            for (int i = 0; i < frames.Length; ++i)
            {
                _stackFrames[foreignFrames + i] = new StackFrame(frames[i], needFileInfo);
            }
        }
    }
}
