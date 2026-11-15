// cs2wasm: hashing is computation, not a capability the sandbox lacks: managed SHA-1, SHA-2 and MD5 behind .NET's
// HashAlgorithm and IncrementalHash (the rest of System.Security.Cryptography is declarations, Generated/Stubs.g.cs).
// The algorithms are FIPS 180-4 and RFC 1321.
using System.Buffers.Binary;
using System.IO;

namespace System.Security.Cryptography
{
    public abstract class HashAlgorithm : IDisposable, ICryptoTransform
    {
        protected int HashSizeValue;
        protected internal byte[]? HashValue;
        protected int State;
        bool _disposed;

        public virtual int HashSize => HashSizeValue;
        public virtual byte[]? Hash => (byte[]?)HashValue?.Clone();
        public virtual int InputBlockSize => 1;
        public virtual int OutputBlockSize => 1;
        public virtual bool CanTransformMultipleBlocks => true;
        public virtual bool CanReuseTransform => true;

        public static HashAlgorithm Create(string hashName) => throw new PlatformNotSupportedException();

        public byte[] ComputeHash(byte[] buffer) { ArgumentNullException.ThrowIfNull(buffer); return ComputeHash(buffer, 0, buffer.Length); }

        public byte[] ComputeHash(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ObjectDisposedException.ThrowIf(_disposed, this);
            HashCore(buffer, offset, count);
            HashValue = HashFinal();
            byte[] r = (byte[])HashValue.Clone();
            Initialize();
            return r;
        }

        public byte[] ComputeHash(Stream inputStream)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var buf = new byte[4096];
            int n;
            while ((n = inputStream.Read(buf, 0, buf.Length)) > 0) HashCore(buf, 0, n);
            HashValue = HashFinal();
            byte[] r = (byte[])HashValue.Clone();
            Initialize();
            return r;
        }

        public bool TryComputeHash(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
        {
            HashCore(source.ToArray(), 0, source.Length);
            var h = HashFinal();
            Initialize();
            if (h.Length > destination.Length) { bytesWritten = 0; return false; }
            h.CopyTo(destination);
            bytesWritten = h.Length;
            return true;
        }

        public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[]? outputBuffer, int outputOffset)
        {
            State = 1;
            HashCore(inputBuffer, inputOffset, inputCount);
            if (outputBuffer != null && (inputBuffer != outputBuffer || inputOffset != outputOffset))
                Buffer.BlockCopy(inputBuffer, inputOffset, outputBuffer, outputOffset, inputCount);
            return inputCount;
        }

