// il2wasm: zlib-ng's inflate, ported to C#, so that il2wasm's libSystem.IO.Compression.Native decompresses as the
// browser-wasm runtime's does, call for call: the same output, return codes, stream positions and error messages
// (SPEC 7; docs/CORELIB.md section 7.1).
//
// Ported from zlib-ng 2.2.5 as dotnet/runtime v11.0.0 vendors it (src/native/external/zlib-ng; zlib-ng commit
// d54e3769be0c522015b784eca2af258b1c026107): inflate.c, inflate.h, inflate_p.h, inftrees.c, inftrees.h,
// inffast_tpl.h (its generic form), inffixed_tbl.h (made here by inflate_table, as zlib's makefixed makes it), with
// the zlib-compatible API, GUNZIP on, no INFLATE_STRICT, no INFLATE_ALLOW_INVALID_DISTANCE_TOOFAR_ARRR, no gzip
// header record and no dictionary. The chunked copies (chunkcopy_safe, CHUNKCOPY, CHUNKMEMSET, CHUNKUNROLL) are byte
// copies here: every one of them writes the bytes an LZ77 copy of that length and distance gives. This is an altered
// source version: a translation to C#, not the original software.
//
// zlib-ng's licence (LICENSE.md), which applies to this file:
//
//   (C) 1995-2024 Jean-loup Gailly and Mark Adler
//
//   This software is provided 'as-is', without any express or implied
//   warranty. In no event will the authors be held liable for any damages
//   arising from the use of this software.
//
//   Permission is granted to anyone to use this software for any purpose,
//   including commercial applications, and to alter it and redistribute it
//   freely, subject to the following restrictions:
//
//   1. The origin of this software must not be misrepresented; you must not
//      claim that you wrote the original software. If you use this software
//      in a product, an acknowledgment in the product documentation would be
//      appreciated but is not required.
//
//   2. Altered source versions must be plainly marked as such, and must not be
//      misrepresented as being the original software.
//
//   3. This notice may not be removed or altered from any source distribution.
using System;
using System.Runtime.InteropServices;

namespace Il2Wasm.Runtime.Natives
{
    /// <summary>inftrees.c. A `code` is a uint: op in bits 0-7, bits in 8-15, val in 16-31.</summary>
    internal static unsafe class ZInfTrees
    {
        public const int ENOUGH_LENS = 1332, ENOUGH_DISTS = 592, ENOUGH = ENOUGH_LENS + ENOUGH_DISTS;
        public const int CODES = 0, LENS = 1, DISTS = 2;
        const int MAX_BITS = 15;

        public static uint Code(uint op, uint bits, uint val) => op | bits << 8 | val << 16;
        public static uint Op(uint c) => c & 0xFF;
        public static uint Bits(uint c) => (c >> 8) & 0xFF;
        public static uint Val(uint c) => c >> 16;

