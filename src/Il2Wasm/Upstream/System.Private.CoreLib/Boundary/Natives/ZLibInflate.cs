// il2wasm: inflate for libSystem.IO.Compression.Native (Natives/CompressionNative.cs), from the managed inflater
// System.IO.Compression's old boundary used (Upstream/System.IO.Compression/Boundary/ManagedZLib.cs). Inflate's output is
// fixed by the format (RFC 1950, 1951, 1952); this is not a port of zlib-ng's inflate.c, so its error messages and how
// much input it holds back may differ from it (docs/CORELIB.md section 7).
using System;

namespace Il2Wasm.Runtime.Natives.Inflate
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
}