        public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount)
        {
            HashCore(inputBuffer, inputOffset, inputCount);
            HashValue = HashFinal();
            var r = new byte[inputCount];
            Buffer.BlockCopy(inputBuffer, inputOffset, r, 0, inputCount);
            State = 0;
            Initialize();
            return r;
        }

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        public void Clear() => Dispose();
        protected virtual void Dispose(bool disposing) { if (disposing) { HashValue = null; _disposed = true; } }

        protected abstract void HashCore(byte[] array, int ibStart, int cbSize);
        protected abstract byte[] HashFinal();
        public abstract void Initialize();
    }

    /// <summary>The block-oriented part every Merkle–Damgård hash here shares: buffering into 64- or 128-byte blocks,
    /// and the padding with the bit length (big-endian for SHA, little-endian for MD5).</summary>
    internal abstract class BlockHash
    {
        readonly byte[] _block;
        int _filled;
        ulong _length;
        protected BlockHash(int blockSize) { _block = new byte[blockSize]; }
        protected abstract void Compress(ReadOnlySpan<byte> block);
        protected abstract byte[] Digest();
        protected abstract void ResetState();
        protected virtual bool LittleEndianLength => false;

        public void Reset() { _filled = 0; _length = 0; ResetState(); }

        public void Append(ReadOnlySpan<byte> data)
        {
            _length += (ulong)data.Length;
            while (data.Length > 0)
            {
                int take = Math.Min(_block.Length - _filled, data.Length);
                data[..take].CopyTo(_block.AsSpan(_filled));
                _filled += take;
                data = data[take..];
                if (_filled == _block.Length) { Compress(_block); _filled = 0; }
            }
        }

        public byte[] Finish()
        {
            ulong bits = _length * 8;
            int lenBytes = _block.Length == 128 ? 16 : 8;
            Span<byte> pad = stackalloc byte[_block.Length * 2];
            pad.Clear();
            pad[0] = 0x80;
            int padLen = _block.Length - ((_filled + 1 + lenBytes) % _block.Length);
            if (padLen == _block.Length) padLen = 0;
            int total = 1 + padLen + lenBytes;
            var tail = pad[..total];
            if (LittleEndianLength) BinaryPrimitives.WriteUInt64LittleEndian(tail[(total - 8)..], bits);
            else BinaryPrimitives.WriteUInt64BigEndian(tail[(total - 8)..], bits);
            ulong saved = _length;
            Append(tail);
            _length = saved;
            var d = Digest();
            Reset();
            return d;
        }
    }

    internal sealed class Sha1Engine : BlockHash
    {
        readonly uint[] _h = new uint[5];
        readonly uint[] _w = new uint[80];
        public Sha1Engine() : base(64) => ResetState();
        protected override void ResetState() { _h[0] = 0x67452301; _h[1] = 0xEFCDAB89; _h[2] = 0x98BADCFE; _h[3] = 0x10325476; _h[4] = 0xC3D2E1F0; }
        protected override void Compress(ReadOnlySpan<byte> b)
        {
            var w = _w;
            for (int i = 0; i < 16; i++) w[i] = BinaryPrimitives.ReadUInt32BigEndian(b[(4 * i)..]);
            for (int i = 16; i < 80; i++) w[i] = uint.RotateLeft(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
            uint a = _h[0], bb = _h[1], c = _h[2], d = _h[3], e = _h[4];
            for (int i = 0; i < 80; i++)
            {
                uint f, k;
                if (i < 20) { f = (bb & c) | (~bb & d); k = 0x5A827999; }
                else if (i < 40) { f = bb ^ c ^ d; k = 0x6ED9EBA1; }
                else if (i < 60) { f = (bb & c) | (bb & d) | (c & d); k = 0x8F1BBCDC; }
                else { f = bb ^ c ^ d; k = 0xCA62C1D6; }
                uint t = uint.RotateLeft(a, 5) + f + e + k + w[i];
                e = d; d = c; c = uint.RotateLeft(bb, 30); bb = a; a = t;
            }
            _h[0] += a; _h[1] += bb; _h[2] += c; _h[3] += d; _h[4] += e;
        }
        protected override byte[] Digest()
        {
            var r = new byte[20];
            for (int i = 0; i < 5; i++) BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4 * i), _h[i]);
            return r;
        }
    }

    internal sealed class Sha256Engine : BlockHash
    {
        static readonly uint[] K =
        [
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3,
            0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13,
            0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
            0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
        ];
        readonly uint[] _h = new uint[8];
        readonly uint[] _w = new uint[64];
        public Sha256Engine() : base(64) => ResetState();
        protected override void ResetState()
        {
            _h[0] = 0x6a09e667; _h[1] = 0xbb67ae85; _h[2] = 0x3c6ef372; _h[3] = 0xa54ff53a;
            _h[4] = 0x510e527f; _h[5] = 0x9b05688c; _h[6] = 0x1f83d9ab; _h[7] = 0x5be0cd19;
        }
        protected override void Compress(ReadOnlySpan<byte> b)
        {
            var w = _w;
            for (int i = 0; i < 16; i++) w[i] = BinaryPrimitives.ReadUInt32BigEndian(b[(4 * i)..]);
            for (int i = 16; i < 64; i++)
            {
                uint s0 = uint.RotateRight(w[i - 15], 7) ^ uint.RotateRight(w[i - 15], 18) ^ (w[i - 15] >> 3);
                uint s1 = uint.RotateRight(w[i - 2], 17) ^ uint.RotateRight(w[i - 2], 19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }
            uint a = _h[0], bb = _h[1], c = _h[2], d = _h[3], e = _h[4], f = _h[5], g = _h[6], h = _h[7];
            for (int i = 0; i < 64; i++)
            {
                uint S1 = uint.RotateRight(e, 6) ^ uint.RotateRight(e, 11) ^ uint.RotateRight(e, 25);
                uint ch = (e & f) ^ (~e & g);
                uint t1 = h + S1 + ch + K[i] + w[i];
                uint S0 = uint.RotateRight(a, 2) ^ uint.RotateRight(a, 13) ^ uint.RotateRight(a, 22);
                uint maj = (a & bb) ^ (a & c) ^ (bb & c);
                uint t2 = S0 + maj;
                h = g; g = f; f = e; e = d + t1; d = c; c = bb; bb = a; a = t1 + t2;
            }
            _h[0] += a; _h[1] += bb; _h[2] += c; _h[3] += d; _h[4] += e; _h[5] += f; _h[6] += g; _h[7] += h;
        }
        protected override byte[] Digest()
        {
            var r = new byte[32];
            for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4 * i), _h[i]);
            return r;
        }
    }

    internal sealed class Sha512Engine : BlockHash
    {
        static readonly ulong[] K =
        [
            0x428a2f98d728ae22, 0x7137449123ef65cd, 0xb5c0fbcfec4d3b2f, 0xe9b5dba58189dbbc, 0x3956c25bf348b538, 0x59f111f1b605d019, 0x923f82a4af194f9b, 0xab1c5ed5da6d8118,
            0xd807aa98a3030242, 0x12835b0145706fbe, 0x243185be4ee4b28c, 0x550c7dc3d5ffb4e2, 0x72be5d74f27b896f, 0x80deb1fe3b1696b1, 0x9bdc06a725c71235, 0xc19bf174cf692694,
            0xe49b69c19ef14ad2, 0xefbe4786384f25e3, 0x0fc19dc68b8cd5b5, 0x240ca1cc77ac9c65, 0x2de92c6f592b0275, 0x4a7484aa6ea6e483, 0x5cb0a9dcbd41fbd4, 0x76f988da831153b5,
            0x983e5152ee66dfab, 0xa831c66d2db43210, 0xb00327c898fb213f, 0xbf597fc7beef0ee4, 0xc6e00bf33da88fc2, 0xd5a79147930aa725, 0x06ca6351e003826f, 0x142929670a0e6e70,
            0x27b70a8546d22ffc, 0x2e1b21385c26c926, 0x4d2c6dfc5ac42aed, 0x53380d139d95b3df, 0x650a73548baf63de, 0x766a0abb3c77b2a8, 0x81c2c92e47edaee6, 0x92722c851482353b,
            0xa2bfe8a14cf10364, 0xa81a664bbc423001, 0xc24b8b70d0f89791, 0xc76c51a30654be30, 0xd192e819d6ef5218, 0xd69906245565a910, 0xf40e35855771202a, 0x106aa07032bbd1b8,
            0x19a4c116b8d2d0c8, 0x1e376c085141ab53, 0x2748774cdf8eeb99, 0x34b0bcb5e19b48a8, 0x391c0cb3c5c95a63, 0x4ed8aa4ae3418acb, 0x5b9cca4f7763e373, 0x682e6ff3d6b2b8a3,
            0x748f82ee5defb2fc, 0x78a5636f43172f60, 0x84c87814a1f0ab72, 0x8cc702081a6439ec, 0x90befffa23631e28, 0xa4506cebde82bde9, 0xbef9a3f7b2c67915, 0xc67178f2e372532b,
            0xca273eceea26619c, 0xd186b8c721c0c207, 0xeada7dd6cde0eb1e, 0xf57d4f7fee6ed178, 0x06f067aa72176fba, 0x0a637dc5a2c898a6, 0x113f9804bef90dae, 0x1b710b35131c471b,
            0x28db77f523047d84, 0x32caab7b40c72493, 0x3c9ebe0a15c9bebc, 0x431d67c49c100d4c, 0x4cc5d4becb3e42b6, 0x597f299cfc657e2a, 0x5fcb6fab3ad6faec, 0x6c44198c4a475817,
        ];
        readonly ulong[] _h = new ulong[8];
        readonly ulong[] _w = new ulong[80];
        readonly ulong[] _iv;
        readonly int _size;
        public Sha512Engine(ulong[] iv, int size) : base(128) { _iv = iv; _size = size; ResetState(); }
        public static readonly ulong[] Iv512 = [0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1, 0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179];
        public static readonly ulong[] Iv384 = [0xcbbb9d5dc1059ed8, 0x629a292a367cd507, 0x9159015a3070dd17, 0x152fecd8f70e5939, 0x67332667ffc00b31, 0x8eb44a8768581511, 0xdb0c2e0d64f98fa7, 0x47b5481dbefa4fa4];
        protected override void ResetState() => Array.Copy(_iv, _h, 8);
        protected override void Compress(ReadOnlySpan<byte> b)
        {
            var w = _w;
            for (int i = 0; i < 16; i++) w[i] = BinaryPrimitives.ReadUInt64BigEndian(b[(8 * i)..]);
            for (int i = 16; i < 80; i++)
            {
                ulong s0 = ulong.RotateRight(w[i - 15], 1) ^ ulong.RotateRight(w[i - 15], 8) ^ (w[i - 15] >> 7);
                ulong s1 = ulong.RotateRight(w[i - 2], 19) ^ ulong.RotateRight(w[i - 2], 61) ^ (w[i - 2] >> 6);
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }
            ulong a = _h[0], bb = _h[1], c = _h[2], d = _h[3], e = _h[4], f = _h[5], g = _h[6], h = _h[7];
            for (int i = 0; i < 80; i++)
            {
                ulong S1 = ulong.RotateRight(e, 14) ^ ulong.RotateRight(e, 18) ^ ulong.RotateRight(e, 41);
                ulong ch = (e & f) ^ (~e & g);
                ulong t1 = h + S1 + ch + K[i] + w[i];
                ulong S0 = ulong.RotateRight(a, 28) ^ ulong.RotateRight(a, 34) ^ ulong.RotateRight(a, 39);
                ulong maj = (a & bb) ^ (a & c) ^ (bb & c);
                ulong t2 = S0 + maj;
                h = g; g = f; f = e; e = d + t1; d = c; c = bb; bb = a; a = t1 + t2;
            }
            _h[0] += a; _h[1] += bb; _h[2] += c; _h[3] += d; _h[4] += e; _h[5] += f; _h[6] += g; _h[7] += h;
        }
        protected override byte[] Digest()
        {
            var full = new byte[64];
            for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt64BigEndian(full.AsSpan(8 * i), _h[i]);
            return full.AsSpan(0, _size).ToArray();
        }
    }

    internal sealed class Md5Engine : BlockHash
    {
        static readonly uint[] T = MakeT();
        static readonly int[] S = [7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
            4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21];
        static uint[] MakeT()
        {
            // (the constants are floor(abs(sin(i + 1)) * 2^32), written out to stay independent of the math library)
            return [
                0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501, 0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be,
                0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821, 0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
                0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a, 0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c,
                0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70, 0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
                0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1, 0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1,
                0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
            ];
        }
        readonly uint[] _h = new uint[4];
        public Md5Engine() : base(64) => ResetState();
        protected override bool LittleEndianLength => true;
        protected override void ResetState() { _h[0] = 0x67452301; _h[1] = 0xefcdab89; _h[2] = 0x98badcfe; _h[3] = 0x10325476; }
        protected override void Compress(ReadOnlySpan<byte> b)
        {
            Span<uint> m = stackalloc uint[16];
            for (int i = 0; i < 16; i++) m[i] = BinaryPrimitives.ReadUInt32LittleEndian(b[(4 * i)..]);
            uint a = _h[0], bb = _h[1], c = _h[2], d = _h[3];
            for (int i = 0; i < 64; i++)
            {
                uint f; int g;
                if (i < 16) { f = (bb & c) | (~bb & d); g = i; }
                else if (i < 32) { f = (d & bb) | (~d & c); g = (5 * i + 1) % 16; }
                else if (i < 48) { f = bb ^ c ^ d; g = (3 * i + 5) % 16; }
                else { f = c ^ (bb | ~d); g = (7 * i) % 16; }
                uint t = d; d = c; c = bb;
                bb = bb + uint.RotateLeft(a + f + T[i] + m[g], S[i]);
                a = t;
            }
            _h[0] += a; _h[1] += bb; _h[2] += c; _h[3] += d;
        }
        protected override byte[] Digest()
        {
            var r = new byte[16];
            for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4 * i), _h[i]);
            return r;
        }
    }

    public readonly struct HashAlgorithmName : IEquatable<HashAlgorithmName>
    {
        public HashAlgorithmName(string? name) { Name = name; }
        public string? Name { get; }
        public static HashAlgorithmName MD5 => new("MD5");
        public static HashAlgorithmName SHA1 => new("SHA1");
        public static HashAlgorithmName SHA256 => new("SHA256");
        public static HashAlgorithmName SHA384 => new("SHA384");
        public static HashAlgorithmName SHA512 => new("SHA512");
        public static HashAlgorithmName SHA3_256 => new("SHA3-256");
        public static HashAlgorithmName SHA3_384 => new("SHA3-384");
        public static HashAlgorithmName SHA3_512 => new("SHA3-512");
        public override string ToString() => Name ?? string.Empty;
        public override bool Equals(object? obj) => obj is HashAlgorithmName n && Equals(n);
        public bool Equals(HashAlgorithmName other) => Name == other.Name;
        public override int GetHashCode() => Name?.GetHashCode() ?? 0;
        public static bool operator ==(HashAlgorithmName left, HashAlgorithmName right) => left.Equals(right);
        public static bool operator !=(HashAlgorithmName left, HashAlgorithmName right) => !(left == right);
    }

    public abstract class SHA1 : HashAlgorithm
    {
        public const int HashSizeInBits = 160, HashSizeInBytes = 20;
        protected SHA1() { HashSizeValue = 160; }
        public static new SHA1 Create() => new Impl();
        public static byte[] HashData(byte[] source) => HashData(new ReadOnlySpan<byte>(source));
        public static byte[] HashData(ReadOnlySpan<byte> source) { var e = new Sha1Engine(); e.Append(source); return e.Finish(); }
        sealed class Impl : SHA1
        {
            readonly Sha1Engine _e = new();
            protected override void HashCore(byte[] array, int ibStart, int cbSize) => _e.Append(array.AsSpan(ibStart, cbSize));
            protected override byte[] HashFinal() => _e.Finish();
            public override void Initialize() => _e.Reset();
        }
    }

    public abstract class SHA256 : HashAlgorithm
    {
        public const int HashSizeInBits = 256, HashSizeInBytes = 32;
        protected SHA256() { HashSizeValue = 256; }
        public static new SHA256 Create() => new Impl();
        public static byte[] HashData(byte[] source) => HashData(new ReadOnlySpan<byte>(source));
        public static byte[] HashData(ReadOnlySpan<byte> source) { var e = new Sha256Engine(); e.Append(source); return e.Finish(); }
        sealed class Impl : SHA256
        {
            readonly Sha256Engine _e = new();
            protected override void HashCore(byte[] array, int ibStart, int cbSize) => _e.Append(array.AsSpan(ibStart, cbSize));
            protected override byte[] HashFinal() => _e.Finish();
            public override void Initialize() => _e.Reset();
        }
    }

    public abstract class SHA384 : HashAlgorithm
    {
        public const int HashSizeInBits = 384, HashSizeInBytes = 48;
        protected SHA384() { HashSizeValue = 384; }
        public static new SHA384 Create() => new Impl();
        public static byte[] HashData(byte[] source) => HashData(new ReadOnlySpan<byte>(source));
        public static byte[] HashData(ReadOnlySpan<byte> source) { var e = new Sha512Engine(Sha512Engine.Iv384, 48); e.Append(source); return e.Finish(); }
        sealed class Impl : SHA384
        {
            readonly Sha512Engine _e = new(Sha512Engine.Iv384, 48);
            protected override void HashCore(byte[] array, int ibStart, int cbSize) => _e.Append(array.AsSpan(ibStart, cbSize));
            protected override byte[] HashFinal() => _e.Finish();
            public override void Initialize() => _e.Reset();
        }
    }

    public abstract class SHA512 : HashAlgorithm
    {
        public const int HashSizeInBits = 512, HashSizeInBytes = 64;
        protected SHA512() { HashSizeValue = 512; }
        public static new SHA512 Create() => new Impl();
        public static byte[] HashData(byte[] source) => HashData(new ReadOnlySpan<byte>(source));
        public static byte[] HashData(ReadOnlySpan<byte> source) { var e = new Sha512Engine(Sha512Engine.Iv512, 64); e.Append(source); return e.Finish(); }
        sealed class Impl : SHA512
        {
            readonly Sha512Engine _e = new(Sha512Engine.Iv512, 64);
            protected override void HashCore(byte[] array, int ibStart, int cbSize) => _e.Append(array.AsSpan(ibStart, cbSize));
            protected override byte[] HashFinal() => _e.Finish();
            public override void Initialize() => _e.Reset();
        }
    }

    public abstract class MD5 : HashAlgorithm
    {
        public const int HashSizeInBits = 128, HashSizeInBytes = 16;
        protected MD5() { HashSizeValue = 128; }
        public static new MD5 Create() => new Impl();
        public static byte[] HashData(byte[] source) => HashData(new ReadOnlySpan<byte>(source));
        public static byte[] HashData(ReadOnlySpan<byte> source) { var e = new Md5Engine(); e.Append(source); return e.Finish(); }
        sealed class Impl : MD5
        {
            readonly Md5Engine _e = new();
            protected override void HashCore(byte[] array, int ibStart, int cbSize) => _e.Append(array.AsSpan(ibStart, cbSize));
            protected override byte[] HashFinal() => _e.Finish();
            public override void Initialize() => _e.Reset();
        }
    }

    public sealed class IncrementalHash : IDisposable
    {
        readonly BlockHash _e;
        IncrementalHash(HashAlgorithmName name, BlockHash e) { AlgorithmName = name; _e = e; }
        public HashAlgorithmName AlgorithmName { get; }
        public int HashLengthInBytes => AlgorithmName.Name switch { "MD5" => 16, "SHA1" => 20, "SHA256" => 32, "SHA384" => 48, _ => 64 };

        public static IncrementalHash CreateHash(HashAlgorithmName hashAlgorithm) => new(hashAlgorithm, hashAlgorithm.Name switch
        {
            "MD5" => new Md5Engine(),
            "SHA1" => new Sha1Engine(),
            "SHA256" => new Sha256Engine(),
            "SHA384" => new Sha512Engine(Sha512Engine.Iv384, 48),
            "SHA512" => new Sha512Engine(Sha512Engine.Iv512, 64),
            _ => throw new CryptographicException(string.Format("'{0}' is not a known hash algorithm.", hashAlgorithm.Name)),
        });

        public void AppendData(byte[] data) => _e.Append(data);
        public void AppendData(byte[] data, int offset, int count) => _e.Append(data.AsSpan(offset, count));
        public void AppendData(ReadOnlySpan<byte> data) => _e.Append(data);
        public byte[] GetHashAndReset() => _e.Finish();
        public int GetHashAndReset(Span<byte> destination) { var h = _e.Finish(); h.CopyTo(destination); return h.Length; }
        public bool TryGetHashAndReset(Span<byte> destination, out int bytesWritten)
        {
            var h = _e.Finish();
            if (h.Length > destination.Length) { bytesWritten = 0; return false; }
            h.CopyTo(destination); bytesWritten = h.Length; return true;
        }
        public void Dispose() { }
    }
}
