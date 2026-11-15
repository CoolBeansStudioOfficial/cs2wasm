using System.IO;
using System.Text;
using Cs2Wasm.Runtime;

namespace System
{
    /// <summary>
    /// The console's runtime boundary (SPEC 7.4: Console output goes to the engine's log import). Console.Out and
    /// Console.Error are synchronized writers that hand their UTF-16 text to the engine as it is written, with
    /// nothing to encode, buffer or flush; Console.OpenStandardOutput/OpenStandardError are byte streams whose UTF-8
    /// the engine receives as text. (Manifest substitutions of Console.CreateOutputWriter and ConsolePal's
    /// OpenStandardOutput/OpenStandardError.)
    /// </summary>
    internal static class Cs2WasmConsole
    {
        public static TextWriter CreateOutputWriter(Stream outputStream) =>
            outputStream == Stream.Null ? TextWriter.Null : TextWriter.Synchronized(new EngineTextWriter());

        public static Stream OpenStandardOutput() => new EngineStream();
        public static Stream OpenStandardError() => new EngineStream();
    }

    internal sealed unsafe class EngineTextWriter : TextWriter
    {
        public override Encoding Encoding => Console.OutputEncoding;

        public override void Write(char value) => Engine.Write((int)&value, 1);

        public override void Write(ReadOnlySpan<char> buffer)
        {
            if (buffer.IsEmpty) return;
            fixed (char* p = buffer) Engine.Write((int)p, buffer.Length);
        }

        public override void Write(char[] buffer, int index, int count) => Write(new ReadOnlySpan<char>(buffer, index, count));

        public override void Write(char[]? buffer)
        {
            if (buffer != null) Write(new ReadOnlySpan<char>(buffer));
        }

        public override void Write(string? value)
        {
            if (value != null) Write(value.AsSpan());
        }

        public override void WriteLine() => Write(CoreNewLine);

        public override void WriteLine(string? value)
        {
            Write(value);
            Write(CoreNewLine);
        }

        public override void WriteLine(ReadOnlySpan<char> buffer)
        {
            Write(buffer);
            Write(CoreNewLine);
        }
    }

    /// <summary>A write-only byte stream whose UTF-8 goes to the engine as text (a sequence split across writes is
    /// completed by the next write; malformed bytes become U+FFFD).</summary>
    internal sealed unsafe class EngineStream : Stream
    {
        int _pending, _need, _min;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            Write(new ReadOnlySpan<byte>(buffer, offset, count));
        }

        public override void Write(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty) return;
            char[] text = new char[bytes.Length + 1];
            int n = 0;
            foreach (byte b in bytes)
            {
                if (_need > 0)
                {
                    if ((b & 0xC0) == 0x80)
                    {
                        _pending = (_pending << 6) | (b & 0x3F);
                        if (--_need == 0) n = Put(text, n, _pending);
                        continue;
                    }
                    text[n++] = '�';
                    _need = 0;
                }
                if (b < 0x80) text[n++] = (char)b;
                else if ((b & 0xE0) == 0xC0) { _pending = b & 0x1F; _need = 1; _min = 0x80; }
                else if ((b & 0xF0) == 0xE0) { _pending = b & 0x0F; _need = 2; _min = 0x800; }
                else if ((b & 0xF8) == 0xF0) { _pending = b & 0x07; _need = 3; _min = 0x10000; }
                else text[n++] = '�';
                if (n >= text.Length - 2) { Flush(text, n); n = 0; }
            }
            Flush(text, n);
        }

        int Put(char[] text, int n, int cp)
        {
            if (cp < _min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) { text[n++] = '�'; return n; }
            if (cp < 0x10000) { text[n++] = (char)cp; return n; }
            cp -= 0x10000;
            text[n++] = (char)(0xD800 + (cp >> 10));
            text[n++] = (char)(0xDC00 + (cp & 0x3FF));
            return n;
        }

        static void Flush(char[] text, int n)
        {
            if (n == 0) return;
            fixed (char* p = text) Engine.Write((int)p, n);
        }
    }
}
