// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// il2wasm: Mono's internal calls are implemented over il2wasm's runtime; those it has no equivalent for yet throw
// NotSupportedException, and those the sandbox lacks are build errors (MissingCapability).

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace System.Diagnostics
{
    public partial class StackFrame
    {
        /// <summary>il2wasm: the frame's method as a trace writes it ("Program.A()"); GetMethod() is null.</summary>
        internal string? _il2wasmName;

        internal StackFrame(MonoStackFrame monoStackFrame, bool needFileInfo)
        {
            _il2wasmName = monoStackFrame.internalMethodName;
            _method = monoStackFrame.methodBase;
            _nativeOffset = monoStackFrame.nativeOffset;
            _ilOffset = monoStackFrame.ilOffset;

            if (needFileInfo)
            {
                _fileName = monoStackFrame.fileName;
                _lineNumber = monoStackFrame.lineNumber;
                _columnNumber = monoStackFrame.columnNumber;
            }

            _isLastFrameFromForeignExceptionStackTrace = monoStackFrame.isLastFrameFromForeignException;
        }

        [MethodImplAttribute(MethodImplOptions.NoInlining)]
        private void BuildStackFrame(int skipFrames, bool needFileInfo)
        {
            // il2wasm: the stack as the engine gives it (Rt.CurrentTrace), without this class's own frames
            if (skipFrames < 0)
                return;
            var all = StackTrace.CurrentFrames();
            if (skipFrames >= all.Count)
                return;
            var f = all[skipFrames];
            _il2wasmName = f.internalMethodName;
            if (needFileInfo)
            {
                _fileName = f.fileName;
                _lineNumber = f.lineNumber;
                _columnNumber = f.columnNumber;
            }
        }

#pragma warning disable IDE0060
        private static bool AppendStackFrameWithoutMethodBase(StringBuilder sb) => false;
#pragma warning restore IDE0060

                private static bool GetFrameInfo(int skipFrames, bool needFileInfo,
                                                ObjectHandleOnStack out_method, ObjectHandleOnStack out_file,
                                                out int ilOffset, out int nativeOffset, out int line, out int column)
                {
                    // il2wasm: traces are text (Exception.StackTrace), not frames: a StackTrace of the current thread has
                    // none (so code that takes one, as Task does for a canceled task's exception, goes on)
                    ilOffset = nativeOffset = line = column = 0;
                    return false;
                }
    }
}
