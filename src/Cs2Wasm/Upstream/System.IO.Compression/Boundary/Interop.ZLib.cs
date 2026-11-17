// cs2wasm: the zlib functions System.IO.Compression calls (upstream declares them as platform invokes into
// libSystem.IO.Compression.Native), over the deflate and inflate of ManagedZLib.cs. A stream's state is an object, held
// through the ZStream's internal state field by a handle.
using System;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Cs2Wasm.Compression;

internal static partial class Interop
{
    internal static partial class ZLib
    {
        /// <summary>The ZStream's internalState (private to it): the fourth pointer-sized field.</summary>
        static unsafe IntPtr* StateOf(ZLibNative.ZStream* stream) => (IntPtr*)stream + 3;

        static unsafe T? State<T>(ZLibNative.ZStream* stream) where T : class
        {
            var h = *StateOf(stream);
            return h == IntPtr.Zero ? null : GCHandle.FromIntPtr(h).Target as T;
        }

        static unsafe ZLibNative.ErrorCode End(ZLibNative.ZStream* stream)
        {
            var h = *StateOf(stream);
            if (h == IntPtr.Zero) return ZLibNative.ErrorCode.StreamError;
            GCHandle.FromIntPtr(h).Free();
            *StateOf(stream) = IntPtr.Zero;
            return ZLibNative.ErrorCode.Ok;
        }

        internal static unsafe partial ZLibNative.ErrorCode DeflateInit2_(ZLibNative.ZStream* stream, ZLibNative.CompressionLevel level, ZLibNative.CompressionMethod method, int windowBits, int memLevel, ZLibNative.CompressionStrategy strategy)
        {
            *StateOf(stream) = GCHandle.ToIntPtr(GCHandle.Alloc(new ManagedDeflater((int)level, windowBits)));
            return ZLibNative.ErrorCode.Ok;
        }

        internal static unsafe partial ZLibNative.ErrorCode Deflate(ZLibNative.ZStream* stream, ZLibNative.FlushCode flush)
        {
            var d = State<ManagedDeflater>(stream);
            if (d == null) return ZLibNative.ErrorCode.StreamError;
            byte* nextIn = (byte*)stream->nextIn, nextOut = (byte*)stream->nextOut;
            uint availIn = stream->availIn, availOut = stream->availOut;
            int r = d.Deflate(&nextIn, &availIn, &nextOut, &availOut, (int)flush);
            stream->nextIn = (IntPtr)nextIn; stream->availIn = availIn;
            stream->nextOut = (IntPtr)nextOut; stream->availOut = availOut;
            return (ZLibNative.ErrorCode)r;
        }

        internal static unsafe partial ZLibNative.ErrorCode DeflateEnd(ZLibNative.ZStream* stream) => End(stream);

        internal static unsafe partial ZLibNative.ErrorCode DeflateReset(ZLibNative.ZStream* stream)
        {
            var d = State<ManagedDeflater>(stream);
            if (d == null) return ZLibNative.ErrorCode.StreamError;
            d.Reset();
            return ZLibNative.ErrorCode.Ok;
        }

        internal static unsafe partial ZLibNative.ErrorCode InflateInit2_(ZLibNative.ZStream* stream, int windowBits)
        {
            *StateOf(stream) = GCHandle.ToIntPtr(GCHandle.Alloc(new ManagedInflater(windowBits)));
            return ZLibNative.ErrorCode.Ok;
        }

        internal static unsafe partial ZLibNative.ErrorCode InflateReset2_(ZLibNative.ZStream* stream, int windowBits)
        {
            var i = State<ManagedInflater>(stream);
            if (i == null) return ZLibNative.ErrorCode.StreamError;
            i.Reset(windowBits);
            return ZLibNative.ErrorCode.Ok;
        }

        internal static unsafe partial ZLibNative.ErrorCode Inflate(ZLibNative.ZStream* stream, ZLibNative.FlushCode flush)
        {
            var i = State<ManagedInflater>(stream);
            if (i == null) return ZLibNative.ErrorCode.StreamError;
            byte* nextIn = (byte*)stream->nextIn, nextOut = (byte*)stream->nextOut;
            uint availIn = stream->availIn, availOut = stream->availOut;
            int r = i.Inflate(&nextIn, &availIn, &nextOut, &availOut);
            stream->nextIn = (IntPtr)nextIn; stream->availIn = availIn;
            stream->nextOut = (IntPtr)nextOut; stream->availOut = availOut;
            return (ZLibNative.ErrorCode)r;
        }

        internal static unsafe partial ZLibNative.ErrorCode InflateEnd(ZLibNative.ZStream* stream) => End(stream);

        internal static unsafe partial uint crc32(uint crc, byte* buffer, int len) => Checksums.Crc32(crc, buffer, len);

        /// <summary>zlib's bound, with the room a gzip wrapper takes.</summary>
        internal static partial uint compressBound(uint sourceLen) => sourceLen + (sourceLen >> 12) + (sourceLen >> 14) + (sourceLen >> 25) + 13 + 18;
    }
}