        static readonly ushort[] s_lbase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258, 0, 0];
        static readonly ushort[] s_lext = [16, 16, 16, 16, 16, 16, 16, 16, 17, 17, 17, 17, 18, 18, 18, 18, 19, 19, 19, 19, 20, 20, 20, 20, 21, 21, 21, 21, 16, 203, 77];
        static readonly ushort[] s_dbase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577, 0, 0];
        static readonly ushort[] s_dext = [16, 16, 16, 16, 17, 17, 18, 18, 19, 19, 20, 20, 21, 21, 22, 22, 23, 23, 24, 24, 25, 25, 26, 26, 27, 27, 28, 28, 29, 29, 64, 64];

        /// <summary>inffixed_tbl.h's lenfix (512) and distfix (32).</summary>
        public static readonly uint* LenFix, DistFix;

        static ZInfTrees()
        {
            LenFix = (uint*)NativeMemory.AllocZeroed(512 * 4);
            DistFix = (uint*)NativeMemory.AllocZeroed(32 * 4);
            ushort* lens = stackalloc ushort[320];
            ushort* work = stackalloc ushort[288];
            uint sym = 0;
            while (sym < 144) lens[sym++] = 8;
            while (sym < 256) lens[sym++] = 9;
            while (sym < 280) lens[sym++] = 7;
            while (sym < 288) lens[sym++] = 8;
            uint* next = LenFix;
            uint bits = 9;
            InflateTable(LENS, lens, 288, ref next, ref bits, work);
            sym = 0;
            while (sym < 32) lens[sym++] = 5;
            next = DistFix;
            bits = 5;
            InflateTable(DISTS, lens, 32, ref next, ref bits, work);
        }

        public static int InflateTable(int type, ushort* lens, uint codes, ref uint* table, ref uint bits, ushort* work)
        {
            uint len, sym, min, max, root, curr, drop, used, huff, incr, fill, low, mask, match;
            int left;
            uint here;
            uint* next;
            ushort* count = stackalloc ushort[MAX_BITS + 1];
            ushort* offs = stackalloc ushort[MAX_BITS + 1];
            for (len = 0; len <= MAX_BITS; len++) count[len] = 0;
            for (sym = 0; sym < codes; sym++) count[lens[sym]]++;
            root = bits;
            for (max = MAX_BITS; max >= 1; max--)
                if (count[max] != 0) break;
            root = Math.Min(root, max);
            if (max == 0)
            {
                here = Code(64, 1, 0);
                *table++ = here;
                *table++ = here;
                bits = 1;
                return 0;
            }
            for (min = 1; min < max; min++)
                if (count[min] != 0) break;
            root = Math.Max(root, min);
            left = 1;
            for (len = 1; len <= MAX_BITS; len++)
            {
                left <<= 1;
                left -= count[len];
                if (left < 0) return -1;
            }
            if (left > 0 && (type == CODES || max != 1)) return -1;
            offs[1] = 0;
            for (len = 1; len < MAX_BITS; len++) offs[len + 1] = (ushort)(offs[len] + count[len]);
            for (sym = 0; sym < codes; sym++)
                if (lens[sym] != 0) work[offs[lens[sym]]++] = (ushort)sym;
            ushort[]? baseArr = null, extraArr = null;
            switch (type)
            {
                case CODES: match = 20; break;
                case LENS: baseArr = s_lbase; extraArr = s_lext; match = 257; break;
                default: baseArr = s_dbase; extraArr = s_dext; match = 0; break;
            }
            huff = 0;
            sym = 0;
            len = min;
            next = table;
            curr = root;
            drop = 0;
            low = unchecked((uint)-1);
            used = 1u << (int)root;
            mask = used - 1;
            if ((type == LENS && used > ENOUGH_LENS) || (type == DISTS && used > ENOUGH_DISTS)) return 1;
            for (; ; )
            {
                uint hbits = len - drop;
                if (work[sym] >= match)
                {
                    // (CODES: base and extra are work itself, and match 20 is above every symbol: never taken)
                    here = Code(extraArr![work[sym] - match], hbits, baseArr![work[sym] - match]);
                }
                else if (work[sym] + 1u < match) here = Code(0, hbits, work[sym]);
                else here = Code(32 + 64, hbits, 0);
                incr = 1u << (int)(len - drop);
                fill = 1u << (int)curr;
                min = fill;
                do
                {
                    fill -= incr;
                    next[(huff >> (int)drop) + fill] = here;
                } while (fill != 0);
                incr = 1u << (int)(len - 1);
                while ((huff & incr) != 0) incr >>= 1;
                if (incr != 0)
                {
                    huff &= incr - 1;
                    huff += incr;
                }
                else huff = 0;
                sym++;
                if (--count[len] == 0)
                {
                    if (len == max) break;
                    len = lens[work[sym]];
                }
                if (len > root && (huff & mask) != low)
                {
                    if (drop == 0) drop = root;
                    next += min;
                    curr = len - drop;
                    left = 1 << (int)curr;
                    while (curr + drop < max)
                    {
                        left -= count[curr + drop];
                        if (left <= 0) break;
                        curr++;
                        left <<= 1;
                    }
                    used += 1u << (int)curr;
                    if ((type == LENS && used > ENOUGH_LENS) || (type == DISTS && used > ENOUGH_DISTS)) return 1;
                    low = huff & mask;
                    table[low] = Code(curr, root, (uint)(next - table));
                }
            }
            if (huff != 0) next[huff] = Code(64, len - drop, 0);
            table += used;
            bits = root;
            return 0;
        }
    }

    /// <summary>inflate_state, and inflate.c and inffast_tpl.h over it.</summary>
    internal sealed unsafe class ZngInflate : ZStreamFields
    {
        enum Mode
        {
            HEAD = 16180, FLAGS, TIME, OS, EXLEN, EXTRA, NAME, COMMENT, HCRC, DICTID, DICT,
            TYPE, TYPEDO, STORED, COPY_, COPY, TABLE, LENLENS, CODELENS, LEN_, LEN, LENEXT, DIST, DISTEXT, MATCH, LIT,
            CHECK, LENGTH, DONE, BAD, SYNC,
        }

        const int MAX_WBITS = 15, MIN_WBITS = 8, MAX_BITS = 15, MAX_DIST_EXTRA_BITS = 13;
        const int Z_TREES = 6;
        const uint INFLATE_FAST_MIN_HAVE = 15, INFLATE_FAST_MIN_LEFT = 260;
        static readonly ushort[] s_order = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

        byte* _block;
        Mode mode;
        int last, wrap, havedict, flags;
        uint was, check, total;
        int back;
        uint wbits, wsize, wbufsize, whave, wnext;
        byte* window;
        ulong hold;
        uint bits, lenbits, distbits;
        uint* lencode, distcode;
        uint length, offset, extra, ncode, nlen, ndist, have;
        uint* next;
        uint crcFold;
        ushort* lens, work;
        uint* codes;

        ZngInflate() { }

        /// <summary>inflateInit2: the state, or the error and null.</summary>
        public static int Init2(int windowBits, out ZngInflate? state)
        {
            state = null;
            var s = new ZngInflate();
            // (alloc_inflate's block: the window (32K + 64), then the state's tables)
            nuint windowSize = (1 << MAX_WBITS) + 64;
            nuint size = windowSize + 64 + 320 * 2 + 288 * 2 + ZInfTrees.ENOUGH * 4;
            byte* blk = (byte*)NativeMemory.AllocZeroed(size);
            if (blk == null) return ZConst.Z_MEM_ERROR;
            s._block = blk;
            s.window = blk;
            s.lens = (ushort*)(blk + windowSize + 64);
            s.work = s.lens + 320;
            s.codes = (uint*)(s.work + 288);
            s.wbufsize = (uint)windowSize;
            s.mode = Mode.HEAD;
            s.Msg = null;
            int ret = s.Reset2(windowBits);
            if (ret != ZConst.Z_OK) { s.Free(); return ret; }
            state = s;
            return ret;
        }

        public void Free()
        {
            if (_block != null) NativeMemory.Free(_block);
            _block = null;
        }

        int ResetKeep()
        {
            TotalIn = TotalOut = 0;
            total = 0;
            Msg = null;
            if (wrap != 0) Adler = (uint)(wrap & 1);
            mode = Mode.HEAD;
            check = 1;
            last = 0;
            havedict = 0;
            flags = -1;
            hold = 0;
            bits = 0;
            lencode = distcode = next = codes;
            back = -1;
            return ZConst.Z_OK;
        }

        int Reset()
        {
            wsize = 0;
            whave = 0;
            wnext = 0;
            return ResetKeep();
        }

        public int Reset2(int windowBits)
        {
            int w;
            if (windowBits < 0)
            {
                w = 0;
                if (windowBits < -MAX_WBITS) return ZConst.Z_STREAM_ERROR;
                windowBits = -windowBits;
            }
            else
            {
                w = (windowBits >> 4) + 5;
                if (windowBits < 48) windowBits &= MAX_WBITS;
            }
            if (windowBits != 0 && (windowBits < MIN_WBITS || windowBits > MAX_WBITS)) return ZConst.Z_STREAM_ERROR;
            wrap = w;
            wbits = (uint)windowBits;
            return Reset();
        }

        void FixedTables()
        {
            lencode = ZInfTrees.LenFix;
            lenbits = 9;
            distcode = ZInfTrees.DistFix;
            distbits = 5;
        }

        // ---------------- the checksums ----------------
        void ChksumCpy(byte* dst, byte* src, uint copy)
        {
            if (copy == 0) return;
            Buffer.MemoryCopy(src, dst, copy, copy);
            if (flags != 0) crcFold = Crc32.Update(crcFold, src, (int)copy);
            else Adler = check = Adler32.Update(check, src, (int)copy);
        }

        void Chksum(byte* src, uint len)
        {
            if (flags != 0) crcFold = Crc32.Update(crcFold, src, (int)len);
            else Adler = check = Adler32.Update(check, src, (int)len);
        }

        void UpdateWindow(byte* end, uint len, bool cksum)
        {
            uint dist;
            if (wsize == 0) wsize = 1u << (int)wbits;
            if (len >= wsize)
            {
                if (cksum)
                {
                    if (len > wsize) Chksum(end - len, len - wsize);
                    ChksumCpy(window, end - wsize, wsize);
                }
                else Buffer.MemoryCopy(end - wsize, window, wsize, wsize);
                wnext = 0;
                whave = wsize;
            }
            else
            {
                dist = wsize - wnext;
                dist = Math.Min(dist, len);
                if (cksum) ChksumCpy(window + wnext, end - len, dist);
                else Buffer.MemoryCopy(end - len, window + wnext, dist, dist);
                len -= dist;
                if (len != 0)
                {
                    if (cksum) ChksumCpy(window, end - len, len);
                    else Buffer.MemoryCopy(end - len, window, len, len);
                    wnext = len;
                    whave = wsize;
                }
                else
                {
                    wnext += dist;
                    if (wnext == wsize) wnext = 0;
                    if (whave < wsize) whave += dist;
                }
            }
        }

        static uint Crc2(uint crc, ulong word)
        {
            byte* hbuf = stackalloc byte[2];
            hbuf[0] = (byte)word; hbuf[1] = (byte)(word >> 8);
            return Crc32.Update(crc, hbuf, 2);
        }

        static uint Crc4(uint crc, ulong word)
        {
            byte* hbuf = stackalloc byte[4];
            hbuf[0] = (byte)word; hbuf[1] = (byte)(word >> 8); hbuf[2] = (byte)(word >> 16); hbuf[3] = (byte)(word >> 24);
            return Crc32.Update(crc, hbuf, 4);
        }

        static uint Swap32(uint x) => (x >> 24) | ((x >> 8) & 0xFF00) | ((x << 8) & 0xFF0000) | (x << 24);

        /// <summary>LZ77's copy: `len` bytes from `from` to `out`, byte by byte (what every chunked copy gives).</summary>
        static byte* Copy(byte* outp, byte* from, uint len)
        {
            while (len-- != 0) *outp++ = *from++;
            return outp;
        }

        // ---------------- inflate.c: inflate ----------------
        public int Inflate(int flush)
        {
            if (NextOut == null || (NextIn == null && AvailIn != 0)) return ZConst.Z_STREAM_ERROR;
            if (mode == Mode.TYPE) mode = Mode.TYPEDO;
            byte* put = NextOut;
            uint left = AvailOut;
            byte* nxt = NextIn;
            uint hv = AvailIn;
            ulong hld = hold;
            uint bts = bits;
            uint inBytes = hv, outBytes = left;
            int ret = ZConst.Z_OK;
            uint copy, len;
            uint here, lst;
            byte* from;

            // (the macros: PULLBYTE is `if (!Pull()) goto inf_leave;`)
            bool Pull()
            {
                if (hv == 0) return false;
                hv--;
                hld += (ulong)(*nxt++) << (int)bts;
                bts += 8;
                return true;
            }
            uint BitsOf(uint n) => (uint)(hld & ((1ul << (int)n) - 1));
            void Drop(uint n) { hld >>= (int)n; bts -= n; }
            void SetBad(string m) { mode = Mode.BAD; Msg = m; }

            for (; ; )
            {
                switch (mode)
                {
                    case Mode.HEAD:
                        if (wrap == 0) { mode = Mode.TYPEDO; break; }
                        while (bts < 16) if (!Pull()) goto inf_leave;
                        if ((wrap & 2) != 0 && hld == 0x8b1f)
                        {
                            if (wbits == 0) wbits = MAX_WBITS;
                            check = 0;
                            check = Crc2(check, hld);
                            hld = 0; bts = 0;
                            mode = Mode.FLAGS;
                            break;
                        }
                        if ((wrap & 1) == 0 || ((BitsOf(8) << 8) + (hld >> 8)) % 31 != 0) { SetBad("incorrect header check"); break; }
                        if (BitsOf(4) != ZConst.Z_DEFLATED) { SetBad("unknown compression method"); break; }
                        Drop(4);
                        len = BitsOf(4) + 8;
                        if (wbits == 0) wbits = len;
                        if (len > MAX_WBITS || len > wbits) { SetBad("invalid window size"); break; }
                        flags = 0;
                        Adler = check = 1;
                        mode = (hld & 0x200) != 0 ? Mode.DICTID : Mode.TYPE;
                        hld = 0; bts = 0;
                        break;
                    case Mode.FLAGS:
                        while (bts < 16) if (!Pull()) goto inf_leave;
                        flags = (int)hld;
                        if ((flags & 0xff) != ZConst.Z_DEFLATED) { SetBad("unknown compression method"); break; }
                        if ((flags & 0xe000) != 0) { SetBad("unknown header flags set"); break; }
                        if ((flags & 0x0200) != 0 && (wrap & 4) != 0) check = Crc2(check, hld);
                        hld = 0; bts = 0;
                        mode = Mode.TIME;
                        goto case Mode.TIME;
                    case Mode.TIME:
                        while (bts < 32) if (!Pull()) goto inf_leave;
                        if ((flags & 0x0200) != 0 && (wrap & 4) != 0) check = Crc4(check, hld);
                        hld = 0; bts = 0;
                        mode = Mode.OS;
                        goto case Mode.OS;
                    case Mode.OS:
                        while (bts < 16) if (!Pull()) goto inf_leave;
                        if ((flags & 0x0200) != 0 && (wrap & 4) != 0) check = Crc2(check, hld);
                        hld = 0; bts = 0;
                        mode = Mode.EXLEN;
                        goto case Mode.EXLEN;
                    case Mode.EXLEN:
                        if ((flags & 0x0400) != 0)
                        {
                            while (bts < 16) if (!Pull()) goto inf_leave;
                            length = (ushort)hld;
                            if ((flags & 0x0200) != 0 && (wrap & 4) != 0) check = Crc2(check, hld);
                            hld = 0; bts = 0;
                        }
                        mode = Mode.EXTRA;
                        goto case Mode.EXTRA;
                    case Mode.EXTRA:
                        if ((flags & 0x0400) != 0)
                        {
                            copy = length;
                            if (copy > hv) copy = hv;
                            if (copy != 0)
                            {
                                if ((flags & 0x0200) != 0 && (wrap & 4) != 0) check = Crc32.Update(check, nxt, (int)copy);
                                hv -= copy;
                                nxt += copy;
                                length -= copy;
                            }
                            if (length != 0) goto inf_leave;
                        }
                        length = 0;
                        mode = Mode.NAME;
                        goto case Mode.NAME;
                    case Mode.NAME:
                        if ((flags & 0x0800) != 0)
                        {
                            if (hv == 0) goto inf_leave;
                            copy = 0;
                            do { len = nxt[copy++]; } while (len != 0 && copy < hv);
                            if ((flags & 0x0200) != 0 && (wrap & 4) != 0) check = Crc32.Update(check, nxt, (int)copy);
                            hv -= copy;
                            nxt += copy;
                            if (len != 0) goto inf_leave;
                        }
                        length = 0;
                        mode = Mode.COMMENT;
                        goto case Mode.COMMENT;
                    case Mode.COMMENT:
                        if ((flags & 0x1000) != 0)
                        {
                            if (hv == 0) goto inf_leave;
                            copy = 0;
                            do { len = nxt[copy++]; } while (len != 0 && copy < hv);
                            if ((flags & 0x0200) != 0 && (wrap & 4) != 0) check = Crc32.Update(check, nxt, (int)copy);
                            hv -= copy;
                            nxt += copy;
                            if (len != 0) goto inf_leave;
                        }
                        mode = Mode.HCRC;
                        goto case Mode.HCRC;
                    case Mode.HCRC:
                        if ((flags & 0x0200) != 0)
                        {
                            while (bts < 16) if (!Pull()) goto inf_leave;
                            if ((wrap & 4) != 0 && hld != (check & 0xffff)) { SetBad("header crc mismatch"); break; }
                            hld = 0; bts = 0;
                        }
                        if ((wrap & 4) != 0 && flags != 0) { crcFold = 0; Adler = check = 0; }
                        mode = Mode.TYPE;
                        break;
                    case Mode.DICTID:
                        while (bts < 32) if (!Pull()) goto inf_leave;
                        Adler = check = Swap32((uint)hld);
                        hld = 0; bts = 0;
                        mode = Mode.DICT;
                        goto case Mode.DICT;
                    case Mode.DICT:
                        if (havedict == 0)
                        {
                            NextOut = put; AvailOut = left; NextIn = nxt; AvailIn = hv; hold = hld; bits = bts;
                            return ZConst.Z_NEED_DICT;
                        }
                        Adler = check = 1;
                        mode = Mode.TYPE;
                        goto case Mode.TYPE;
                    case Mode.TYPE:
                        if (flush == ZConst.Z_BLOCK || flush == Z_TREES) goto inf_leave;
                        goto case Mode.TYPEDO;
                    case Mode.TYPEDO:
                        if (last != 0)
                        {
                            hld >>= (int)(bts & 7); bts -= bts & 7;
                            mode = Mode.CHECK;
                            break;
                        }
                        while (bts < 3) if (!Pull()) goto inf_leave;
                        last = (int)BitsOf(1);
                        Drop(1);
                        switch (BitsOf(2))
                        {
                            case 0: mode = Mode.STORED; break;
                            case 1:
                                FixedTables();
                                mode = Mode.LEN_;
                                if (flush == Z_TREES) { Drop(2); goto inf_leave; }
                                break;
                            case 2: mode = Mode.TABLE; break;
                            case 3: SetBad("invalid block type"); break;
                        }
                        Drop(2);
                        break;
                    case Mode.STORED:
                        hld >>= (int)(bts & 7); bts -= bts & 7;
                        while (bts < 32) if (!Pull()) goto inf_leave;
                        if ((hld & 0xffff) != ((hld >> 16) ^ 0xffff)) { SetBad("invalid stored block lengths"); break; }
                        length = (ushort)hld;
                        hld = 0; bts = 0;
                        mode = Mode.COPY_;
                        if (flush == Z_TREES) goto inf_leave;
                        goto case Mode.COPY_;
                    case Mode.COPY_:
                        mode = Mode.COPY;
                        goto case Mode.COPY;
                    case Mode.COPY:
                        copy = length;
                        if (copy != 0)
                        {
                            copy = Math.Min(copy, hv);
                            copy = Math.Min(copy, left);
                            if (copy == 0) goto inf_leave;
                            Buffer.MemoryCopy(nxt, put, copy, copy);
                            hv -= copy;
                            nxt += copy;
                            left -= copy;
                            put += copy;
                            length -= copy;
                            break;
                        }
                        mode = Mode.TYPE;
                        break;
                    case Mode.TABLE:
                        while (bts < 14) if (!Pull()) goto inf_leave;
                        nlen = BitsOf(5) + 257;
                        Drop(5);
                        ndist = BitsOf(5) + 1;
                        Drop(5);
                        ncode = BitsOf(4) + 4;
                        Drop(4);
                        if (nlen > 286 || ndist > 30) { SetBad("too many length or distance symbols"); break; }
                        have = 0;
                        mode = Mode.LENLENS;
                        goto case Mode.LENLENS;
                    case Mode.LENLENS:
                        {
                            while (have < ncode)
                            {
                                while (bts < 3) if (!Pull()) goto inf_leave;
                                lens[s_order[have++]] = (ushort)BitsOf(3);
                                Drop(3);
                            }
                            while (have < 19) lens[s_order[have++]] = 0;
                            next = codes;
                            lencode = next;
                            lenbits = 7;
                            uint* n = next; uint b = lenbits;
                            ret = ZInfTrees.InflateTable(ZInfTrees.CODES, lens, 19, ref n, ref b, work);
                            next = n; lenbits = b;
                            if (ret != 0) { SetBad("invalid code lengths set"); break; }
                            have = 0;
                            mode = Mode.CODELENS;
                            goto case Mode.CODELENS;
                        }
                    case Mode.CODELENS:
                        {
                            while (have < nlen + ndist)
                            {
                                for (; ; )
                                {
                                    here = lencode[BitsOf(lenbits)];
                                    if (ZInfTrees.Bits(here) <= bts) break;
                                    if (!Pull()) goto inf_leave;
                                }
                                uint hval = ZInfTrees.Val(here), hbits = ZInfTrees.Bits(here);
                                if (hval < 16)
                                {
                                    Drop(hbits);
                                    lens[have++] = (ushort)hval;
                                }
                                else
                                {
                                    if (hval == 16)
                                    {
                                        while (bts < hbits + 2) if (!Pull()) goto inf_leave;
                                        Drop(hbits);
                                        if (have == 0) { SetBad("invalid bit length repeat"); break; }
                                        len = lens[have - 1];
                                        copy = 3 + BitsOf(2);
                                        Drop(2);
                                    }
                                    else if (hval == 17)
                                    {
                                        while (bts < hbits + 3) if (!Pull()) goto inf_leave;
                                        Drop(hbits);
                                        len = 0;
                                        copy = 3 + BitsOf(3);
                                        Drop(3);
                                    }
                                    else
                                    {
                                        while (bts < hbits + 7) if (!Pull()) goto inf_leave;
                                        Drop(hbits);
                                        len = 0;
                                        copy = 11 + BitsOf(7);
                                        Drop(7);
                                    }
                                    if (have + copy > nlen + ndist) { SetBad("invalid bit length repeat"); break; }
                                    while (copy != 0)
                                    {
                                        --copy;
                                        lens[have++] = (ushort)len;
                                    }
                                }
                            }
                            if (mode == Mode.BAD) break;
                            if (lens[256] == 0) { SetBad("invalid code -- missing end-of-block"); break; }
                            next = codes;
                            lencode = next;
                            lenbits = 10;
                            uint* n = next; uint b = lenbits;
                            ret = ZInfTrees.InflateTable(ZInfTrees.LENS, lens, nlen, ref n, ref b, work);
                            next = n; lenbits = b;
                            if (ret != 0) { SetBad("invalid literal/lengths set"); break; }
                            distcode = next;
                            distbits = 9;
                            n = next; b = distbits;
                            ret = ZInfTrees.InflateTable(ZInfTrees.DISTS, lens + nlen, ndist, ref n, ref b, work);
                            next = n; distbits = b;
                            if (ret != 0) { SetBad("invalid distances set"); break; }
                            mode = Mode.LEN_;
                            if (flush == Z_TREES) goto inf_leave;
                            goto case Mode.LEN_;
                        }
                    case Mode.LEN_:
                        mode = Mode.LEN;
                        goto case Mode.LEN;
                    case Mode.LEN:
                        if (hv >= INFLATE_FAST_MIN_HAVE && left >= INFLATE_FAST_MIN_LEFT)
                        {
                            NextOut = put; AvailOut = left; NextIn = nxt; AvailIn = hv; hold = hld; bits = bts;
                            InflateFast(outBytes);
                            put = NextOut; left = AvailOut; nxt = NextIn; hv = AvailIn; hld = hold; bts = bits;
                            if (mode == Mode.TYPE) back = -1;
                            break;
                        }
                        back = 0;
                        for (; ; )
                        {
                            here = lencode[BitsOf(lenbits)];
                            if (ZInfTrees.Bits(here) <= bts) break;
                            if (!Pull()) goto inf_leave;
                        }
                        if (ZInfTrees.Op(here) != 0 && (ZInfTrees.Op(here) & 0xf0) == 0)
                        {
                            lst = here;
                            for (; ; )
                            {
                                here = lencode[ZInfTrees.Val(lst) + (BitsOf(ZInfTrees.Bits(lst) + ZInfTrees.Op(lst)) >> (int)ZInfTrees.Bits(lst))];
                                if (ZInfTrees.Bits(lst) + ZInfTrees.Bits(here) <= bts) break;
                                if (!Pull()) goto inf_leave;
                            }
                            Drop(ZInfTrees.Bits(lst));
                            back += (int)ZInfTrees.Bits(lst);
                        }
                        Drop(ZInfTrees.Bits(here));
                        back += (int)ZInfTrees.Bits(here);
                        length = ZInfTrees.Val(here);
                        if (ZInfTrees.Op(here) == 0) { mode = Mode.LIT; break; }
                        if ((ZInfTrees.Op(here) & 32) != 0) { back = -1; mode = Mode.TYPE; break; }
                        if ((ZInfTrees.Op(here) & 64) != 0) { SetBad("invalid literal/length code"); break; }
                        extra = ZInfTrees.Op(here) & MAX_BITS;
                        mode = Mode.LENEXT;
                        goto case Mode.LENEXT;
                    case Mode.LENEXT:
                        if (extra != 0)
                        {
                            while (bts < extra) if (!Pull()) goto inf_leave;
                            length += BitsOf(extra);
                            Drop(extra);
                            back += (int)extra;
                        }
                        was = length;
                        mode = Mode.DIST;
                        goto case Mode.DIST;
                    case Mode.DIST:
                        for (; ; )
                        {
                            here = distcode[BitsOf(distbits)];
                            if (ZInfTrees.Bits(here) <= bts) break;
                            if (!Pull()) goto inf_leave;
                        }
                        if ((ZInfTrees.Op(here) & 0xf0) == 0)
                        {
                            lst = here;
                            for (; ; )
                            {
                                here = distcode[ZInfTrees.Val(lst) + (BitsOf(ZInfTrees.Bits(lst) + ZInfTrees.Op(lst)) >> (int)ZInfTrees.Bits(lst))];
                                if (ZInfTrees.Bits(lst) + ZInfTrees.Bits(here) <= bts) break;
                                if (!Pull()) goto inf_leave;
                            }
                            Drop(ZInfTrees.Bits(lst));
                            back += (int)ZInfTrees.Bits(lst);
                        }
                        Drop(ZInfTrees.Bits(here));
                        back += (int)ZInfTrees.Bits(here);
                        if ((ZInfTrees.Op(here) & 64) != 0) { SetBad("invalid distance code"); break; }
                        offset = ZInfTrees.Val(here);
                        extra = ZInfTrees.Op(here) & MAX_BITS;
                        mode = Mode.DISTEXT;
                        goto case Mode.DISTEXT;
                    case Mode.DISTEXT:
                        if (extra != 0)
                        {
                            while (bts < extra) if (!Pull()) goto inf_leave;
                            offset += BitsOf(extra);
                            Drop(extra);
                            back += (int)extra;
                        }
                        mode = Mode.MATCH;
                        goto case Mode.MATCH;
                    case Mode.MATCH:
                        if (left == 0) goto inf_leave;
                        copy = outBytes - left;
                        if (offset > copy)
                        {
                            copy = offset - copy;
                            if (copy > whave) { SetBad("invalid distance too far back"); break; }
                            if (copy > wnext)
                            {
                                copy -= wnext;
                                from = window + (wsize - copy);
                            }
                            else from = window + (wnext - copy);
                            copy = Math.Min(copy, length);
                            copy = Math.Min(copy, left);
                            put = Copy(put, from, copy);
                        }
                        else
                        {
                            copy = Math.Min(length, left);
                            put = Copy(put, put - offset, copy);
                        }
                        left -= copy;
                        length -= copy;
                        if (length == 0) mode = Mode.LEN;
                        break;
                    case Mode.LIT:
                        if (left == 0) goto inf_leave;
                        *put++ = (byte)length;
                        left--;
                        mode = Mode.LEN;
                        break;
                    case Mode.CHECK:
                        if (wrap != 0)
                        {
                            while (bts < 32) if (!Pull()) goto inf_leave;
                            outBytes -= left;
                            TotalOut += outBytes;
                            total += outBytes;
                            if ((wrap & 4) != 0)
                            {
                                if (outBytes != 0) Chksum(put - outBytes, outBytes);
                                if (flags != 0) Adler = check = crcFold;
                            }
                            outBytes = left;
                            if ((wrap & 4) != 0 && (flags != 0 ? (uint)hld : Swap32((uint)hld)) != check)
                            {
                                // (the comparison is of the whole hold with the check in C: hold has 32 bits here)
                                SetBad("incorrect data check");
                                break;
                            }
                            hld = 0; bts = 0;
                        }
                        mode = Mode.LENGTH;
                        goto case Mode.LENGTH;
                    case Mode.LENGTH:
                        if (wrap != 0 && flags != 0)
                        {
                            while (bts < 32) if (!Pull()) goto inf_leave;
                            if ((wrap & 4) != 0 && hld != total) { SetBad("incorrect length check"); break; }
                            hld = 0; bts = 0;
                        }
                        mode = Mode.DONE;
                        goto case Mode.DONE;
                    case Mode.DONE:
                        ret = ZConst.Z_STREAM_END;
                        goto inf_leave;
                    case Mode.BAD:
                        ret = ZConst.Z_DATA_ERROR;
                        goto inf_leave;
                    default:
                        return ZConst.Z_STREAM_ERROR;
                }
            }
        inf_leave:
            NextOut = put; AvailOut = left; NextIn = nxt; AvailIn = hv; hold = hld; bits = bts;
            uint checkBytes = outBytes - AvailOut;
            if (wsize != 0 || (outBytes != AvailOut && mode < Mode.BAD && (mode < Mode.CHECK || flush != ZConst.Z_FINISH)))
                UpdateWindow(NextOut, checkBytes, (wrap & 4) != 0);
            inBytes -= AvailIn;
            outBytes -= AvailOut;
            TotalIn += inBytes;
            TotalOut += outBytes;
            total += outBytes;
            DataType = (int)bits + (last != 0 ? 64 : 0) + (mode == Mode.TYPE ? 128 : 0) + (mode == Mode.LEN_ || mode == Mode.COPY_ ? 256 : 0);
            if (((inBytes == 0 && outBytes == 0) || flush == ZConst.Z_FINISH) && ret == ZConst.Z_OK)
            {
                if (wsize == 0 && flush == ZConst.Z_FINISH) Chksum(put - checkBytes, checkBytes);
                ret = ZConst.Z_BUF_ERROR;
            }
            return ret;
        }

        /// <summary>inflateEnd's result (the block is freed by the caller).</summary>
        public int End() => ZConst.Z_OK;

        // ---------------- inffast_tpl.h ----------------
        void InflateFast(uint start)
        {
            byte* inp = NextIn;
            byte* lastIn = inp + (AvailIn - (INFLATE_FAST_MIN_HAVE - 1));
            byte* outp = NextOut;
            byte* beg = outp - (start - AvailOut);
            byte* end = outp + (AvailOut - (INFLATE_FAST_MIN_LEFT - 1));
            uint ws = wsize, wh = whave, wn = wnext;
            byte* win = window;
            ulong hld = hold;
            uint bts = bits;
            uint* lcode = lencode;
            uint* dcode = distcode;
            uint lmask = (1u << (int)lenbits) - 1;
            uint dmask = (1u << (int)distbits) - 1;
            uint here, op, len, dist;
            byte* from;

            do
            {
                // REFILL
                hld |= *(ulong*)inp << (int)bts;
                inp += 7;
                inp -= (bts >> 3) & 7;
                bts |= 56;
                here = lcode[hld & lmask];
                if (ZInfTrees.Op(here) == 0)
                {
                    *outp++ = (byte)ZInfTrees.Val(here);
                    hld >>= (int)ZInfTrees.Bits(here); bts -= ZInfTrees.Bits(here);
                    here = lcode[hld & lmask];
                    if (ZInfTrees.Op(here) == 0)
                    {
                        *outp++ = (byte)ZInfTrees.Val(here);
                        hld >>= (int)ZInfTrees.Bits(here); bts -= ZInfTrees.Bits(here);
                        here = lcode[hld & lmask];
                    }
                }
            dolen:
                hld >>= (int)ZInfTrees.Bits(here); bts -= ZInfTrees.Bits(here);
                op = ZInfTrees.Op(here);
                if (op == 0) *outp++ = (byte)ZInfTrees.Val(here);
                else if ((op & 16) != 0)
                {
                    len = ZInfTrees.Val(here);
                    op &= MAX_BITS;
                    len += (uint)(hld & ((1ul << (int)op) - 1));
                    hld >>= (int)op; bts -= op;
                    here = dcode[hld & dmask];
                    if (bts < MAX_BITS + MAX_DIST_EXTRA_BITS)
                    {
                        hld |= *(ulong*)inp << (int)bts;
                        inp += 7;
                        inp -= (bts >> 3) & 7;
                        bts |= 56;
                    }
                dodist:
                    hld >>= (int)ZInfTrees.Bits(here); bts -= ZInfTrees.Bits(here);
                    op = ZInfTrees.Op(here);
                    if ((op & 16) != 0)
                    {
                        dist = ZInfTrees.Val(here);
                        op &= MAX_BITS;
                        dist += (uint)(hld & ((1ul << (int)op) - 1));
                        hld >>= (int)op; bts -= op;
                        op = (uint)(outp - beg);
                        if (dist > op)
                        {
                            op = dist - op;
                            if (op > wh)
                            {
                                mode = Mode.BAD; Msg = "invalid distance too far back";
                                break;
                            }
                            from = win;
                            if (wn == 0) from += ws - op;
                            else if (wn >= op) from += wn - op;
                            else
                            {
                                op -= wn;
                                from += ws - op;
                                if (op < len)
                                {
                                    len -= op;
                                    outp = Copy(outp, from, op);
                                    from = win;
                                    op = wn;
                                }
                            }
                            if (op < len)
                            {
                                len -= op;
                                outp = Copy(outp, from, op);
                                outp = Copy(outp, outp - dist, len);
                            }
                            else outp = Copy(outp, from, len);
                        }
                        else outp = Copy(outp, outp - dist, len);
                    }
                    else if ((op & 64) == 0)
                    {
                        here = dcode[ZInfTrees.Val(here) + (uint)(hld & ((1ul << (int)op) - 1))];
                        goto dodist;
                    }
                    else
                    {
                        mode = Mode.BAD; Msg = "invalid distance code";
                        break;
                    }
                }
                else if ((op & 64) == 0)
                {
                    here = lcode[ZInfTrees.Val(here) + (uint)(hld & ((1ul << (int)op) - 1))];
                    goto dolen;
                }
                else if ((op & 32) != 0)
                {
                    mode = Mode.TYPE;
                    break;
                }
                else
                {
                    mode = Mode.BAD; Msg = "invalid literal/length code";
                    break;
                }
            } while (inp < lastIn && outp < end);
            len = bts >> 3;
            inp -= len;
            bts -= len << 3;
            hld &= (1ul << (int)bts) - 1;
            NextIn = inp;
            NextOut = outp;
            AvailIn = (uint)(inp < lastIn ? (INFLATE_FAST_MIN_HAVE - 1) + (lastIn - inp) : (INFLATE_FAST_MIN_HAVE - 1) - (inp - lastIn));
            AvailOut = (uint)(outp < end ? (INFLATE_FAST_MIN_LEFT - 1) + (end - outp) : (INFLATE_FAST_MIN_LEFT - 1) - (outp - end));
            hold = (uint)hld;
            bits = bts;
        }
    }
}
