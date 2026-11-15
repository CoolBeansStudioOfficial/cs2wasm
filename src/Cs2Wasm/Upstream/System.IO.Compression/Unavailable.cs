// cs2wasm: deflate in .NET is native zlib (System.IO.Compression.Native), which the module does not have (section 8).
// These are the members the libraries above name; a program that reaches one fails to build.
using System;
using System.IO;
using Cs2Wasm.Runtime;

namespace System.IO.Compression
{
    public enum CompressionLevel { Optimal = 0, Fastest = 1, NoCompression = 2, SmallestSize = 3 }
    public enum CompressionMode { Decompress = 0, Compress = 1 }

    public class DeflateStream : Stream
    {
        [MissingCapability("deflate compression (native zlib)")]
        public DeflateStream(Stream stream, CompressionLevel compressionLevel, bool leaveOpen) { }
        [MissingCapability("deflate compression (native zlib)")]
        public DeflateStream(Stream stream, CompressionMode mode, bool leaveOpen) { }
        [MissingCapability("deflate compression (native zlib)")]
        public DeflateStream(Stream stream, CompressionMode mode) { }
        [MissingCapability("deflate compression (native zlib)")]
        public DeflateStream(Stream stream, CompressionLevel compressionLevel) { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
