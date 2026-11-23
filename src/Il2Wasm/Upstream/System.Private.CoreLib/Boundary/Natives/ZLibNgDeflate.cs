// il2wasm: zlib-ng's deflate, ported to C#, so that il2wasm's libSystem.IO.Compression.Native compresses to the same
// bytes as the browser-wasm runtime's (SPEC 7: determinism; docs/CORELIB.md section 7).
//
// Ported from zlib-ng 2.2.5 as dotnet/runtime v11.0.0 vendors it (src/native/external/zlib-ng; zlib-ng commit
// d54e3769be0c522015b784eca2af258b1c026107): deflate.c, deflate.h, deflate_p.h, deflate_stored.c, deflate_fast.c,
// deflate_medium.c, deflate_slow.c, deflate_quick.c, deflate_huff.c, deflate_rle.c, trees.c, trees.h, trees_emit.h,
// trees_tbl.h (computed here as trees.c's generator does), insert_string.c, insert_string_roll.c,
// insert_string_tpl.h, match_tpl.h and arch/generic (compare256_c.c, slide_hash_c.c), with the generic C paths the
// WASM build compiles: zlib-compatible API, LIT_MEM on, no dictionary, no gzip header record. This is an altered
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
    internal static class ZConst
    {
        public const int Z_NO_FLUSH = 0, Z_PARTIAL_FLUSH = 1, Z_SYNC_FLUSH = 2, Z_FULL_FLUSH = 3, Z_FINISH = 4, Z_BLOCK = 5;
        public const int Z_OK = 0, Z_STREAM_END = 1, Z_NEED_DICT = 2, Z_STREAM_ERROR = -2, Z_DATA_ERROR = -3, Z_MEM_ERROR = -4, Z_BUF_ERROR = -5;
        public const int Z_FILTERED = 1, Z_HUFFMAN_ONLY = 2, Z_RLE = 3, Z_FIXED = 4;
        public const int Z_BINARY = 0, Z_TEXT = 1, Z_UNKNOWN = 2;
        public const int Z_DEFLATED = 8;

        /// <summary>zutil.c's z_errmsg, by ERR_MSG's index.</summary>
        public static string ErrMsg(int err) => (err < -6 || err > 2 ? 9 : 2 - err) switch
        {
            0 => "need dictionary", 1 => "stream end", 2 => "", 3 => "file error", 4 => "stream error",
            5 => "data error", 6 => "insufficient memory", 7 => "buffer error", 8 => "incompatible version", _ => "",
        };
    }

    /// <summary>The stream fields deflate reads and writes (z_stream's).</summary>
    internal unsafe class ZStreamFields
    {
        public byte* NextIn;
        public uint AvailIn;
        public ulong TotalIn;
        public byte* NextOut;
        public uint AvailOut;
        public ulong TotalOut;
        /// <summary>z_errmsg's text, or null.</summary>
        public string? Msg;
        public uint Adler;
        public int DataType;
    }

    /// <summary>trees_tbl.h's tables, made as trees.c's gen_trees_header makes them.</summary>
    internal static unsafe class ZTrees
    {
        public const int LENGTH_CODES = 29, LITERALS = 256, L_CODES = LITERALS + 1 + LENGTH_CODES, D_CODES = 30, BL_CODES = 19;
        public const int HEAP_SIZE = 2 * L_CODES + 1, END_BLOCK = 256, MAX_BITS = 15, MAX_BL_BITS = 7;
        public const int REP_3_6 = 16, REPZ_3_10 = 17, REPZ_11_138 = 18, DIST_CODE_LEN = 512;
        public static readonly int[] ExtraLBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
        public static readonly int[] ExtraDBits = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
        public static readonly int[] ExtraBlBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 7];
        public static readonly byte[] BlOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

        /// <summary>ct_data as two ushorts each: [2n] Freq/Code, [2n+1] Dad/Len.</summary>
        public static readonly ushort* StaticLTree, StaticDTree;
        public static readonly byte* DistCode, LengthCode;
        public static readonly int[] BaseLength = new int[LENGTH_CODES], BaseDist = new int[D_CODES];

        static ZTrees()
        {
            StaticLTree = (ushort*)NativeMemory.AllocZeroed((nuint)((L_CODES + 2) * 4));
            StaticDTree = (ushort*)NativeMemory.AllocZeroed((nuint)(D_CODES * 4));
            DistCode = (byte*)NativeMemory.AllocZeroed(DIST_CODE_LEN);
            LengthCode = (byte*)NativeMemory.AllocZeroed(256);
            int length = 0, code;
            for (code = 0; code < LENGTH_CODES - 1; code++)
            {
                BaseLength[code] = length;
                for (int n = 0; n < (1 << ExtraLBits[code]); n++) LengthCode[length++] = (byte)code;
            }
            LengthCode[length - 1] = (byte)code;
            int dist = 0;
            for (code = 0; code < 16; code++)
            {
                BaseDist[code] = dist;
                for (int n = 0; n < (1 << ExtraDBits[code]); n++) DistCode[dist++] = (byte)code;
            }
            dist >>= 7;
            for (; code < D_CODES; code++)
            {
                BaseDist[code] = dist << 7;
                for (int n = 0; n < (1 << (ExtraDBits[code] - 7)); n++) DistCode[256 + dist++] = (byte)code;
            }
            ushort* blCount = stackalloc ushort[MAX_BITS + 1];
            for (int b = 0; b <= MAX_BITS; b++) blCount[b] = 0;
            int i = 0;
            while (i <= 143) { StaticLTree[2 * i++ + 1] = 8; blCount[8]++; }
            while (i <= 255) { StaticLTree[2 * i++ + 1] = 9; blCount[9]++; }
            while (i <= 279) { StaticLTree[2 * i++ + 1] = 7; blCount[7]++; }
            while (i <= 287) { StaticLTree[2 * i++ + 1] = 8; blCount[8]++; }
            GenCodes(StaticLTree, L_CODES + 1, blCount);
            for (i = 0; i < D_CODES; i++)
            {
                StaticDTree[2 * i + 1] = 5;
                StaticDTree[2 * i] = BiReverse((uint)i, 5);
            }
        }

        public static ushort BiReverse(uint code, int len)
        {
            static byte BitRev8(uint b) => (byte)(((((ulong)(byte)b * 0x80200802UL) & 0x0884422110UL) * 0x0101010101UL) >> 32);
            return (ushort)((BitRev8(code >> 8) | (uint)BitRev8(code) << 8) >> (16 - len));
        }

        public static void GenCodes(ushort* tree, int maxCode, ushort* blCount)
        {
            ushort* nextCode = stackalloc ushort[MAX_BITS + 1];
            uint code = 0;
            for (int bits = 1; bits <= MAX_BITS; bits++)
            {
                code = (code + blCount[bits - 1]) << 1;
                nextCode[bits] = (ushort)code;
            }
            for (int n = 0; n <= maxCode; n++)
            {
                int len = tree[2 * n + 1];
                if (len == 0) continue;
                tree[2 * n] = BiReverse(nextCode[len]++, len);
            }
        }

        public static int DCode(uint dist) => dist < 256 ? DistCode[dist] : DistCode[256 + (dist >> 7)];
    }

    /// <summary>deflate_state, and deflate.c and its strategies over it.</summary>
    internal sealed unsafe class ZngDeflate : ZStreamFields
    {
        const int INIT_STATE = 1, GZIP_STATE = 4, BUSY_STATE = 2, FINISH_STATE = 3;
        const uint HASH_SIZE = 65536, HASH_MASK = HASH_SIZE - 1;
        const int STD_MIN_MATCH = 3, STD_MAX_MATCH = 258, WANT_MIN_MATCH = 4;
        const int MIN_LOOKAHEAD = STD_MAX_MATCH + STD_MIN_MATCH + 1, WIN_INIT = STD_MAX_MATCH;
        const int BIT_BUF_SIZE = 64, MAX_STORED = 65535, LIT_BUFS = 5;
        const int MAX_MEM_LEVEL = 9, MIN_WBITS = 8, MAX_WBITS = 15;
        const int STORED_BLOCK = 0, STATIC_TREES = 1, DYN_TREES = 2;
        const int PRESET_DICT = 0x20;
        /// <summary>zutil.h's OS_CODE for Emscripten: "assume Unix".</summary>
        const int OS_CODE = 3;

        enum BlockState { NeedMore, BlockDone, FinishStarted, FinishDone }

        // deflate.c's configuration_table: good_length, max_lazy, nice_length, max_chain, function
        static readonly ushort[] s_good = [0, 0, 4, 4, 4, 8, 8, 8, 32, 32];
        static readonly ushort[] s_lazy = [0, 0, 4, 6, 12, 16, 16, 32, 128, 258];
        static readonly ushort[] s_nice = [0, 0, 8, 16, 32, 32, 128, 128, 258, 258];
        static readonly ushort[] s_chain = [0, 0, 4, 6, 24, 32, 128, 256, 1024, 4096];
        // 0 stored, 1 quick, 2 fast, 3 medium, 4 slow
        static readonly byte[] s_func = [0, 1, 2, 3, 3, 3, 3, 4, 4, 4];

        byte* _block;
        byte* pending_buf, pending_out;
        uint pending_buf_size, pending;
        int wrap, status, last_flush, block_open;
        uint w_size, w_bits, w_mask, lookahead, high_water, window_size;
        byte* window;
        ushort* prev, head;
        uint ins_h;
        int block_start;
        uint match_length, prev_match, strstart, match_start, prev_length;
        int match_available;
        uint max_chain_length, max_lazy_match, good_match;
        int nice_match;
        bool roll;
        int level, strategy;
        uint crc;
        ushort* dyn_ltree, dyn_dtree, bl_tree;
        int l_max_code, d_max_code, bl_max_code;
        ushort* bl_count;
        int* heap;
        int heap_len, heap_max;
        byte* depth;
        uint lit_bufsize;
        ushort* d_buf;
        byte* l_buf;
        uint sym_next, sym_end;
        uint opt_len, static_len, matches, insert;
        ulong bi_buf;
        int bi_valid;

        ZngDeflate() { }

        static nuint Pad64(nuint n) => (n + 63) & ~(nuint)63;

        /// <summary>deflateInit2: the state, or the error (Z_STREAM_ERROR, Z_MEM_ERROR) and null.</summary>
        public static int Init2(int level, int method, int windowBits, int memLevel, int strategy, out ZngDeflate? state)
        {
            state = null;
            int wrap = 1;
            if (level == -1) level = 6;
            if (windowBits < 0)
            {
                wrap = 0;
                if (windowBits < -MAX_WBITS) return ZConst.Z_STREAM_ERROR;
                windowBits = -windowBits;
            }
            else if (windowBits > MAX_WBITS)
            {
                wrap = 2;
                windowBits -= 16;
            }
            if (memLevel < 1 || memLevel > MAX_MEM_LEVEL || method != ZConst.Z_DEFLATED || windowBits < MIN_WBITS ||
                windowBits > MAX_WBITS || level < 0 || level > 9 || strategy < 0 || strategy > ZConst.Z_FIXED ||
                (windowBits == 8 && wrap != 1))
                return ZConst.Z_STREAM_ERROR;
            if (windowBits == 8) windowBits = 9;
            uint litBufsize = 1u << (memLevel + 6);
            var s = new ZngDeflate();
            // (one block, as alloc_deflate lays it out: window, prev, head, pending; then the trees, which are part
            // of deflate_state; all zeroed, and the window padded for the 8-byte reads past its end)
            nuint wsz = (nuint)((1 << windowBits) * 2), prevSz = (nuint)((1 << windowBits) * 2), headSz = HASH_SIZE * 2, pendSz = litBufsize * LIT_BUFS;
            nuint treesSz = (nuint)((ZTrees.HEAP_SIZE + 2 * ZTrees.D_CODES + 1 + 2 * ZTrees.BL_CODES + 1) * 4 + (ZTrees.MAX_BITS + 1) * 2 + (2 * ZTrees.L_CODES + 1) * 4 + 2 * ZTrees.L_CODES + 1);
            nuint at = 0;
            nuint wAt = at; at = Pad64(at + wsz + 64);
            nuint pAt = at; at = Pad64(at + prevSz);
            nuint hAt = at; at = Pad64(at + headSz);
            nuint bAt = at; at = Pad64(at + pendSz);
            nuint tAt = at; at = Pad64(at + treesSz);
            byte* blk = (byte*)NativeMemory.AllocZeroed(at);
            if (blk == null) return ZConst.Z_MEM_ERROR;
            s._block = blk;
            s.window = blk + wAt;
            s.prev = (ushort*)(blk + pAt);
            s.head = (ushort*)(blk + hAt);
            s.pending_buf = blk + bAt;
            byte* t = blk + tAt;
            s.dyn_ltree = (ushort*)t; t += ZTrees.HEAP_SIZE * 4;
            s.dyn_dtree = (ushort*)t; t += (2 * ZTrees.D_CODES + 1) * 4;
            s.bl_tree = (ushort*)t; t += (2 * ZTrees.BL_CODES + 1) * 4;
            s.bl_count = (ushort*)t; t += (ZTrees.MAX_BITS + 1) * 2;
            s.heap = (int*)t; t += (2 * ZTrees.L_CODES + 1) * 4;
            s.depth = t;
            s.status = INIT_STATE;
            s.wrap = wrap;
            s.w_bits = (uint)windowBits;
            s.w_size = 1u << windowBits;
            s.w_mask = s.w_size - 1;
            s.high_water = 0;
            s.lit_bufsize = litBufsize;
            s.pending_buf_size = litBufsize * 4;
            s.d_buf = (ushort*)(s.pending_buf + (litBufsize << 1));
            s.l_buf = s.pending_buf + (litBufsize << 2);
            s.sym_end = litBufsize - 1;
            s.level = level;
            s.strategy = strategy;
            s.block_open = 0;
            state = s;
            return s.Reset();
        }

        public void Free()
        {
            if (_block != null) NativeMemory.Free(_block);
            _block = null;
        }

        int ResetKeep()
        {
            TotalIn = TotalOut = 0;
            Msg = null;
            DataType = ZConst.Z_UNKNOWN;
            pending = 0;
            pending_out = pending_buf;
            if (wrap < 0) wrap = -wrap;
            status = wrap == 2 ? GZIP_STATE : INIT_STATE;
            if (wrap == 2) { crc = 0; Adler = 0; }
            else Adler = 1;
            last_flush = -2;
            TrInit();
            return ZConst.Z_OK;
        }

        public int Reset()
        {
            int ret = ResetKeep();
            if (ret == ZConst.Z_OK) LmInit();
            return ret;
        }

        /// <summary>deflateEnd's result (the block is freed by the caller).</summary>
        public int End() => status == BUSY_STATE ? ZConst.Z_DATA_ERROR : ZConst.Z_OK;

        void LmSetLevel(int lvl)
        {
            max_lazy_match = s_lazy[lvl];
            good_match = s_good[lvl];
            nice_match = s_nice[lvl];
            max_chain_length = s_chain[lvl];
            roll = max_chain_length > 1024;
            level = lvl;
        }

        void LmInit()
        {
            window_size = 2 * w_size;
            new Span<byte>(head, (int)(HASH_SIZE * 2)).Clear();
            LmSetLevel(level);
            strstart = 0;
            block_start = 0;
            lookahead = 0;
            insert = 0;
            prev_length = 0;
            match_available = 0;
            match_start = 0;
            ins_h = 0;
        }

        // ---------------- deflate.h's put_* ----------------
        void PutByte(uint c) => pending_buf[pending++] = (byte)c;
        void PutShort(ushort w) { pending_buf[pending] = (byte)w; pending_buf[pending + 1] = (byte)(w >> 8); pending += 2; }
        void PutShortMsb(ushort w) { pending_buf[pending] = (byte)(w >> 8); pending_buf[pending + 1] = (byte)w; pending += 2; }
        void PutUint32(uint d) { *(uint*)(pending_buf + pending) = d; pending += 4; }
        void PutUint32Msb(uint d) { PutShortMsb((ushort)(d >> 16)); PutShortMsb((ushort)d); }
        void PutUint64(ulong d) { *(ulong*)(pending_buf + pending) = d; pending += 8; }

        uint MaxDist => w_size - MIN_LOOKAHEAD;

        // ---------------- insert_string_tpl.h, both variants ----------------
        static uint HashMul(uint val) => ((val * 2654435761U) >> 16) & HASH_MASK;

        /// <summary>s->update_hash: update_hash_roll from level 9, else update_hash (whose HASH_CALC ignores h).</summary>
        uint UpdateHash(uint h, uint val) => roll ? ((h << 5) ^ (byte)val) & (32768u - 1u) : HashMul(val);

        /// <summary>quick_insert_string (the multiplicative hash: deflate_fast, medium and quick call it directly).</summary>
        ushort QuickInsertString(uint str)
        {
            uint val = *(uint*)(window + str);
            uint hm = HashMul(val);
            ushort h = head[hm];
            if (h != str)
            {
                prev[str & w_mask] = h;
                head[hm] = (ushort)str;
            }
            return h;
        }

        void InsertString(uint str, uint count)
        {
            byte* p = window + str, end = p + count;
            for (ushort idx = (ushort)str; p < end; idx++, p++)
            {
                uint hm = HashMul(*(uint*)p);
                ushort h = head[hm];
                if (h != idx)
                {
                    prev[idx & w_mask] = h;
                    head[hm] = idx;
                }
            }
        }

        ushort QuickInsertStringRoll(uint str)
        {
            ins_h = ((ins_h << 5) ^ window[str + STD_MIN_MATCH - 1]) & (32768u - 1u);
            uint hm = ins_h;
            ushort h = head[hm];
            if (h != str)
            {
                prev[str & w_mask] = h;
                head[hm] = (ushort)str;
            }
            return h;
        }

        void InsertStringRoll(uint str, uint count)
        {
            byte* p = window + str + STD_MIN_MATCH - 1, end = p + count;
            for (ushort idx = (ushort)str; p < end; idx++, p++)
            {
                ins_h = ((ins_h << 5) ^ *p) & (32768u - 1u);
                uint hm = ins_h;
                ushort h = head[hm];
                if (h != idx)
                {
                    prev[idx & w_mask] = h;
                    head[hm] = idx;
                }
            }
        }

        // s->insert_string / s->quick_insert_string (the roll variants from level 9)
        ushort SQuickInsertString(uint str) => roll ? QuickInsertStringRoll(str) : QuickInsertString(str);
        void SInsertString(uint str, uint count) { if (roll) InsertStringRoll(str, count); else InsertString(str, count); }

        // ---------------- slide_hash_c.c ----------------
        void SlideHash()
        {
            ushort wsize = (ushort)w_size;
            for (uint i = 0; i < HASH_SIZE; i++) { ushort m = head[i]; head[i] = (ushort)(m >= wsize ? m - wsize : 0); }
            for (uint i = 0; i < wsize; i++) { ushort m = prev[i]; prev[i] = (ushort)(m >= wsize ? m - wsize : 0); }
        }

        // ---------------- compare256 (every variant gives the length of the common prefix, at most 256) ----------------
        static uint Compare256(byte* a, byte* b)
        {
            uint len = 0;
            do
            {
                if (a[len] != b[len]) return len;
                len++;
            } while (len < 256);
            return 256;
        }

        // ---------------- match_tpl.h: longest_match_c and longest_match_slow_c ----------------
        uint LongestMatch(uint curMatch, bool slow)
        {
            uint wmask = w_mask;
            byte* scan = window + strstart;
            byte* mbaseStart = window;
            byte* mbaseEnd;
            uint limit, limitBase = 0;
            bool earlyExit = false;
            uint chainLength, niceMatch, bestLen, offset;
            uint look = lookahead;
            uint matchOffset = 0;
            ulong scanStart, scanEnd;

            bestLen = prev_length != 0 ? prev_length : STD_MIN_MATCH - 1;
            offset = bestLen - 1;
            if (bestLen >= 4)
            {
                offset -= 2;
                if (bestLen >= 8) offset -= 4;
            }
            scanStart = *(ulong*)scan;
            scanEnd = *(ulong*)(scan + offset);
            mbaseEnd = mbaseStart + offset;
            chainLength = max_chain_length;
            if (bestLen >= good_match) chainLength >>= 2;
            niceMatch = (uint)nice_match;
            limit = strstart > MaxDist ? (ushort)(strstart - MaxDist) : 0u;
            if (slow)
            {
                limitBase = limit;
                if (bestLen >= STD_MIN_MATCH)
                {
                    uint hash = UpdateHash(0, scan[1]);
                    hash = UpdateHash(hash, scan[2]);
                    for (uint i = 3; i <= bestLen; i++)
                    {
                        hash = UpdateHash(hash, scan[i]);
                        uint pos = head[hash];
                        if (pos < curMatch)
                        {
                            matchOffset = (ushort)(i - 2);
                            curMatch = pos;
                        }
                    }
                    limit = (ushort)(limitBase + matchOffset);
                    if (curMatch <= limit) goto break_matching;
                    mbaseStart -= matchOffset;
                    mbaseEnd -= matchOffset;
                }
            }
            else earlyExit = level < 5;

            for (; ; )
            {
                if (curMatch >= strstart) break;
                if (bestLen < 4)
                {
                    for (; ; )
                    {
                        if (*(ushort*)(mbaseEnd + curMatch) == (ushort)scanEnd && *(ushort*)(mbaseStart + curMatch) == (ushort)scanStart) break;
                        if (--chainLength != 0 && (curMatch = prev[curMatch & wmask]) > limit) continue;
                        return bestLen;
                    }
                }
                else if (bestLen >= 8)
                {
                    for (; ; )
                    {
                        if (*(ulong*)(mbaseEnd + curMatch) == scanEnd && *(ulong*)(mbaseStart + curMatch) == scanStart) break;
                        if (--chainLength != 0 && (curMatch = prev[curMatch & wmask]) > limit) continue;
                        return bestLen;
                    }
                }
                else
                {
                    for (; ; )
                    {
                        if (*(uint*)(mbaseEnd + curMatch) == (uint)scanEnd && *(uint*)(mbaseStart + curMatch) == (uint)scanStart) break;
                        if (--chainLength != 0 && (curMatch = prev[curMatch & wmask]) > limit) continue;
                        return bestLen;
                    }
                }
                uint len = Compare256(scan + 2, mbaseStart + curMatch + 2) + 2;
                if (len > bestLen)
                {
                    uint ms = curMatch - matchOffset;
                    match_start = ms;
                    if (len > look) return look;
                    bestLen = len;
                    if (bestLen >= niceMatch) return bestLen;
                    offset = bestLen - 1;
                    if (bestLen >= 4)
                    {
                        offset -= 2;
                        if (bestLen >= 8) offset -= 4;
                    }
                    scanEnd = *(ulong*)(scan + offset);
                    if (slow && len > STD_MIN_MATCH && ms + len < strstart)
                    {
                        uint pos, nextPos;
                        curMatch -= matchOffset;
                        matchOffset = 0;
                        nextPos = curMatch;
                        for (uint i = 0; i <= len - STD_MIN_MATCH; i++)
                        {
                            pos = prev[(curMatch + i) & wmask];
                            if (pos < nextPos)
                            {
                                if (pos <= limitBase + i) goto break_matching;
                                nextPos = pos;
                                matchOffset = (ushort)i;
                            }
                        }
                        curMatch = nextPos;
                        byte* scanEndStr = scan + len - (STD_MIN_MATCH + 1);
                        uint hash = UpdateHash(0, scanEndStr[0]);
                        hash = UpdateHash(hash, scanEndStr[1]);
                        hash = UpdateHash(hash, scanEndStr[2]);
                        pos = head[hash];
                        if (pos < curMatch)
                        {
                            matchOffset = (ushort)(len - (STD_MIN_MATCH + 1));
                            if (pos <= limitBase + matchOffset) goto break_matching;
                            curMatch = pos;
                        }
                        limit = (ushort)(limitBase + matchOffset);
                        mbaseStart = window - matchOffset;
                        mbaseEnd = mbaseStart + offset;
                        continue;
                    }
                    mbaseEnd = mbaseStart + offset;
                }
                else if (earlyExit) break;
                if (--chainLength != 0 && (curMatch = prev[curMatch & wmask]) > limit) continue;
                return bestLen;
            }
            return bestLen;
        break_matching:
            return bestLen < lookahead ? bestLen : lookahead;
        }

        // ---------------- deflate.c: read_buf, fill_window, flush_pending ----------------
        uint ReadBuf(byte* buf, uint size)
        {
            uint len = Math.Min(AvailIn, size);
            if (len == 0) return 0;
            AvailIn -= len;
            Buffer.MemoryCopy(NextIn, buf, len, len);
            if (wrap == 2) crc = Crc32.Update(crc, buf, (int)len);
            else if (wrap == 1) Adler = Adler32.Update(Adler, buf, (int)len);
            NextIn += len;
            TotalIn += len;
            return len;
        }

        void FillWindow()
        {
            uint n, more, wsize = w_size;
            do
            {
                more = window_size - lookahead - strstart;
                if (strstart >= wsize + MaxDist)
                {
                    Buffer.MemoryCopy(window + wsize, window, wsize, wsize);
                    if (match_start >= wsize) match_start -= wsize;
                    else { match_start = 0; prev_length = 0; }
                    strstart -= wsize;
                    block_start -= (int)wsize;
                    if (insert > strstart) insert = strstart;
                    SlideHash();
                    more += wsize;
                }
                if (AvailIn == 0) break;
                n = ReadBuf(window + strstart + lookahead, more);
                lookahead += n;
                if (lookahead + insert >= STD_MIN_MATCH)
                {
                    uint str = strstart - insert;
                    if (max_chain_length > 1024) ins_h = UpdateHash(window[str], window[str + 1]);
                    else if (str >= 1) SQuickInsertString(str + 2 - STD_MIN_MATCH);
                    uint count = insert;
                    if (lookahead == 1) count -= 1;
                    if (count > 0)
                    {
                        SInsertString(str, count);
                        insert -= count;
                    }
                }
            } while (lookahead < MIN_LOOKAHEAD && AvailIn != 0);
            if (high_water < window_size)
            {
                uint curr = strstart + lookahead, init;
                if (high_water < curr)
                {
                    init = window_size - curr;
                    if (init > WIN_INIT) init = WIN_INIT;
                    new Span<byte>(window + curr, (int)init).Clear();
                    high_water = curr + init;
                }
                else if (high_water < curr + WIN_INIT)
                {
                    init = curr + WIN_INIT - high_water;
                    if (init > window_size - high_water) init = window_size - high_water;
                    new Span<byte>(window + high_water, (int)init).Clear();
                    high_water += init;
                }
            }
        }

        void FlushPending()
        {
            TrFlushBits();
            uint len = Math.Min(pending, AvailOut);
            if (len == 0) return;
            Buffer.MemoryCopy(pending_out, NextOut, len, len);
            NextOut += len;
            pending_out += len;
            TotalOut += len;
            AvailOut -= len;
            pending -= len;
            if (pending == 0) pending_out = pending_buf;
        }

        static int Rank(int f) => f * 2 - (f > 4 ? 9 : 0);

        int ErrReturn(int err) { Msg = ZConst.ErrMsg(err); return err; }

        // ---------------- deflate.c: deflate ----------------
        public int Deflate(int flush)
        {
            if (flush > ZConst.Z_BLOCK || flush < 0) return ZConst.Z_STREAM_ERROR;
            if (NextOut == null || (AvailIn != 0 && NextIn == null) || (status == FINISH_STATE && flush != ZConst.Z_FINISH))
                return ErrReturn(ZConst.Z_STREAM_ERROR);
            if (AvailOut == 0) return ErrReturn(ZConst.Z_BUF_ERROR);
            int oldFlush = last_flush;
            last_flush = flush;
            if (pending != 0)
            {
                FlushPending();
                if (AvailOut == 0) { last_flush = -1; return ZConst.Z_OK; }
            }
            else if (AvailIn == 0 && Rank(flush) <= Rank(oldFlush) && flush != ZConst.Z_FINISH)
                return ErrReturn(ZConst.Z_BUF_ERROR);
            if (status == FINISH_STATE && AvailIn != 0) return ErrReturn(ZConst.Z_BUF_ERROR);
            if (status == INIT_STATE && wrap == 0) status = BUSY_STATE;
            if (status == INIT_STATE)
            {
                uint header = (uint)(ZConst.Z_DEFLATED + ((w_bits - 8) << 4)) << 8;
                uint levelFlags;
                if (strategy >= ZConst.Z_HUFFMAN_ONLY || level < 2) levelFlags = 0;
                else if (level < 6) levelFlags = 1;
                else if (level == 6) levelFlags = 2;
                else levelFlags = 3;
                header |= levelFlags << 6;
                if (strstart != 0) header |= PRESET_DICT;
                header += 31 - (header % 31);
                PutShortMsb((ushort)header);
                if (strstart != 0) PutUint32Msb(Adler);
                Adler = 1;
                status = BUSY_STATE;
                FlushPending();
                if (pending != 0) { last_flush = -1; return ZConst.Z_OK; }
            }
            if (status == GZIP_STATE)
            {
                crc = 0;
                PutByte(31);
                PutByte(139);
                PutByte(8);
                // (no header record: .NET sets none)
                PutUint32(0);
                PutByte(0);
                PutByte(level == 9 ? 2u : (strategy >= ZConst.Z_HUFFMAN_ONLY || level < 2 ? 4u : 0u));
                PutByte(OS_CODE);
                status = BUSY_STATE;
                FlushPending();
                if (pending != 0) { last_flush = -1; return ZConst.Z_OK; }
            }
            if (AvailIn != 0 || lookahead != 0 || (flush != ZConst.Z_NO_FLUSH && status != FINISH_STATE))
            {
                BlockState bstate = level == 0 ? DeflateStored(flush)
                    : strategy == ZConst.Z_HUFFMAN_ONLY ? DeflateHuff(flush)
                    : strategy == ZConst.Z_RLE ? DeflateRle(flush)
                    : s_func[level] switch { 1 => DeflateQuick(flush), 2 => DeflateFast(flush), 3 => DeflateMedium(flush), _ => DeflateSlow(flush) };
                if (bstate == BlockState.FinishStarted || bstate == BlockState.FinishDone) status = FINISH_STATE;
                if (bstate == BlockState.NeedMore || bstate == BlockState.FinishStarted)
                {
                    if (AvailOut == 0) last_flush = -1;
                    return ZConst.Z_OK;
                }
                if (bstate == BlockState.BlockDone)
                {
                    if (flush == ZConst.Z_PARTIAL_FLUSH) TrAlign();
                    else if (flush != ZConst.Z_BLOCK)
                    {
                        TrStoredBlock(null, 0, false);
                        if (flush == ZConst.Z_FULL_FLUSH)
                        {
                            new Span<byte>(head, (int)(HASH_SIZE * 2)).Clear();
                            if (lookahead == 0) { strstart = 0; block_start = 0; insert = 0; }
                        }
                    }
                    FlushPending();
                    if (AvailOut == 0) { last_flush = -1; return ZConst.Z_OK; }
                }
            }
            if (flush != ZConst.Z_FINISH) return ZConst.Z_OK;
            if (wrap == 2)
            {
                Adler = crc;
                PutUint32(Adler);
                PutUint32((uint)TotalIn);
            }
            else if (wrap == 1) PutUint32Msb(Adler);
            FlushPending();
            if (wrap > 0) wrap = -wrap;
            return pending == 0 ? ZConst.Z_STREAM_END : ZConst.Z_OK;
        }

        // ---------------- deflate_p.h ----------------
        bool TallyLit(byte c)
        {
            d_buf[sym_next] = 0;
            l_buf[sym_next++] = c;
            dyn_ltree[2 * c]++;
            return sym_next == sym_end;
        }

        bool TallyDist(uint dist, uint len)
        {
            d_buf[sym_next] = (ushort)dist;
            l_buf[sym_next++] = (byte)len;
            matches++;
            dist--;
            dyn_ltree[2 * (ZTrees.LengthCode[len] + ZTrees.LITERALS + 1)]++;
            dyn_dtree[2 * ZTrees.DCode(dist)]++;
            return sym_next == sym_end;
        }

        void FlushBlockOnly(bool last)
        {
            TrFlushBlock(block_start >= 0 ? window + (uint)block_start : null, (uint)((int)strstart - block_start), last);
            block_start = (int)strstart;
            FlushPending();
        }

        // ---------------- deflate_stored.c ----------------
        BlockState DeflateStored(int flush)
        {
            uint minBlock = Math.Min(pending_buf_size - 5, w_size);
            uint len, left, have, last = 0;
            uint used = AvailIn;
            do
            {
                len = MAX_STORED;
                have = (uint)(bi_valid + 42) >> 3;
                if (AvailOut < have) break;
                have = AvailOut - have;
                left = (uint)((int)strstart - block_start);
                if (len > (ulong)left + AvailIn) len = left + AvailIn;
                len = Math.Min(len, have);
                if (len < minBlock && ((len == 0 && flush != ZConst.Z_FINISH) || flush == ZConst.Z_NO_FLUSH || len != left + AvailIn)) break;
                last = flush == ZConst.Z_FINISH && len == left + AvailIn ? 1u : 0u;
                TrStoredBlock(null, 0, last != 0);
                pending -= 4;
                PutShort((ushort)len);
                PutShort((ushort)~len);
                FlushPending();
                if (left != 0)
                {
                    left = Math.Min(left, len);
                    Buffer.MemoryCopy(window + block_start, NextOut, left, left);
                    NextOut += left;
                    AvailOut -= left;
                    TotalOut += left;
                    block_start += (int)left;
                    len -= left;
                }
                if (len != 0)
                {
                    ReadBuf(NextOut, len);
                    NextOut += len;
                    AvailOut -= len;
                    TotalOut += len;
                }
            } while (last == 0);
            used -= AvailIn;
            if (used != 0)
            {
                if (used >= w_size)
                {
                    matches = 2;
                    Buffer.MemoryCopy(NextIn - w_size, window, w_size, w_size);
                    strstart = w_size;
                    insert = strstart;
                }
                else
                {
                    if (window_size - strstart <= used)
                    {
                        strstart -= w_size;
                        Buffer.MemoryCopy(window + w_size, window, strstart, strstart);
                        if (matches < 2) matches++;
                        insert = Math.Min(insert, strstart);
                    }
                    Buffer.MemoryCopy(NextIn - used, window + strstart, used, used);
                    strstart += used;
                    insert += Math.Min(used, w_size - insert);
                }
                block_start = (int)strstart;
            }
            high_water = Math.Max(high_water, strstart);
            if (last != 0) return BlockState.FinishDone;
            if (flush != ZConst.Z_NO_FLUSH && flush != ZConst.Z_FINISH && AvailIn == 0 && (int)strstart == block_start) return BlockState.BlockDone;
            have = window_size - strstart;
            if (AvailIn > have && block_start >= (int)w_size)
            {
                block_start -= (int)w_size;
                strstart -= w_size;
                Buffer.MemoryCopy(window + w_size, window, strstart, strstart);
                if (matches < 2) matches++;
                have += w_size;
                insert = Math.Min(insert, strstart);
            }
            have = Math.Min(have, AvailIn);
            if (have != 0)
            {
                ReadBuf(window + strstart, have);
                strstart += have;
                insert += Math.Min(have, w_size - insert);
            }
            high_water = Math.Max(high_water, strstart);
            have = (uint)(bi_valid + 42) >> 3;
            have = Math.Min(pending_buf_size - have, MAX_STORED);
            minBlock = Math.Min(have, w_size);
            left = (uint)((int)strstart - block_start);
            if (left >= minBlock || ((left != 0 || flush == ZConst.Z_FINISH) && flush != ZConst.Z_NO_FLUSH && AvailIn == 0 && left <= have))
            {
                len = Math.Min(left, have);
                last = flush == ZConst.Z_FINISH && AvailIn == 0 && len == left ? 1u : 0u;
                TrStoredBlock(window + block_start, len, last != 0);
                block_start += (int)len;
                FlushPending();
            }
            return last != 0 ? BlockState.FinishStarted : BlockState.NeedMore;
        }

        // ---------------- deflate_fast.c ----------------
        BlockState DeflateFast(int flush)
        {
            uint matchLen = 0;
            for (; ; )
            {
                if (lookahead < MIN_LOOKAHEAD)
                {
                    FillWindow();
                    if (lookahead < MIN_LOOKAHEAD && flush == ZConst.Z_NO_FLUSH) return BlockState.NeedMore;
                    if (lookahead == 0) break;
                }
                if (lookahead >= WANT_MIN_MATCH)
                {
                    ushort hashHead = QuickInsertString(strstart);
                    long dist = (long)strstart - hashHead;
                    if (dist <= MaxDist && dist > 0 && hashHead != 0) matchLen = LongestMatch(hashHead, false);
                }
                bool bflush;
                if (matchLen >= WANT_MIN_MATCH)
                {
                    bflush = TallyDist(strstart - match_start, matchLen - STD_MIN_MATCH);
                    lookahead -= matchLen;
                    if (matchLen <= max_lazy_match && lookahead >= WANT_MIN_MATCH)
                    {
                        matchLen--;
                        strstart++;
                        InsertString(strstart, matchLen);
                        strstart += matchLen;
                    }
                    else
                    {
                        strstart += matchLen;
                        QuickInsertString(strstart + 2 - STD_MIN_MATCH);
                    }
                    matchLen = 0;
                }
                else
                {
                    bflush = TallyLit(window[strstart]);
                    lookahead--;
                    strstart++;
                }
                if (bflush) { FlushBlockOnly(false); if (AvailOut == 0) return BlockState.NeedMore; }
            }
            insert = strstart < STD_MIN_MATCH - 1 ? strstart : STD_MIN_MATCH - 1;
            if (flush == ZConst.Z_FINISH) { FlushBlockOnly(true); if (AvailOut == 0) return BlockState.FinishStarted; return BlockState.FinishDone; }
            if (sym_next != 0) { FlushBlockOnly(false); if (AvailOut == 0) return BlockState.NeedMore; }
            return BlockState.BlockDone;
        }

        // ---------------- deflate_medium.c ----------------
        struct Match { public ushort MatchStart, MatchLength, StrStart, OrgStart; }

        bool EmitMatch(Match m)
        {
            int bflush = 0;
            if (m.MatchLength < WANT_MIN_MATCH)
            {
                while (m.MatchLength != 0)
                {
                    bflush += TallyLit(window[m.StrStart]) ? 1 : 0;
                    lookahead--;
                    m.StrStart++;
                    m.MatchLength--;
                }
                return bflush != 0;
            }
            bflush += TallyDist((uint)(m.StrStart - m.MatchStart), (uint)(m.MatchLength - STD_MIN_MATCH)) ? 1 : 0;
            lookahead -= m.MatchLength;
            return bflush != 0;
        }

        void InsertMatch(Match m)
        {
            if (lookahead <= (uint)(m.MatchLength + WANT_MIN_MATCH)) return;
            if (m.MatchLength < WANT_MIN_MATCH)
            {
                m.StrStart++;
                m.MatchLength--;
                if (m.MatchLength > 0)
                {
                    if (m.StrStart >= m.OrgStart)
                    {
                        if (m.StrStart + m.MatchLength - 1 >= m.OrgStart) InsertString(m.StrStart, m.MatchLength);
                        else InsertString(m.StrStart, (uint)(m.OrgStart - m.StrStart + 1));
                        m.StrStart += m.MatchLength;
                        m.MatchLength = 0;
                    }
                }
                return;
            }
            if (m.MatchLength <= 16 * max_lazy_match && lookahead >= WANT_MIN_MATCH)
            {
                m.MatchLength--;
                m.StrStart++;
                if (m.StrStart >= m.OrgStart)
                {
                    if (m.StrStart + m.MatchLength - 1 >= m.OrgStart) InsertString(m.StrStart, m.MatchLength);
                    else InsertString(m.StrStart, (uint)(m.OrgStart - m.StrStart + 1));
                }
                else if (m.OrgStart < m.StrStart + m.MatchLength)
                    InsertString(m.OrgStart, (uint)(m.StrStart + m.MatchLength - m.OrgStart));
                m.StrStart += m.MatchLength;
                m.MatchLength = 0;
            }
            else
            {
                m.StrStart += m.MatchLength;
                m.MatchLength = 0;
                if (m.StrStart >= STD_MIN_MATCH - 2) QuickInsertString((uint)(m.StrStart + 2 - STD_MIN_MATCH));
            }
        }

        void FizzleMatches(ref Match current, ref Match next)
        {
            if (current.MatchLength <= 1) return;
            if (current.MatchLength > 1 + next.MatchStart) return;
            if (current.MatchLength > 1 + next.StrStart) return;
            byte* match = window - current.MatchLength + 1 + next.MatchStart;
            byte* orig = window - current.MatchLength + 1 + next.StrStart;
            if (*match != *orig) return;
            Match c = current, n = next;
            uint limit = next.StrStart > MaxDist ? (ushort)(next.StrStart - (ushort)MaxDist) : 0u;
            match = window + n.MatchStart - 1;
            orig = window + n.StrStart - 1;
            int changed = 0;
            while (*match == *orig)
            {
                if (c.MatchLength < 1) break;
                if (n.StrStart <= limit) break;
                if (n.MatchLength >= 256) break;
                if (n.MatchStart <= 1) break;
                n.StrStart--;
                n.MatchStart--;
                n.MatchLength++;
                c.MatchLength--;
                match--;
                orig--;
                changed++;
            }
            if (changed == 0) return;
            if (c.MatchLength <= 1 && n.MatchLength != 2)
            {
                n.OrgStart++;
                current = c;
                next = n;
            }
        }

        BlockState DeflateMedium(int flush)
        {
            Match current = default, next = default;
            bool earlyExit = level < 5;
            for (; ; )
            {
                ushort hashHead;
                long dist;
                if (lookahead < MIN_LOOKAHEAD)
                {
                    FillWindow();
                    if (lookahead < MIN_LOOKAHEAD && flush == ZConst.Z_NO_FLUSH) return BlockState.NeedMore;
                    if (lookahead == 0) break;
                    next.MatchLength = 0;
                }
                if (!earlyExit && next.MatchLength > 0)
                {
                    current = next;
                    next.MatchLength = 0;
                }
                else
                {
                    hashHead = 0;
                    if (lookahead >= WANT_MIN_MATCH) hashHead = QuickInsertString(strstart);
                    current.StrStart = (ushort)strstart;
                    current.OrgStart = current.StrStart;
                    dist = (long)strstart - hashHead;
                    if (dist <= MaxDist && dist > 0 && hashHead != 0)
                    {
                        current.MatchLength = (ushort)LongestMatch(hashHead, false);
                        current.MatchStart = (ushort)match_start;
                        if (current.MatchLength < WANT_MIN_MATCH) current.MatchLength = 1;
                        if (current.MatchStart >= current.StrStart) current.MatchLength = 1;
                    }
                    else
                    {
                        current.MatchStart = 0;
                        current.MatchLength = 1;
                    }
                }
                InsertMatch(current);
                if (!earlyExit && lookahead > MIN_LOOKAHEAD && (uint)(current.StrStart + current.MatchLength) < window_size - MIN_LOOKAHEAD)
                {
                    strstart = (uint)(current.StrStart + current.MatchLength);
                    hashHead = QuickInsertString(strstart);
                    next.StrStart = (ushort)strstart;
                    next.OrgStart = next.StrStart;
                    dist = (long)strstart - hashHead;
                    if (dist <= MaxDist && dist > 0 && hashHead != 0)
                    {
                        next.MatchLength = (ushort)LongestMatch(hashHead, false);
                        next.MatchStart = (ushort)match_start;
                        if (next.MatchStart >= next.StrStart) next.MatchLength = 1;
                        if (next.MatchLength < WANT_MIN_MATCH) next.MatchLength = 1;
                        else FizzleMatches(ref current, ref next);
                    }
                    else
                    {
                        next.MatchStart = 0;
                        next.MatchLength = 1;
                    }
                    strstart = current.StrStart;
                }
                else next.MatchLength = 0;
                bool bflush = EmitMatch(current);
                strstart += current.MatchLength;
                if (bflush) { FlushBlockOnly(false); if (AvailOut == 0) return BlockState.NeedMore; }
            }
            insert = strstart < STD_MIN_MATCH - 1 ? strstart : STD_MIN_MATCH - 1;
            if (flush == ZConst.Z_FINISH) { FlushBlockOnly(true); if (AvailOut == 0) return BlockState.FinishStarted; return BlockState.FinishDone; }
            if (sym_next != 0) { FlushBlockOnly(false); if (AvailOut == 0) return BlockState.NeedMore; }
            return BlockState.BlockDone;
        }

        // ---------------- deflate_slow.c ----------------
        BlockState DeflateSlow(int flush)
        {
            bool slowMatch = max_chain_length > 1024;
            for (; ; )
            {
                if (lookahead < MIN_LOOKAHEAD)
                {
                    FillWindow();
                    if (lookahead < MIN_LOOKAHEAD && flush == ZConst.Z_NO_FLUSH) return BlockState.NeedMore;
                    if (lookahead == 0) break;
                }
                ushort hashHead = 0;
                if (lookahead >= WANT_MIN_MATCH) hashHead = SQuickInsertString(strstart);
                prev_match = (ushort)match_start;
                uint matchLen = STD_MIN_MATCH - 1;
                long dist = (long)strstart - hashHead;
                if (dist <= MaxDist && dist > 0 && prev_length < max_lazy_match && hashHead != 0)
                {
                    matchLen = LongestMatch(hashHead, slowMatch);
                    if (matchLen <= 5 && strategy == ZConst.Z_FILTERED) matchLen = STD_MIN_MATCH - 1;
                }
                if (prev_length >= STD_MIN_MATCH && matchLen <= prev_length)
                {
                    uint maxInsert = strstart + lookahead - STD_MIN_MATCH;
                    bool bflush = TallyDist(strstart - 1 - prev_match, prev_length - STD_MIN_MATCH);
                    prev_length -= 1;
                    lookahead -= prev_length;
                    uint movFwd = prev_length - 1;
                    if (maxInsert > strstart)
                    {
                        uint insertCnt = movFwd;
                        if (insertCnt > maxInsert - strstart) insertCnt = maxInsert - strstart;
                        SInsertString(strstart + 1, insertCnt);
                    }
                    prev_length = 0;
                    match_available = 0;
                    strstart += movFwd + 1;
                    if (bflush) { FlushBlockOnly(false); if (AvailOut == 0) return BlockState.NeedMore; }
                }
                else if (match_available != 0)
                {
                    bool bflush = TallyLit(window[strstart - 1]);
                    if (bflush) FlushBlockOnly(false);
                    prev_length = matchLen;
                    strstart++;
                    lookahead--;
                    if (AvailOut == 0) return BlockState.NeedMore;
                }
                else
                {
                    prev_length = matchLen;
                    match_available = 1;
                    strstart++;
                    lookahead--;
                }
            }
            if (match_available != 0)
            {
                TallyLit(window[strstart - 1]);
                match_available = 0;
            }
            insert = strstart < STD_MIN_MATCH - 1 ? strstart : STD_MIN_MATCH - 1;
            if (flush == ZConst.Z_FINISH) { FlushBlockOnly(true); if (AvailOut == 0) return BlockState.FinishStarted; return BlockState.FinishDone; }
            if (sym_next != 0) { FlushBlockOnly(false); if (AvailOut == 0) return BlockState.NeedMore; }
            return BlockState.BlockDone;
        }

        // ---------------- deflate_quick.c ----------------
        void QuickStartBlock(bool last)
        {
            TrEmitTree(STATIC_TREES, last);
            block_open = 1 + (last ? 1 : 0);
            block_start = (int)strstart;
        }

        /// <summary>QUICK_END_BLOCK: false when the macro returns (the out buffer is full).</summary>
        bool QuickEndBlock(bool last)
        {
            if (block_open != 0)
            {
                EmitEndBlock(ZTrees.StaticLTree);
                if (last) BiWindup();
                block_open = 0;
                block_start = (int)strstart;
                FlushPending();
                if (AvailOut == 0) return false;
            }
            return true;
        }

        BlockState DeflateQuick(int flush)
        {
            bool last = flush == ZConst.Z_FINISH;
            if (last && block_open != 2)
            {
                if (!QuickEndBlock(false)) return BlockState.NeedMore;
                QuickStartBlock(last);
            }
            else if (block_open == 0 && lookahead > 0) QuickStartBlock(last);
            for (; ; )
            {
                if (pending + ((BIT_BUF_SIZE + 7) >> 3) >= pending_buf_size)
                {
                    FlushPending();
                    if (AvailOut == 0)
                        return last && AvailIn == 0 && bi_valid == 0 && block_open == 0 ? BlockState.FinishStarted : BlockState.NeedMore;
                }
                if (lookahead < MIN_LOOKAHEAD)
                {
                    FillWindow();
                    if (lookahead < MIN_LOOKAHEAD && flush == ZConst.Z_NO_FLUSH) return BlockState.NeedMore;
                    if (lookahead == 0) break;
                    if (block_open == 0) QuickStartBlock(last);
                }
                if (lookahead >= WANT_MIN_MATCH)
                {
                    ushort hashHead = QuickInsertString(strstart);
                    long dist = (long)strstart - hashHead;
                    if (dist <= MaxDist && dist > 0)
                    {
                        byte* strStart = window + strstart;
                        byte* matchStart = window + hashHead;
                        if (*(ushort*)strStart == *(ushort*)matchStart)
                        {
                            uint matchLen = Compare256(strStart + 2, matchStart + 2) + 2;
                            if (matchLen >= WANT_MIN_MATCH)
                            {
                                if (matchLen > lookahead) matchLen = lookahead;
                                if (matchLen > STD_MAX_MATCH) matchLen = STD_MAX_MATCH;
                                EmitDist(ZTrees.StaticLTree, ZTrees.StaticDTree, matchLen - STD_MIN_MATCH, (uint)dist);
                                lookahead -= matchLen;
                                strstart += matchLen;
                                continue;
                            }
                        }
                    }
                }
                EmitLit(ZTrees.StaticLTree, window[strstart]);
                strstart++;
                lookahead--;
            }
            insert = strstart < STD_MIN_MATCH - 1 ? strstart : STD_MIN_MATCH - 1;
            if (last)
            {
                if (!QuickEndBlock(true)) return BlockState.FinishStarted;
                return BlockState.FinishDone;
            }
            if (!QuickEndBlock(false)) return BlockState.NeedMore;
            return BlockState.BlockDone;
        }

        // ---------------- deflate_huff.c ----------------
        BlockState DeflateHuff(int flush)
        {
            for (; ; )
            {
                if (lookahead == 0)
                {
                    FillWindow();
                    if (lookahead == 0)
                    {
                        if (flush == ZConst.Z_NO_FLUSH) return BlockState.NeedMore;
                        break;
                    }
                }
                bool bflush = TallyLit(window[strstart]);
                lookahead--;
                strstart++;
                if (bflush) { FlushBlockOnly(false); if (AvailOut == 0) return BlockState.NeedMore; }
            }
            insert = 0;
            if (flush == ZConst.Z_FINISH) { FlushBlockOnly(true); if (AvailOut == 0) return BlockState.FinishStarted; return BlockState.FinishDone; }
            if (sym_next != 0) { FlushBlockOnly(false); if (AvailOut == 0) return BlockState.NeedMore; }
            return BlockState.BlockDone;
        }

        // ---------------- deflate_rle.c ----------------
        BlockState DeflateRle(int flush)
        {
            uint matchLen = 0;
            for (; ; )
            {
                if (lookahead <= STD_MAX_MATCH)
                {
                    FillWindow();
                    if (lookahead <= STD_MAX_MATCH && flush == ZConst.Z_NO_FLUSH) return BlockState.NeedMore;
                    if (lookahead == 0) break;
                }
                if (lookahead >= STD_MIN_MATCH && strstart > 0)
                {
                    byte* scan = window + strstart - 1;
                    if (scan[0] == scan[1] && scan[1] == scan[2])
                    {
                        // (compare256_rle: the run of scan[0] from scan + 3)
                        uint l = 0;
                        while (l < 256 && scan[3 + l] == scan[0]) l++;
                        matchLen = l + 2;
                        matchLen = Math.Min(matchLen, lookahead);
                        matchLen = Math.Min(matchLen, STD_MAX_MATCH);
                    }
                }
                bool bflush;
                if (matchLen >= STD_MIN_MATCH)
                {
                    bflush = TallyDist(1, matchLen - STD_MIN_MATCH);
                    lookahead -= matchLen;
                    strstart += matchLen;
                    matchLen = 0;
                }
                else
                {
                    bflush = TallyLit(window[strstart]);
                    lookahead--;
                    strstart++;
                }
                if (bflush) { FlushBlockOnly(false); if (AvailOut == 0) return BlockState.NeedMore; }
            }
            insert = 0;
            if (flush == ZConst.Z_FINISH) { FlushBlockOnly(true); if (AvailOut == 0) return BlockState.FinishStarted; return BlockState.FinishDone; }
            if (sym_next != 0) { FlushBlockOnly(false); if (AvailOut == 0) return BlockState.NeedMore; }
            return BlockState.BlockDone;
        }

        // ---------------- trees_emit.h ----------------
        void SendBits(ref ulong biBuf, ref int biValid, ulong val, int len)
        {
            int totalBits = biValid + len;
            if (totalBits < BIT_BUF_SIZE && biValid < BIT_BUF_SIZE)
            {
                biBuf |= val << biValid;
                biValid = totalBits;
            }
            else if (biValid >= BIT_BUF_SIZE)
            {
                PutUint64(biBuf);
                biBuf = val;
                biValid = len;
            }
            else
            {
                biBuf |= val << biValid;
                PutUint64(biBuf);
                biBuf = val >> (BIT_BUF_SIZE - biValid);
                biValid = totalBits - BIT_BUF_SIZE;
            }
        }

        void SendCode(ref ulong biBuf, ref int biValid, int c, ushort* tree) => SendBits(ref biBuf, ref biValid, tree[2 * c], tree[2 * c + 1]);

        void BiWindup()
        {
            if (bi_valid > 56) PutUint64(bi_buf);
            else
            {
                if (bi_valid > 24)
                {
                    PutUint32((uint)bi_buf);
                    bi_buf >>= 32;
                    bi_valid -= 32;
                }
                if (bi_valid > 8)
                {
                    PutShort((ushort)bi_buf);
                    bi_buf >>= 16;
                    bi_valid -= 16;
                }
                if (bi_valid > 0) PutByte((byte)bi_buf);
            }
            bi_buf = 0;
            bi_valid = 0;
        }

        void EmitLit(ushort* ltree, int c)
        {
            ulong b = bi_buf; int v = bi_valid;
            SendCode(ref b, ref v, c, ltree);
            bi_buf = b; bi_valid = v;
        }

        void EmitDist(ushort* ltree, ushort* dtree, uint lc, uint dist)
        {
            ulong b = bi_buf; int v = bi_valid;
            uint code = ZTrees.LengthCode[lc];
            uint c = code + ZTrees.LITERALS + 1;
            ulong matchBits = ltree[2 * c];
            int matchBitsLen = ltree[2 * c + 1];
            int extra = ZTrees.ExtraLBits[code];
            if (extra != 0)
            {
                lc -= (uint)ZTrees.BaseLength[code];
                matchBits |= (ulong)lc << matchBitsLen;
                matchBitsLen += extra;
            }
            dist--;
            code = (uint)ZTrees.DCode(dist);
            matchBits |= (ulong)dtree[2 * code] << matchBitsLen;
            matchBitsLen += dtree[2 * code + 1];
            extra = ZTrees.ExtraDBits[code];
            if (extra != 0)
            {
                dist -= (uint)ZTrees.BaseDist[code];
                matchBits |= (ulong)dist << matchBitsLen;
                matchBitsLen += extra;
            }
            SendBits(ref b, ref v, matchBits, matchBitsLen);
            bi_buf = b; bi_valid = v;
        }

        void EmitEndBlock(ushort* ltree)
        {
            ulong b = bi_buf; int v = bi_valid;
            SendCode(ref b, ref v, ZTrees.END_BLOCK, ltree);
            bi_buf = b; bi_valid = v;
        }

        void TrEmitTree(int type, bool last)
        {
            ulong b = bi_buf; int v = bi_valid;
            SendBits(ref b, ref v, (ulong)((type << 1) + (last ? 1 : 0)), 3);
            bi_buf = b; bi_valid = v;
        }

        // ---------------- trees.c ----------------
        void TrInit()
        {
            bi_buf = 0;
            bi_valid = 0;
            InitBlock();
        }

        void InitBlock()
        {
            for (int n = 0; n < ZTrees.L_CODES; n++) dyn_ltree[2 * n] = 0;
            for (int n = 0; n < ZTrees.D_CODES; n++) dyn_dtree[2 * n] = 0;
            for (int n = 0; n < ZTrees.BL_CODES; n++) bl_tree[2 * n] = 0;
            dyn_ltree[2 * ZTrees.END_BLOCK] = 1;
            opt_len = static_len = 0;
            sym_next = matches = 0;
        }

        bool Smaller(ushort* tree, int n, int m) => tree[2 * n] < tree[2 * m] || (tree[2 * n] == tree[2 * m] && depth[n] <= depth[m]);

        void PqDownHeap(ushort* tree, int k)
        {
            int v = heap[k];
            int j = k << 1;
            while (j <= heap_len)
            {
                if (j < heap_len && Smaller(tree, heap[j + 1], heap[j])) j++;
                if (Smaller(tree, v, heap[j])) break;
                heap[k] = heap[j];
                k = j;
                j <<= 1;
            }
            heap[k] = v;
        }

        void GenBitlen(ushort* tree, int maxCode, ushort* stree, int[] extra, int extraBase, uint maxLength)
        {
            int h, n, m, xbits, overflow = 0;
            uint bits;
            for (bits = 0; bits <= ZTrees.MAX_BITS; bits++) bl_count[bits] = 0;
            tree[2 * heap[heap_max] + 1] = 0;
            for (h = heap_max + 1; h < ZTrees.HEAP_SIZE; h++)
            {
                n = heap[h];
                bits = tree[2 * tree[2 * n + 1] + 1] + 1u;
                if (bits > maxLength) { bits = maxLength; overflow++; }
                tree[2 * n + 1] = (ushort)bits;
                if (n > maxCode) continue;
                bl_count[bits]++;
                xbits = 0;
                if (n >= extraBase) xbits = extra[n - extraBase];
                ushort f = tree[2 * n];
                opt_len += f * (uint)(bits + xbits);
                if (stree != null) static_len += f * (uint)(stree[2 * n + 1] + xbits);
            }
            if (overflow == 0) return;
            do
            {
                bits = maxLength - 1;
                while (bl_count[bits] == 0) bits--;
                bl_count[bits]--;
                bl_count[bits + 1] += 2;
                bl_count[maxLength]--;
                overflow -= 2;
            } while (overflow > 0);
            for (bits = maxLength; bits != 0; bits--)
            {
                n = bl_count[bits];
                while (n != 0)
                {
                    m = heap[--h];
                    if (m > maxCode) continue;
                    if (tree[2 * m + 1] != bits)
                    {
                        opt_len += bits * tree[2 * m];
                        opt_len -= (uint)(tree[2 * m + 1] * tree[2 * m]);
                        tree[2 * m + 1] = (ushort)bits;
                    }
                    n--;
                }
            }
        }

        /// <summary>build_tree; the tree's max_code.</summary>
        int BuildTree(ushort* tree, ushort* stree, int elems, int[] extra, int extraBase, uint maxLength)
        {
            int n, m, maxCode = -1, node;
            heap_len = 0;
            heap_max = ZTrees.HEAP_SIZE;
            for (n = 0; n < elems; n++)
            {
                if (tree[2 * n] != 0)
                {
                    heap[++heap_len] = maxCode = n;
                    depth[n] = 0;
                }
                else tree[2 * n + 1] = 0;
            }
            while (heap_len < 2)
            {
                node = heap[++heap_len] = maxCode < 2 ? ++maxCode : 0;
                tree[2 * node] = 1;
                depth[node] = 0;
                opt_len--;
                if (stree != null) static_len -= stree[2 * node + 1];
            }
            for (n = heap_len / 2; n >= 1; n--) PqDownHeap(tree, n);
            node = elems;
            do
            {
                n = heap[1];
                heap[1] = heap[heap_len--];
                PqDownHeap(tree, 1);
                m = heap[1];
                heap[--heap_max] = n;
                heap[--heap_max] = m;
                tree[2 * node] = (ushort)(tree[2 * n] + tree[2 * m]);
                depth[node] = (byte)((depth[n] >= depth[m] ? depth[n] : depth[m]) + 1);
                tree[2 * n + 1] = tree[2 * m + 1] = (ushort)node;
                heap[1] = node++;
                PqDownHeap(tree, 1);
            } while (heap_len >= 2);
            heap[--heap_max] = heap[1];
            GenBitlen(tree, maxCode, stree, extra, extraBase, maxLength);
            ZTrees.GenCodes(tree, maxCode, bl_count);
            return maxCode;
        }

        void ScanTree(ushort* tree, int maxCode)
        {
            int prevlen = -1, curlen, nextlen = tree[1];
            int count = 0, maxCount = 7, minCount = 4;
            if (nextlen == 0) { maxCount = 138; minCount = 3; }
            tree[2 * (maxCode + 1) + 1] = 0xffff;
            for (int n = 0; n <= maxCode; n++)
            {
                curlen = nextlen;
                nextlen = tree[2 * (n + 1) + 1];
                if (++count < maxCount && curlen == nextlen) continue;
                else if (count < minCount) bl_tree[2 * curlen] += (ushort)count;
                else if (curlen != 0)
                {
                    if (curlen != prevlen) bl_tree[2 * curlen]++;
                    bl_tree[2 * ZTrees.REP_3_6]++;
                }
                else if (count <= 10) bl_tree[2 * ZTrees.REPZ_3_10]++;
                else bl_tree[2 * ZTrees.REPZ_11_138]++;
                count = 0;
                prevlen = curlen;
                if (nextlen == 0) { maxCount = 138; minCount = 3; }
                else if (curlen == nextlen) { maxCount = 6; minCount = 3; }
                else { maxCount = 7; minCount = 4; }
            }
        }

        void SendTree(ushort* tree, int maxCode)
        {
            int prevlen = -1, curlen, nextlen = tree[1];
            int count = 0, maxCount = 7, minCount = 4;
            if (nextlen == 0) { maxCount = 138; minCount = 3; }
            ulong b = bi_buf; int v = bi_valid;
            for (int n = 0; n <= maxCode; n++)
            {
                curlen = nextlen;
                nextlen = tree[2 * (n + 1) + 1];
                if (++count < maxCount && curlen == nextlen) continue;
                else if (count < minCount)
                {
                    do { SendCode(ref b, ref v, curlen, bl_tree); } while (--count != 0);
                }
                else if (curlen != 0)
                {
                    if (curlen != prevlen)
                    {
                        SendCode(ref b, ref v, curlen, bl_tree);
                        count--;
                    }
                    SendCode(ref b, ref v, ZTrees.REP_3_6, bl_tree);
                    SendBits(ref b, ref v, (ulong)(count - 3), 2);
                }
                else if (count <= 10)
                {
                    SendCode(ref b, ref v, ZTrees.REPZ_3_10, bl_tree);
                    SendBits(ref b, ref v, (ulong)(count - 3), 3);
                }
                else
                {
                    SendCode(ref b, ref v, ZTrees.REPZ_11_138, bl_tree);
                    SendBits(ref b, ref v, (ulong)(count - 11), 7);
                }
                count = 0;
                prevlen = curlen;
                if (nextlen == 0) { maxCount = 138; minCount = 3; }
                else if (curlen == nextlen) { maxCount = 6; minCount = 3; }
                else { maxCount = 7; minCount = 4; }
            }
            bi_buf = b; bi_valid = v;
        }

        int BuildBlTree()
        {
            ScanTree(dyn_ltree, l_max_code);
            ScanTree(dyn_dtree, d_max_code);
            bl_max_code = BuildTree(bl_tree, null, ZTrees.BL_CODES, ZTrees.ExtraBlBits, 0, ZTrees.MAX_BL_BITS);
            int maxBlindex;
            for (maxBlindex = ZTrees.BL_CODES - 1; maxBlindex >= 3; maxBlindex--)
                if (bl_tree[2 * ZTrees.BlOrder[maxBlindex] + 1] != 0) break;
            opt_len += 3 * ((uint)maxBlindex + 1) + 5 + 5 + 4;
            return maxBlindex;
        }

        void SendAllTrees(int lcodes, int dcodes, int blcodes)
        {
            ulong b = bi_buf; int v = bi_valid;
            SendBits(ref b, ref v, (ulong)(lcodes - 257), 5);
            SendBits(ref b, ref v, (ulong)(dcodes - 1), 5);
            SendBits(ref b, ref v, (ulong)(blcodes - 4), 4);
            for (int rank = 0; rank < blcodes; rank++) SendBits(ref b, ref v, bl_tree[2 * ZTrees.BlOrder[rank] + 1], 3);
            bi_buf = b; bi_valid = v;
            SendTree(dyn_ltree, lcodes - 1);
            SendTree(dyn_dtree, dcodes - 1);
        }

        void TrStoredBlock(byte* buf, uint storedLen, bool last)
        {
            TrEmitTree(STORED_BLOCK, last);
            BiWindup();
            PutShort((ushort)storedLen);
            PutShort((ushort)~storedLen);
            if (storedLen != 0)
            {
                Buffer.MemoryCopy(buf, pending_buf + pending, storedLen, storedLen);
                pending += storedLen;
            }
        }

        void TrAlign()
        {
            TrEmitTree(STATIC_TREES, false);
            EmitEndBlock(ZTrees.StaticLTree);
            TrFlushBits();
        }

        void TrFlushBlock(byte* buf, uint storedLen, bool last)
        {
            uint optLenb, staticLenb;
            int maxBlindex = 0;
            if (sym_next == 0)
            {
                optLenb = staticLenb = 0;
                static_len = 7;
            }
            else if (level > 0)
            {
                if (DataType == ZConst.Z_UNKNOWN) DataType = DetectDataType();
                l_max_code = BuildTree(dyn_ltree, ZTrees.StaticLTree, ZTrees.L_CODES, ZTrees.ExtraLBits, ZTrees.LITERALS + 1, ZTrees.MAX_BITS);
                d_max_code = BuildTree(dyn_dtree, ZTrees.StaticDTree, ZTrees.D_CODES, ZTrees.ExtraDBits, 0, ZTrees.MAX_BITS);
                maxBlindex = BuildBlTree();
                optLenb = (opt_len + 3 + 7) >> 3;
                staticLenb = (static_len + 3 + 7) >> 3;
                if (staticLenb <= optLenb || strategy == ZConst.Z_FIXED) optLenb = staticLenb;
            }
            else optLenb = staticLenb = storedLen + 5;
            if (storedLen + 4 <= optLenb && buf != null) TrStoredBlock(buf, storedLen, last);
            else if (staticLenb == optLenb)
            {
                TrEmitTree(STATIC_TREES, last);
                CompressBlock(ZTrees.StaticLTree, ZTrees.StaticDTree);
            }
            else
            {
                TrEmitTree(DYN_TREES, last);
                SendAllTrees(l_max_code + 1, d_max_code + 1, maxBlindex + 1);
                CompressBlock(dyn_ltree, dyn_dtree);
            }
            InitBlock();
            if (last) BiWindup();
        }

        void CompressBlock(ushort* ltree, ushort* dtree)
        {
            uint sx = 0;
            if (sym_next != 0)
            {
                do
                {
                    uint dist = d_buf[sx];
                    uint lc = l_buf[sx++];
                    if (dist == 0) EmitLit(ltree, (int)lc);
                    else EmitDist(ltree, dtree, lc, dist);
                } while (sx < sym_next);
            }
            EmitEndBlock(ltree);
        }

        int DetectDataType()
        {
            uint blackMask = 0xf3ffc07fU;
            int n;
            for (n = 0; n <= 31; n++, blackMask >>= 1)
                if ((blackMask & 1) != 0 && dyn_ltree[2 * n] != 0) return ZConst.Z_BINARY;
            if (dyn_ltree[2 * 9] != 0 || dyn_ltree[2 * 10] != 0 || dyn_ltree[2 * 13] != 0) return ZConst.Z_TEXT;
            for (n = 32; n < ZTrees.LITERALS; n++)
                if (dyn_ltree[2 * n] != 0) return ZConst.Z_TEXT;
            return ZConst.Z_BINARY;
        }

        void TrFlushBits()
        {
            if (bi_valid >= 48)
            {
                PutUint32((uint)bi_buf);
                PutShort((ushort)(bi_buf >> 32));
                bi_buf >>= 48;
                bi_valid -= 48;
            }
            else if (bi_valid >= 32)
            {
                PutUint32((uint)bi_buf);
                bi_buf >>= 32;
                bi_valid -= 32;
            }
            if (bi_valid >= 16)
            {
                PutShort((ushort)bi_buf);
                bi_buf >>= 16;
                bi_valid -= 16;
            }
            if (bi_valid >= 8)
            {
                PutByte((byte)bi_buf);
                bi_buf >>= 8;
                bi_valid -= 8;
            }
        }
    }

    /// <summary>CRC-32 (zlib's, the reflected 0xEDB88320 polynomial).</summary>
    internal static unsafe class Crc32
    {
        static readonly uint* s_table = MakeTable();

        static uint* MakeTable()
        {
            var t = (uint*)NativeMemory.Alloc(256 * 4);
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        /// <summary>crc32 (zlib-ng crc32.c: a null buffer gives 0).</summary>
        public static uint Update(uint crc, byte* p, int len)
        {
            if (p == null) return 0;
            uint* t = s_table;
            crc = ~crc;
            for (int i = 0; i < len; i++) crc = t[(crc ^ p[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }
    }

    /// <summary>Adler-32.</summary>
    internal static unsafe class Adler32
    {
        public static uint Update(uint adler, byte* p, int len)
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
}
