// il2wasm: deflate and inflate in C# (RFC 1950, 1951, 1952). .NET's System.IO.Compression calls native zlib; a module
// has no native libraries (SPEC section 8), so the functions upstream declares as platform invokes (Interop.ZLib) are
// these. The streams are valid deflate, zlib and gzip streams that any inflater reads; the compressed bytes are not
// the ones native zlib would have chosen (nor are .NET's own the same from one version or platform to the next).
using System;

namespace Il2Wasm.Compression
{
    internal static unsafe class Checksums
    {
        static readonly uint[] s_crc = MakeTable();
        static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Crc32(uint crc, byte* p, int len)
        {
            var t = s_crc;
            crc = ~crc;
            for (int i = 0; i < len; i++) crc = t[(crc ^ p[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        public static uint Adler32(uint adler, byte* p, int len)
        {
            uint a = adler & 0xFFFF, b = adler >> 16;
            while (len > 0)
            {
                int n = len < 5552 ? len : 5552;
                len -= n;
                while (n-- > 0) { a += *p++; b += a; }
                a %= 65521; b %= 65521;
            }
            return (b << 16) | a;
        }
    }

    internal static class Tables
    {
        public static readonly ushort[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
        public static readonly byte[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
        public static readonly ushort[] DistBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
        public static readonly byte[] DistExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
        public static readonly byte[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
        public static readonly byte[] FixedLitLen = MakeFixedLitLen();
        public static readonly byte[] FixedDist = MakeFixedDist();
        static byte[] MakeFixedLitLen()
        {
            var l = new byte[288];
            for (int i = 0; i < 288; i++) l[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
            return l;
        }
        static byte[] MakeFixedDist() { var l = new byte[32]; for (int i = 0; i < 32; i++) l[i] = 5; return l; } // (32 codes of 5 bits: the last two stand for no distance)
    }

    /// <summary>A canonical Huffman code for decoding: codes of up to 9 bits by table, longer ones bit by bit.</summary>
    internal sealed class DecodeTable
    {
        const int FastBits = 9;
        readonly ushort[] _fast = new ushort[1 << FastBits]; // (symbol &lt;&lt; 4) | length, 0: not here
        readonly ushort[] _count = new ushort[16];
        readonly ushort[] _symbol;
        public bool Valid;

        public DecodeTable(int maxSymbols) { _symbol = new ushort[maxSymbols]; }

        /// <summary>Builds from code lengths; false when the lengths are no code (oversubscribed, or incomplete
        /// with more than one symbol).</summary>
        public bool Build(byte[] lengths, int offset, int n)
        {
            Array.Clear(_fast);
            Array.Clear(_count);
            for (int i = 0; i < n; i++) _count[lengths[offset + i]]++;
            _count[0] = 0;
            int left = 1, used = 0;
            for (int len = 1; len <= 15; len++)
            {
                left <<= 1;
                left -= _count[len];
                if (left < 0) return Valid = false;
                used += _count[len];
            }
            if (left > 0 && used > 1) return Valid = false;
            Span<ushort> offs = stackalloc ushort[16];
            offs[1] = 0;
            for (int len = 1; len < 15; len++) offs[len + 1] = (ushort)(offs[len] + _count[len]);
            for (int i = 0; i < n; i++)
                if (lengths[offset + i] != 0) _symbol[offs[lengths[offset + i]]++] = (ushort)i;
            // the table of short codes, by the bits as they arrive (the code reversed)
            int code = 0, index = 0;
            for (int len = 1; len <= FastBits; len++)
            {
                for (int k = 0; k < _count[len]; k++, code++, index++)
                {
                    int rev = 0;
                    for (int b = 0; b < len; b++) rev |= ((code >> b) & 1) << (len - 1 - b);
                    ushort entry = (ushort)((_symbol[index] << 4) | len);
                    for (int j = rev; j < (1 << FastBits); j += 1 << len) _fast[j] = entry;
                }
                code <<= 1;
            }
            return Valid = true;
        }

        /// <summary>The symbol at the low end of `hold` (of which `bits` bits are there) and its length, as
        /// (symbol &lt;&lt; 4) | length; -1 when more bits are needed, -2 when the bits are no code.</summary>
        public int Decode(uint hold, int bits)
        {
            int e = _fast[hold & ((1 << FastBits) - 1)];
            if (e != 0) return (e & 15) <= bits ? e : -1;
            int code = 0, first = 0, index = 0;
            for (int len = 1; len <= 15; len++)
            {
                if (len > bits) return -1;
                code |= (int)(hold & 1);
                hold >>= 1;
                int count = _count[len];
                if (code - count < first) return (_symbol[index + (code - first)] << 4) | len;
                index += count;
                first += count;
                first <<= 1;
                code <<= 1;
            }
            return -2;
        }
    }

    internal sealed unsafe class ManagedInflater
    {
        const int Ok = 0, StreamEnd = 1, DataError = -3, BufError = -5;
        const int WSize = 32768, WMask = WSize - 1;
        enum S { Head, GzId, GzFlags, GzTime, GzXfl, GzExtraLen, GzExtra, GzName, GzComment, GzHcrc, BlockHead, StoredLen, StoredCopy, Table, TableLens, TableCodes, Len, Match, Check, CheckLen, Done, Bad }

        int _wrap; // 0 raw, 1 zlib, 2 gzip, 3 zlib or gzip (by the first bytes)
        S _state;
        uint _hold; int _bits;
        bool _last;
        readonly byte[] _window = new byte[WSize];
        int _wpos, _wfill;
        uint _check; uint _total;
        int _gzFlags, _skip;
        int _stored;
        int _hlit, _hdist, _hclen, _index;
        readonly byte[] _lens = new byte[320];
        readonly byte[] _clens = new byte[19];
        readonly DecodeTable _codeLen = new(19), _litLen = new(288), _dist = new(30);
        static readonly DecodeTable s_fixedLitLen = MakeFixed(Tables.FixedLitLen, 288), s_fixedDist = MakeFixed(Tables.FixedDist, 32);
        DecodeTable _lit = null!, _dst = null!;
        int _length, _distance;

        static DecodeTable MakeFixed(byte[] lens, int n) { var t = new DecodeTable(n); t.Build(lens, 0, n); return t; }

        public ManagedInflater(int windowBits) { Reset(windowBits); }

        public void Reset(int windowBits)
        {
            _wrap = windowBits < 0 ? 0 : windowBits >= 32 ? 3 : windowBits >= 16 ? 2 : 1;
            _state = S.Head; _hold = 0; _bits = 0; _last = false; _wpos = 0; _wfill = 0; _total = 0;
            _check = _wrap == 1 ? 1u : 0u;
        }

        public int Inflate(byte** nextIn, uint* availIn, byte** nextOut, uint* availOut)
        {
            byte* inp = *nextIn; uint have = *availIn;
            byte* outp = *nextOut; uint room = *availOut;
            byte* outStart = outp; uint haveStart = have;
            int result = Ok;
            uint hold = _hold; int bits = _bits;
            var window = _window; int wpos = _wpos;

            while (true)
            {
                // (as many whole bytes as fit and are there; none past the end of the stream: they are the caller's)
                if (_state < S.Done && !(_state == S.Check && _wrap == 0))
                    while (bits <= 24 && have > 0) { hold |= (uint)*inp++ << bits; bits += 8; have--; }
                switch (_state)
                {
                    case S.Head:
                        if (_wrap == 0) { _state = S.BlockHead; continue; }
                        if (bits < 16) goto leave;
                        if (_wrap >= 2 && (hold & 0xFFFF) == 0x8B1F) { _wrap = 2; _check = 0; _state = S.GzId; continue; }
                        if (_wrap == 2) goto bad;
                        {
                            uint cmf = hold & 0xFF, flg = (hold >> 8) & 0xFF;
                            if ((cmf & 15) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0) goto bad;
                            hold >>= 16; bits -= 16;
                            _wrap = 1; _check = 1;
                            _state = S.BlockHead;
                            continue;
                        }
                    case S.GzId:
                        if (bits < 16) goto leave;
                        hold >>= 16; bits -= 16; _state = S.GzFlags; continue;
                    case S.GzFlags:
                        if (bits < 16) goto leave;
                        if ((hold & 0xFF) != 8 || (hold & 0xE000) != 0) goto bad;
                        _gzFlags = (int)(hold >> 8) & 0xFF;
                        hold >>= 16; bits -= 16; _state = S.GzTime; continue;
                    case S.GzTime:
                        if (bits < 32) goto leave;
                        hold = 0; bits = 0; _state = S.GzXfl; continue;
                    case S.GzXfl:
                        if (bits < 16) goto leave;
                        hold >>= 16; bits -= 16; _state = (_gzFlags & 4) != 0 ? S.GzExtraLen : S.GzName; continue;
                    case S.GzExtraLen:
                        if (bits < 16) goto leave;
                        _skip = (int)(hold & 0xFFFF);
                        hold >>= 16; bits -= 16; _state = S.GzExtra; continue;
                    case S.GzExtra:
                        while (_skip > 0) { if (bits < 8) goto leave; hold >>= 8; bits -= 8; _skip--; if (bits <= 24 && have > 0) { hold |= (uint)*inp++ << bits; bits += 8; have--; } }
                        _state = S.GzName; continue;
                    case S.GzName:
                    case S.GzComment:
                        {
                            int flag = _state == S.GzName ? 8 : 16;
                            if ((_gzFlags & flag) != 0)
                            {
                                while (true)
                                {
                                    if (bits < 8) goto leave;
                                    uint c = hold & 0xFF;
                                    hold >>= 8; bits -= 8;
                                    if (c == 0) break;
                                    if (bits <= 24 && have > 0) { hold |= (uint)*inp++ << bits; bits += 8; have--; }
                                }
                            }
                            _state = _state == S.GzName ? S.GzComment : S.GzHcrc;
                            continue;
                        }
                    case S.GzHcrc:
                        if ((_gzFlags & 2) != 0) { if (bits < 16) goto leave; hold >>= 16; bits -= 16; }
                        _state = S.BlockHead; continue;

                    case S.BlockHead:
                        if (_last)
                        {
                            // the bytes taken ahead that the deflate data does not use go back
                            hold >>= bits & 7; bits -= bits & 7;
                            int back = bits >> 3, pulled = (int)(haveStart - have);
                            if (back > pulled) back = pulled;
                            inp -= back; have += (uint)back; bits -= back * 8;
                            if (bits < 32) hold &= (1u << bits) - 1;
                            _state = S.Check;
                            continue;
                        }
                        if (bits < 3) goto leave;
                        _last = (hold & 1) != 0;
                        switch ((hold >> 1) & 3)
                        {
                            case 0:
                                hold >>= 3; bits -= 3;
                                hold >>= bits & 7; bits -= bits & 7;
                                _state = S.StoredLen;
                                break;
                            case 1:
                                hold >>= 3; bits -= 3;
                                _lit = s_fixedLitLen; _dst = s_fixedDist;
                                _state = S.Len;
                                break;
                            case 2:
                                hold >>= 3; bits -= 3;
                                _state = S.Table;
                                break;
                            default: goto bad;
                        }
                        continue;
                    case S.StoredLen:
                        if (bits < 32) goto leave;
                        if ((hold & 0xFFFF) != ((hold >> 16) ^ 0xFFFF)) goto bad;
                        _stored = (int)(hold & 0xFFFF);
                        hold = 0; bits = 0;
                        _state = S.StoredCopy;
                        continue;
                    case S.StoredCopy:
                        {
                            // (nothing is held: the length took the 32 bits there were)
                            while (bits > 0 && _stored > 0 && room > 0)
                            {
                                byte b = (byte)hold; hold >>= 8; bits -= 8;
                                *outp++ = b; window[wpos] = b; wpos = (wpos + 1) & WMask; room--; _stored--;
                            }
                            if (bits == 0)
                            {
                                uint n = (uint)_stored;
                                if (n > have) n = have;
                                if (n > room) n = room;
                                for (uint i = 0; i < n; i++) { byte b = inp[i]; outp[i] = b; window[wpos] = b; wpos = (wpos + 1) & WMask; }
                                inp += n; have -= n; outp += n; room -= n; _stored -= (int)n;
                            }
                            if (_stored == 0) { _state = S.BlockHead; continue; }
                            goto leave;
                        }
                    case S.Table:
                        if (bits < 14) goto leave;
                        _hlit = (int)(hold & 31) + 257; _hdist = (int)((hold >> 5) & 31) + 1; _hclen = (int)((hold >> 10) & 15) + 4;
                        hold >>= 14; bits -= 14;
                        if (_hlit > 286 || _hdist > 30) goto bad;
                        Array.Clear(_clens);
                        _index = 0;
                        _state = S.TableLens;
                        continue;
                    case S.TableLens:
                        while (_index < _hclen)
                        {
                            if (bits < 3) { if (have == 0) goto leave; hold |= (uint)*inp++ << bits; bits += 8; have--; }
                            _clens[Tables.CodeLengthOrder[_index++]] = (byte)(hold & 7);
                            hold >>= 3; bits -= 3;
                        }
                        if (!_codeLen.Build(_clens, 0, 19)) goto bad;
                        _index = 0;
                        _state = S.TableCodes;
                        continue;
                    case S.TableCodes:
                        while (_index < _hlit + _hdist)
                        {
                            while (bits <= 24 && have > 0) { hold |= (uint)*inp++ << bits; bits += 8; have--; }
                            int e = _codeLen.Decode(hold, bits);
                            if (e == -1) goto leave;
                            if (e < 0) goto bad;
                            int sym = e >> 4, len = e & 15;
                            if (sym < 16) { hold >>= len; bits -= len; _lens[_index++] = (byte)sym; continue; }
                            int extra = sym == 16 ? 2 : sym == 17 ? 3 : 7;
                            if (bits < len + extra) goto leave;
                            hold >>= len; bits -= len;
                            int repeat = (int)(hold & ((1u << extra) - 1)) + (sym == 18 ? 11 : 3);
                            hold >>= extra; bits -= extra;
                            byte value = 0;
                            if (sym == 16) { if (_index == 0) goto bad; value = _lens[_index - 1]; }
                            if (_index + repeat > _hlit + _hdist) goto bad;
                            while (repeat-- > 0) _lens[_index++] = value;
                        }
                        if (_lens[256] == 0) goto bad;
                        if (!_litLen.Build(_lens, 0, _hlit)) goto bad;
                        if (!_dist.Build(_lens, _hlit, _hdist)) goto bad;
                        _lit = _litLen; _dst = _dist;
                        _state = S.Len;
                        continue;
                    case S.Len:
                        {
                            var lit = _lit; var dst = _dst;
                            while (true)
                            {
                                while (bits <= 24 && have > 0) { hold |= (uint)*inp++ << bits; bits += 8; have--; }
                                if (room == 0) goto leave;
                                int e = lit.Decode(hold, bits);
                                if (e == -1) goto leave;
                                if (e < 0) goto bad;
                                int sym = e >> 4, len = e & 15;
                                if (sym < 256)
                                {
                                    hold >>= len; bits -= len;
                                    *outp++ = (byte)sym; window[wpos] = (byte)sym; wpos = (wpos + 1) & WMask; room--;
                                    continue;
                                }
                                if (sym == 256) { hold >>= len; bits -= len; _state = S.BlockHead; break; }
                                sym -= 257;
                                if (sym >= 29) goto bad;
                                int lx = Tables.LengthExtra[sym];
                                if (bits < len + lx) goto leave;
                                // (the distance's code and extra bits may need more than is held with the length's:
                                // the length is taken, the distance read on its own)
                                hold >>= len; bits -= len;
                                _length = Tables.LengthBase[sym] + (int)(hold & ((1u << lx) - 1));
                                hold >>= lx; bits -= lx;
                                _distance = 0;
                                _state = S.Match;
                                break;
                            }
                            continue;
                        }
                    case S.Match:
                        {
                            if (_distance == 0)
                            {
                                int e = _dst.Decode(hold, bits);
                                if (e == -1) goto leave;
                                if (e < 0) goto bad;
                                int sym = e >> 4, len = e & 15;
                                if (sym >= 30) goto bad;
                                int dx = Tables.DistExtra[sym];
                                if (bits < len + dx) goto leave;
                                hold >>= len; bits -= len;
                                _distance = Tables.DistBase[sym] + (int)(hold & ((1u << dx) - 1));
                                hold >>= dx; bits -= dx;
                                if (_distance > _wfill + (int)(outp - outStart)) goto bad;
                            }
                            int from = (wpos - _distance) & WMask;
                            while (_length > 0 && room > 0)
                            {
                                byte b = window[from]; from = (from + 1) & WMask;
                                *outp++ = b; window[wpos] = b; wpos = (wpos + 1) & WMask; room--; _length--;
                            }
                            if (_length > 0) goto leave;
                            _state = S.Len;
                            continue;
                        }
                    case S.Check:
                        if (_wrap == 0) { _state = S.Done; continue; }
                        if (bits < 32) goto leave;
                        {
                            // what this call wrote is part of what is checked
                            int produced = (int)(outp - outStart);
                            if (produced > 0)
                            {
                                _check = _wrap == 1 ? Checksums.Adler32(_check, outStart, produced) : Checksums.Crc32(_check, outStart, produced);
                                _total += (uint)produced; _wfill = Math.Min(WSize, _wfill + produced);
                                outStart = outp;
                            }
                            uint stored = _wrap == 1 ? ((hold & 0xFF) << 24) | ((hold & 0xFF00) << 8) | ((hold >> 8) & 0xFF00) | (hold >> 24) : hold;
                            if (stored != _check) goto bad;
                            hold = 0; bits = 0;
                            _state = _wrap == 2 ? S.CheckLen : S.Done;
                            continue;
                        }
                    case S.CheckLen:
                        if (bits < 32) goto leave;
                        if (hold != _total) goto bad;
                        hold = 0; bits = 0;
                        _state = S.Done;
                        continue;
                    case S.Done:
                        result = StreamEnd;
                        goto leave;
                    default:
                        goto bad;
                }
            }
        bad:
            _state = S.Bad;
            result = DataError;
        leave:
            {
                int produced = (int)(outp - outStart);
                if (produced > 0)
                {
                    if (_wrap == 1) _check = Checksums.Adler32(_check, outStart, produced);
                    else if (_wrap >= 2) _check = Checksums.Crc32(_check, outStart, produced);
                    _total += (uint)produced; _wfill = Math.Min(WSize, _wfill + produced);
                }
                // bytes taken ahead stay held; when nothing more can come of them they are the next call's
                _hold = hold; _bits = bits; _wpos = wpos;
                bool progress = have != haveStart || outp != *nextOut;
                *nextIn = inp; *availIn = have; *nextOut = outp; *availOut = room;
                if (result == Ok && !progress) result = BufError;
                return result;
            }
        }
    }

    internal sealed unsafe class ManagedDeflater
    {
        const int Ok = 0, StreamEnd = 1, StreamError = -2, BufError = -5;
        const int WSize = 32768, WMask = WSize - 1, HashBits = 15, HashSize = 1 << HashBits;
        const int MinMatch = 3, MaxMatch = 258, MinLookahead = MaxMatch + MinMatch + 1;
        const int Cap = 1 << 17, MaxTokens = 32767;

        readonly int _level, _wrap; // wrap: 0 raw, 1 zlib, 2 gzip
        readonly int _maxChain, _niceLength, _maxLazy; readonly bool _lazy;

        // output not yet taken
        byte[] _pending = new byte[1 << 16];
        int _pendingStart, _pendingEnd;
        ulong _bitBuf; int _bitCount;

        // input: buf[0.._inEnd), of which buf[_pos..) is not compressed yet; _base is the stream position of buf[0]
        readonly byte[] _buf = new byte[Cap + MaxMatch];
        int _inEnd, _pos, _blockStart;
        int _base;
        readonly int[] _head = new int[HashSize];
        readonly int[] _prev = new int[WSize];
        bool _haveLazy; int _lazyLength, _lazyDistance;

        readonly ushort[] _tokLit = new ushort[MaxTokens + 1];
        readonly ushort[] _tokDist = new ushort[MaxTokens + 1];
        int _tokens;
        readonly int[] _litFreq = new int[286];
        readonly int[] _distFreq = new int[30];

        bool _headerDone, _finished, _flushed = true;
        uint _check; uint _totalIn;

        static readonly byte[] s_lengthCode = MakeLengthCodes();
        static readonly byte[] s_distCode = MakeDistCodes();
        static byte[] MakeLengthCodes()
        {
            var t = new byte[MaxMatch + 1];
            for (int c = 0; c < 29; c++)
                for (int l = Tables.LengthBase[c]; l <= MaxMatch && (c == 28 || l < Tables.LengthBase[c + 1]); l++) t[l] = (byte)c;
            t[MaxMatch] = 28;
            return t;
        }
        static byte[] MakeDistCodes()
        {
            // distances 1..256 by themselves, greater ones by (distance - 1) >> 7
            var t = new byte[512];
            for (int c = 0; c < 30; c++)
            {
                int lo = Tables.DistBase[c], hi = c == 29 ? 32768 : Tables.DistBase[c + 1] - 1;
                for (int d = lo; d <= hi && d <= 256; d++) t[d - 1] = (byte)c;
                if (hi > 256) for (int d = Math.Max(lo, 257); d <= hi; d += 128) t[256 + ((d - 1) >> 7)] = (byte)c;
            }
            return t;
        }
        static int DistCode(int d) => d <= 256 ? s_distCode[d - 1] : s_distCode[256 + ((d - 1) >> 7)];

        public ManagedDeflater(int level, int windowBits)
        {
            if (level < 0) level = 6;
            if (level > 9) level = 9;
            _level = level;
            _wrap = windowBits < 0 ? 0 : windowBits >= 16 ? 2 : 1;
            // (zlib's own table of efforts)
            (_maxLazy, _niceLength, _maxChain) = level switch
            {
                0 => (0, 0, 0), 1 => (4, 8, 4), 2 => (5, 16, 8), 3 => (6, 32, 32), 4 => (4, 16, 16), 5 => (16, 32, 32),
                6 => (16, 128, 128), 7 => (32, 128, 256), 8 => (128, 258, 1024), _ => (258, 258, 4096),
            };
            _lazy = level >= 4;
            Reset();
        }

        public void Reset()
        {
            _pendingStart = _pendingEnd = 0; _bitBuf = 0; _bitCount = 0;
            _inEnd = _pos = _blockStart = 0; _base = 0;
            Array.Fill(_head, -1);
            _haveLazy = false; _tokens = 0;
            Array.Clear(_litFreq); Array.Clear(_distFreq);
            _headerDone = false; _finished = false; _flushed = true;
            _check = _wrap == 1 ? 1u : 0u; _totalIn = 0;
        }

        // ---------------- output ----------------
        void Need(int n)
        {
            if (_pendingEnd + n <= _pending.Length) return;
            if (_pendingStart > 0)
            {
                Buffer.BlockCopy(_pending, _pendingStart, _pending, 0, _pendingEnd - _pendingStart);
                _pendingEnd -= _pendingStart; _pendingStart = 0;
            }
            if (_pendingEnd + n > _pending.Length) Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _pendingEnd + n));
        }
        void PutByte(int b) { Need(1); _pending[_pendingEnd++] = (byte)b; }
        void PutBits(uint value, int n)
        {
            _bitBuf |= (ulong)value << _bitCount;
            _bitCount += n;
            if (_bitCount >= 32)
            {
                Need(4);
                var p = _pending; int e = _pendingEnd;
                p[e] = (byte)_bitBuf; p[e + 1] = (byte)(_bitBuf >> 8); p[e + 2] = (byte)(_bitBuf >> 16); p[e + 3] = (byte)(_bitBuf >> 24);
                _pendingEnd = e + 4;
                _bitBuf >>= 32; _bitCount -= 32;
            }
        }
        void AlignToByte()
        {
            while (_bitCount > 0) { PutByte((int)(_bitBuf & 0xFF)); _bitBuf >>= 8; _bitCount -= 8; }
            _bitBuf = 0; _bitCount = 0;
        }
        void PutU16(int v) { PutByte(v & 0xFF); PutByte((v >> 8) & 0xFF); }
        void PutU32(uint v) { PutU16((int)(v & 0xFFFF)); PutU16((int)(v >> 16)); }

        bool Drain(byte** nextOut, uint* availOut)
        {
            int n = _pendingEnd - _pendingStart;
            if (n > (int)*availOut) n = (int)*availOut;
            if (n > 0)
            {
                fixed (byte* p = &_pending[_pendingStart]) Buffer.MemoryCopy(p, *nextOut, n, n);
                *nextOut += n; *availOut -= (uint)n; _pendingStart += n;
            }
            if (_pendingStart == _pendingEnd) { _pendingStart = _pendingEnd = 0; return true; }
            return false;
        }

        void Header()
        {
            _headerDone = true;
            if (_wrap == 1)
            {
                int flevel = _level < 2 ? 0 : _level < 6 ? 1 : _level == 6 ? 2 : 3;
                int h = (0x78 << 8) | (flevel << 6);
                h += 31 - h % 31;
                PutByte(h >> 8); PutByte(h & 0xFF);
            }
            else if (_wrap == 2)
            {
                PutByte(0x1F); PutByte(0x8B); PutByte(8); PutByte(0);
                PutU32(0);
                PutByte(_level == 9 ? 2 : _level == 1 ? 4 : 0);
                PutByte(3);
            }
        }

        // ---------------- Huffman codes ----------------
        /// <summary>Code lengths for the frequencies, none longer than maxBits (frequencies are halved until the
        /// code fits); at least two symbols get a code.</summary>
        static void BuildLengths(int[] freq, int n, int maxBits, byte[] lens)
        {
            Span<int> f = stackalloc int[n];
            int used = 0;
            for (int i = 0; i < n; i++) { f[i] = freq[i]; if (f[i] > 0) used++; }
            for (int i = 0; used < 2 && i < n; i++) if (f[i] == 0) { f[i] = 1; used++; }
            Span<int> parent = stackalloc int[2 * n];
            Span<long> weight = stackalloc long[2 * n];
            Span<int> heap = stackalloc int[n + 1];
            while (true)
            {
                int count = 0, nodes = n;
                for (int i = 0; i < n; i++) if (f[i] > 0) { weight[i] = f[i]; Push(heap, ref count, i, weight); }
                while (count > 1)
                {
                    int a = Pop(heap, ref count, weight), b = Pop(heap, ref count, weight);
                    weight[nodes] = weight[a] + weight[b];
                    parent[a] = nodes; parent[b] = nodes;
                    Push(heap, ref count, nodes, weight);
                    nodes++;
                }
                int root = heap[1];
                parent[root] = -1;
                int longest = 0;
                for (int i = 0; i < n; i++)
                {
                    if (f[i] == 0) { lens[i] = 0; continue; }
                    int d = 0;
                    for (int p = i; parent[p] >= 0; p = parent[p]) d++;
                    lens[i] = (byte)d;
                    if (d > longest) longest = d;
                }
                if (longest <= maxBits) return;
                for (int i = 0; i < n; i++) if (f[i] > 0) f[i] = (f[i] + 1) >> 1;
            }

            static void Push(Span<int> heap, ref int count, int node, Span<long> weight)
            {
                int i = ++count;
                while (i > 1 && Less(node, heap[i >> 1], weight)) { heap[i] = heap[i >> 1]; i >>= 1; }
                heap[i] = node;
            }
            static int Pop(Span<int> heap, ref int count, Span<long> weight)
            {
                int top = heap[1], last = heap[count--];
                int i = 1;
                while (true)
                {
                    int c = i << 1;
                    if (c > count) break;
                    if (c < count && Less(heap[c + 1], heap[c], weight)) c++;
                    if (!Less(heap[c], last, weight)) break;
                    heap[i] = heap[c]; i = c;
                }
                heap[i] = last;
                return top;
            }
            static bool Less(int a, int b, Span<long> weight) => weight[a] < weight[b] || weight[a] == weight[b] && a < b;
        }

        /// <summary>The codes of the lengths, each with its bits in the order they are written.</summary>
        static void BuildCodes(byte[] lens, int n, ushort[] codes)
        {
            Span<int> count = stackalloc int[16];
            Span<int> next = stackalloc int[16];
            count.Clear();
            for (int i = 0; i < n; i++) count[lens[i]]++;
            count[0] = 0;
            int code = 0;
            for (int b = 1; b <= 15; b++) { code = (code + count[b - 1]) << 1; next[b] = code; }
            for (int i = 0; i < n; i++)
            {
                int len = lens[i];
                if (len == 0) { codes[i] = 0; continue; }
                int c = next[len]++, rev = 0;
                for (int b = 0; b < len; b++) rev |= ((c >> b) & 1) << (len - 1 - b);
                codes[i] = (ushort)rev;
            }
        }

        readonly byte[] _litLens = new byte[286], _distLens = new byte[30], _clLens = new byte[19], _allLens = new byte[316];
        readonly ushort[] _litCodes = new ushort[288], _distCodes = new ushort[30], _clCodes = new ushort[19];
        readonly int[] _clFreq = new int[19];
        readonly ushort[] _rle = new ushort[316 * 2];
        static readonly ushort[] s_fixedLitCodes = MakeFixedCodes(Tables.FixedLitLen, 288), s_fixedDistCodes = MakeFixedCodes(Tables.FixedDist, 30);
        static ushort[] MakeFixedCodes(byte[] lens, int n) { var c = new ushort[n]; BuildCodes(lens, n, c); return c; }

        /// <summary>Writes the tokens gathered as one block (the bytes buf[_blockStart.._pos)), by the cheapest of a
        /// block with its own codes, one with the fixed codes and stored bytes.</summary>
        void EmitBlock(bool last)
        {
            // (a match held back began at the byte before _pos: that byte is the next block's)
            int blockEnd = _pos - (_haveLazy ? 1 : 0);
            int span = blockEnd - _blockStart;
            _litFreq[256]++;
            BuildLengths(_litFreq, 286, 15, _litLens);
            BuildLengths(_distFreq, 30, 15, _distLens);
            int hlit = 286; while (hlit > 257 && _litLens[hlit - 1] == 0) hlit--;
            int hdist = 30; while (hdist > 1 && _distLens[hdist - 1] == 0) hdist--;
            // the lengths of both codes, run-length coded with the code length code
            Buffer.BlockCopy(_litLens, 0, _allLens, 0, hlit);
            Buffer.BlockCopy(_distLens, 0, _allLens, hlit, hdist);
            int total = hlit + hdist, nrle = 0;
            Array.Clear(_clFreq);
            for (int i = 0; i < total;)
            {
                int v = _allLens[i], run = 1;
                while (i + run < total && _allLens[i + run] == v) run++;
                i += run;
                if (v == 0)
                {
                    while (run >= 11) { int r = Math.Min(run, 138); _rle[nrle++] = 18; _rle[nrle++] = (ushort)(r - 11); _clFreq[18]++; run -= r; }
                    if (run >= 3) { _rle[nrle++] = 17; _rle[nrle++] = (ushort)(run - 3); _clFreq[17]++; run = 0; }
                }
                else
                {
                    _rle[nrle++] = (ushort)v; _rle[nrle++] = 0; _clFreq[v]++; run--;
                    while (run >= 3) { int r = Math.Min(run, 6); _rle[nrle++] = 16; _rle[nrle++] = (ushort)(r - 3); _clFreq[16]++; run -= r; }
                }
                while (run-- > 0) { _rle[nrle++] = (ushort)v; _rle[nrle++] = 0; _clFreq[v]++; }
            }
            BuildLengths(_clFreq, 19, 7, _clLens);
            int hclen = 19; while (hclen > 4 && _clLens[Tables.CodeLengthOrder[hclen - 1]] == 0) hclen--;

            long dynBits = 3 + 14 + 3 * hclen, fixedBits = 3;
            for (int i = 0; i < 19; i++) dynBits += (long)_clFreq[i] * (_clLens[i] + (i == 16 ? 2 : i == 17 ? 3 : i == 18 ? 7 : 0));
            for (int i = 0; i < 286; i++)
            {
                int x = i >= 257 ? Tables.LengthExtra[i - 257] : 0;
                dynBits += (long)_litFreq[i] * (_litLens[i] + x);
                fixedBits += (long)_litFreq[i] * (Tables.FixedLitLen[i] + x);
            }
            for (int i = 0; i < 30; i++)
            {
                dynBits += (long)_distFreq[i] * (_distLens[i] + Tables.DistExtra[i]);
                fixedBits += (long)_distFreq[i] * (5 + Tables.DistExtra[i]);
            }
            long storedBits = ((long)span + 5L * Math.Max(1, (span + 65534) / 65535)) * 8;

            if (_level == 0 || storedBits <= dynBits && storedBits <= fixedBits)
            {
                int at = _blockStart, left = span;
                do
                {
                    int n = Math.Min(left, 65535);
                    PutBits(last && n == left ? 1u : 0u, 3);
                    AlignToByte();
                    PutU16(n); PutU16(~n & 0xFFFF);
                    Need(n);
                    Buffer.BlockCopy(_buf, at, _pending, _pendingEnd, n);
                    _pendingEnd += n; at += n; left -= n;
                } while (left > 0);
            }
            else
            {
                ushort[] lc, dc; byte[] ll, dl;
                if (fixedBits <= dynBits)
                {
                    PutBits((last ? 1u : 0u) | 2u, 3);
                    lc = s_fixedLitCodes; dc = s_fixedDistCodes; ll = Tables.FixedLitLen; dl = Tables.FixedDist;
                }
                else
                {
                    PutBits((last ? 1u : 0u) | 4u, 3);
                    BuildCodes(_litLens, 286, _litCodes); BuildCodes(_distLens, 30, _distCodes); BuildCodes(_clLens, 19, _clCodes);
                    PutBits((uint)(hlit - 257), 5); PutBits((uint)(hdist - 1), 5); PutBits((uint)(hclen - 4), 4);
                    for (int i = 0; i < hclen; i++) PutBits(_clLens[Tables.CodeLengthOrder[i]], 3);
                    for (int i = 0; i < nrle; i += 2)
                    {
                        int s = _rle[i];
                        PutBits(_clCodes[s], _clLens[s]);
                        if (s == 16) PutBits(_rle[i + 1], 2); else if (s == 17) PutBits(_rle[i + 1], 3); else if (s == 18) PutBits(_rle[i + 1], 7);
                    }
                    lc = _litCodes; dc = _distCodes; ll = _litLens; dl = _distLens;
                }
                for (int i = 0; i < _tokens; i++)
                {
                    int d = _tokDist[i], l = _tokLit[i];
                    if (d == 0) { PutBits(lc[l], ll[l]); continue; }
                    int code = s_lengthCode[l];
                    PutBits(lc[257 + code], ll[257 + code]);
                    int lx = Tables.LengthExtra[code];
                    if (lx != 0) PutBits((uint)(l - Tables.LengthBase[code]), lx);
                    int dcode = DistCode(d);
                    PutBits(dc[dcode], dl[dcode]);
                    int dx = Tables.DistExtra[dcode];
                    if (dx != 0) PutBits((uint)(d - Tables.DistBase[dcode]), dx);
                }
                PutBits(lc[256], ll[256]);
            }
            _tokens = 0;
            Array.Clear(_litFreq); Array.Clear(_distFreq);
            _blockStart = blockEnd;
        }

        // ---------------- matching ----------------
        int Hash(int i) { var b = _buf; return ((b[i] << 10) ^ (b[i + 1] << 5) ^ b[i + 2]) & (HashSize - 1); }

        void Insert(int i)
        {
            int h = Hash(i), abs = _base + i;
            _prev[abs & WMask] = _head[h];
            _head[h] = abs;
        }

        /// <summary>The longest match for buf[i..) among earlier positions of the same hash; its length (0: none of
        /// three bytes or more) and distance.</summary>
        int LongestMatch(int i, int atLeast, out int distance)
        {
            distance = 0;
            var buf = _buf;
            int limit = _inEnd - i; if (limit > MaxMatch) limit = MaxMatch;
            if (limit < MinMatch) return 0;
            int abs = _base + i, lowest = abs - WSize + 1; if (lowest < _base) lowest = _base;
            int cand = _head[Hash(i)], chain = _maxChain, best = atLeast, nice = _niceLength;
            while (cand >= lowest && chain-- > 0)
            {
                int c = cand - _base;
                if (buf[c + best] == buf[i + best] && buf[c] == buf[i])
                {
                    int l = 0;
                    while (l < limit && buf[c + l] == buf[i + l]) l++;
                    if (l > best)
                    {
                        best = l; distance = abs - cand;
                        if (l >= nice || l >= limit) break;
                    }
                }
                int next = _prev[cand & WMask];
                if (next >= cand) break;
                cand = next;
            }
            return distance != 0 && best >= MinMatch ? best : 0;
        }

        void Literal(int b) { _tokLit[_tokens] = (ushort)b; _tokDist[_tokens++] = 0; _litFreq[b]++; }
        void Match(int length, int distance)
        {
            _tokLit[_tokens] = (ushort)length; _tokDist[_tokens++] = (ushort)distance;
            _litFreq[257 + s_lengthCode[length]]++; _distFreq[DistCode(distance)]++;
        }

        /// <summary>Compresses buf[_pos..end) into tokens (all of it when `all`, else up to where a match could
        /// still grow with more input).</summary>
        void Compress(bool all)
        {
            int stop = all ? _inEnd : _inEnd - MinLookahead;
            if (_level == 0)
            {
                // stored: a block for every 65535 bytes at most
                if (stop > _pos) _pos = stop;
                while (_pos - _blockStart >= 65535 || all && _pos > _blockStart)
                {
                    int end = _pos;
                    _pos = Math.Min(end, _blockStart + 65535);
                    EmitBlock(false);
                    _pos = end;
                }
                return;
            }
            while (_pos < stop)
            {
                if (_tokens >= MaxTokens - 1) EmitBlock(false);
                int i = _pos;
                bool canHash = i + MinMatch <= _inEnd;
                int length = 0, distance = 0;
                if (canHash)
                {
                    if (!_haveLazy || _lazyLength < _maxLazy) length = LongestMatch(i, _haveLazy ? _lazyLength : MinMatch - 1, out distance);
                    Insert(i);
                }
                if (_lazy)
                {
                    if (_haveLazy)
                    {
                        if (length > _lazyLength)
                        {
                            // the match here is longer: the byte before is a literal
                            Literal(_buf[i - 1]);
                            _lazyLength = length; _lazyDistance = distance;
                            _pos++;
                        }
                        else
                        {
                            // the match from the byte before stands (it started at i - 1)
                            Match(_lazyLength, _lazyDistance);
                            int end = i - 1 + _lazyLength;
                            for (int k = i + 1; k < end; k++) if (k + MinMatch <= _inEnd) Insert(k);
                            _pos = end;
                            _haveLazy = false;
                        }
                    }
                    else if (length >= MinMatch)
                    {
                        _haveLazy = true; _lazyLength = length; _lazyDistance = distance;
                        _pos++;
                    }
                    else { Literal(_buf[i]); _pos++; }
                }
                else if (length >= MinMatch)
                {
                    Match(length, distance);
                    int end = i + length;
                    for (int k = i + 1; k < end; k++) if (k + MinMatch <= _inEnd) Insert(k);
                    _pos = end;
                }
                else { Literal(_buf[i]); _pos++; }
            }
            if (all && _haveLazy)
            {
                // the input ends inside a match held back
                Match(_lazyLength, _lazyDistance);
                _haveLazy = false;
                // (_pos is one past the match's start: the match ends at the end of the input or before)
                int end = _pos - 1 + _lazyLength;
                for (int k = _pos; k < end; k++) if (k + MinMatch <= _inEnd) Insert(k);
                _pos = end;
                if (_pos < _inEnd) Compress(true);
            }
        }

        /// <summary>Makes room in the buffer: what is more than a window behind the next byte to compress goes.</summary>
        void Slide()
        {
            int keepFrom = _pos - (_haveLazy ? 1 : 0) - WSize;
            if (keepFrom <= 0) return;
            if (_blockStart < keepFrom) EmitBlock(false);
            Buffer.BlockCopy(_buf, keepFrom, _buf, 0, _inEnd - keepFrom);
            _base += keepFrom; _pos -= keepFrom; _inEnd -= keepFrom; _blockStart -= keepFrom;
            if (_base > 0x40000000)
            {
                // positions are kept as they are in the stream: brought back before they overflow
                int delta = (_base - WSize) & ~WMask;
                for (int i = 0; i < _head.Length; i++) _head[i] = _head[i] >= delta ? _head[i] - delta : -1;
                for (int i = 0; i < _prev.Length; i++) _prev[i] = _prev[i] >= delta ? _prev[i] - delta : -1;
                // (a position p is kept at index p & WMask: delta is a multiple of the window, so the places hold)
                _base -= delta;
            }
        }

        public int Deflate(byte** nextIn, uint* availIn, byte** nextOut, uint* availOut, int flush)
        {
            if (_finished && (*availIn != 0 || flush != 4)) return _pendingStart == _pendingEnd ? StreamError : BufError;
            uint in0 = *availIn, out0 = *availOut;
            if (!_headerDone) Header();
            while (true)
            {
                if (!Drain(nextOut, availOut)) break;
                if (_finished) break;
                // input
                uint take = (uint)(Cap - _inEnd);
                if (take > *availIn) take = *availIn;
                if (take > 0)
                {
                    fixed (byte* p = &_buf[_inEnd]) Buffer.MemoryCopy(*nextIn, p, take, take);
                    if (_wrap == 1) _check = Checksums.Adler32(_check, *nextIn, (int)take);
                    else if (_wrap == 2) _check = Checksums.Crc32(_check, *nextIn, (int)take);
                    _totalIn += take;
                    *nextIn += take; *availIn -= take; _inEnd += (int)take;
                    _flushed = false;
                }
                bool all = flush != 0 && *availIn == 0;
                Compress(all);
                if (*availIn > 0)
                {
                    // the buffer is full: the block so far goes, and the window moves up
                    if (_inEnd == Cap) { Slide(); if (_inEnd == Cap) { EmitBlock(false); Slide(); } }
                    continue;
                }
                if (flush == 0) { if (_pendingEnd > _pendingStart) continue; break; }
                if (flush == 4)
                {
                    EmitBlock(true);
                    AlignToByte();
                    if (_wrap == 1) { PutByte((int)(_check >> 24)); PutByte((int)(_check >> 16) & 0xFF); PutByte((int)(_check >> 8) & 0xFF); PutByte((int)_check & 0xFF); }
                    else if (_wrap == 2) { PutU32(_check); PutU32(_totalIn); }
                    _finished = true;
                    continue;
                }
                // a flush: the block so far, then an empty stored block, which ends on a byte
                if (_flushed) break;
                if (_tokens > 0 || _pos > _blockStart) EmitBlock(false);
                PutBits(0, 3);
                AlignToByte();
                PutU16(0); PutU16(0xFFFF);
                _flushed = true;
            }
            if (_finished && _pendingStart == _pendingEnd) return StreamEnd;
            return in0 == *availIn && out0 == *availOut ? BufError : Ok;
        }
    }
}
