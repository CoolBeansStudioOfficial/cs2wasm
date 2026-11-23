// il2wasm: libSystem.IO.Compression.Native's entry points (natives.txt), as its pal_zlib.c is over zlib: the
// PAL_ZStream's fields go into the stream and come back after each call. Deflate is zlib-ng's, ported
// (ZLibNgDeflate.cs: the same bytes as the browser runtime's); inflate is ZLibInflate.cs.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2Wasm.Runtime.Natives.Inflate;

namespace Il2Wasm.Runtime.Natives
{
    internal static unsafe class CompressionNative
    {
        /// <summary>pal_zlib.h's PAL_ZStream (pointers are the target's: 4 bytes on wasm32).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PalZStream
        {
            public byte* NextIn, NextOut;
            public byte* Msg;
            public void* InternalState;
            public uint AvailIn, AvailOut;
        }

        /// <summary>What pal_zlib.c's Init callocs: the stream, whose state deflateInit2 or inflateInit2 makes.</summary>
        sealed class ZStream
        {
            public ZngDeflate? Deflate;
            public ManagedInflater? Inflate;
        }

        static ZStream? Of(PalZStream* s) => s->InternalState == null ? null : GCHandle.FromIntPtr((IntPtr)s->InternalState).Target as ZStream;

        static void Init(PalZStream* s) => s->InternalState = (void*)GCHandle.ToIntPtr(GCHandle.Alloc(new ZStream()));

        static void End(PalZStream* s)
        {
            if (s->InternalState == null) return;
            GCHandle.FromIntPtr((IntPtr)s->InternalState).Free();
            s->InternalState = null;
        }

        /// <summary>z_errmsg's texts as C strings, made once each (msg points at them, as at zlib's constants).</summary>
        static readonly Dictionary<string, IntPtr> s_messages = [];

        static byte* Message(string? text)
        {
            if (text == null) return null;
            lock (s_messages)
            {
                if (!s_messages.TryGetValue(text, out var p))
                {
                    p = (IntPtr)NativeMemory.Alloc((nuint)text.Length + 1);
                    for (int i = 0; i < text.Length; i++) ((byte*)p)[i] = (byte)text[i];
                    ((byte*)p)[text.Length] = 0;
                    s_messages[text] = p;
                }
                return (byte*)p;
            }
        }

        static void In(PalZStream* from, ZStreamFields to)
        {
            to.NextIn = from->NextIn; to.AvailIn = from->AvailIn;
            to.NextOut = from->NextOut; to.AvailOut = from->AvailOut;
        }

        static void Out(ZStreamFields from, PalZStream* to)
        {
            to->NextIn = from.NextIn; to->AvailIn = from.AvailIn;
            to->NextOut = from.NextOut; to->AvailOut = from.AvailOut;
            to->Msg = Message(from.Msg);
        }

        public static int CompressionNative_DeflateInit2_(PalZStream* stream, int level, int method, int windowBits, int memLevel, int strategy)
        {
            Init(stream);
            int r = ZngDeflate.Init2(level, method, windowBits, memLevel, strategy, out var d);
            Of(stream)!.Deflate = d;
            if (d != null) Out(d, stream);
            else stream->Msg = null;
            return r;
        }

        public static int CompressionNative_Deflate(PalZStream* stream, int flush)
        {
            var d = Of(stream)?.Deflate;
            if (d == null) return ZConst.Z_STREAM_ERROR;
            In(stream, d);
            int r = d.Deflate(flush);
            Out(d, stream);
            return r;
        }

        public static int CompressionNative_DeflateEnd(PalZStream* stream)
        {
            var d = Of(stream)?.Deflate;
            int r = ZConst.Z_STREAM_ERROR;
            if (d != null)
            {
                In(stream, d);
                r = d.End();
                d.Free();
            }
            End(stream);
            return r;
        }

        public static int CompressionNative_DeflateReset(PalZStream* stream)
        {
            var d = Of(stream)?.Deflate;
            if (d == null) return ZConst.Z_STREAM_ERROR;
            In(stream, d);
            int r = d.Reset();
            Out(d, stream);
            return r;
        }

        public static int CompressionNative_InflateInit2_(PalZStream* stream, int windowBits)
        {
            Init(stream);
            Of(stream)!.Inflate = new ManagedInflater(windowBits);
            stream->Msg = null;
            return ZConst.Z_OK;
        }

        public static int CompressionNative_Inflate(PalZStream* stream, int flush)
        {
            var i = Of(stream)?.Inflate;
            if (i == null) return ZConst.Z_STREAM_ERROR;
            byte* nextIn = stream->NextIn, nextOut = stream->NextOut;
            uint availIn = stream->AvailIn, availOut = stream->AvailOut;
            int r = i.Inflate(&nextIn, &availIn, &nextOut, &availOut);
            stream->NextIn = nextIn; stream->AvailIn = availIn;
            stream->NextOut = nextOut; stream->AvailOut = availOut;
            return r;
        }

        public static int CompressionNative_InflateEnd(PalZStream* stream)
        {
            int r = Of(stream)?.Inflate != null ? ZConst.Z_OK : ZConst.Z_STREAM_ERROR;
            End(stream);
            return r;
        }

        public static int CompressionNative_InflateReset2_(PalZStream* stream, int windowBits)
        {
            var i = Of(stream)?.Inflate;
            if (i == null) return ZConst.Z_STREAM_ERROR;
            i.Reset(windowBits);
            return ZConst.Z_OK;
        }

        public static uint CompressionNative_Crc32(uint crc, byte* buffer, int len) => Crc32.Update(crc, buffer, len);
    }
}
