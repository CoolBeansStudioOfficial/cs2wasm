using System;
using System.Runtime.InteropServices;
using Cs2Wasm.Runtime;

// cs2wasm: Console output goes to the engine's log import (SPEC 7.4). Standard output and error are file
// descriptors 1 and 2 of the browser console pal; their UTF-8 bytes are decoded to the UTF-16 the import takes.
internal static partial class Interop
{
    internal static unsafe partial class Sys
    {
        internal static unsafe partial int Write(SafeHandle fd, byte* buffer, int bufferSize) => Write(fd.DangerousGetHandle(), buffer, bufferSize);

        internal static unsafe partial int Write(IntPtr fd, byte* buffer, int bufferSize)
        {
            if (bufferSize <= 0) return 0;
            char[] text = new char[bufferSize];
            int n = System.Text.Encoding.UTF8.GetChars(buffer, bufferSize, (char*)Rt.ArrayData(text), bufferSize);
            Engine.Write(Rt.ArrayData(text), n);
            return bufferSize;
        }
    }
}
