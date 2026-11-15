namespace Cs2Wasm.Wasm;

/// <summary>
/// The data segment travels compressed (section 5: packing data segments): string literals are UTF-16 with headers, and
/// the type tables repeat, so a plain LZ77 shrinks them to about a quarter. The format is a sequence of
/// (literal count, literal bytes, match length - 4, match distance) with every number an unsigned LEB128, ending with a
/// last literal run; the module's start code expands it into place (EmitDecoder).
/// </summary>
static class DataCompression
{
    const int MinMatch = 4, Chain = 64;

    public static byte[] Compress(byte[] d)
    {
        var o = new List<byte>(d.Length / 3);
        void U(int v) { while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; } o.Add((byte)v); }
        var head = new Dictionary<int, int>();
        var prev = new int[d.Length];
        int Key(int i) => d[i] | d[i + 1] << 8 | d[i + 2] << 16 | d[i + 3] << 24;
        void Insert(int i)
        {
            if (i + MinMatch > d.Length) return;
            int k = Key(i);
            prev[i] = head.TryGetValue(k, out int p) ? p : -1;
            head[k] = i;
        }
        (int len, int dist) Find(int i)
        {
            if (i + MinMatch > d.Length || !head.TryGetValue(Key(i), out int j)) return (0, 0);
            int best = 0, bd = 0;
            for (int c = 0; j >= 0 && c < Chain; c++, j = prev[j])
            {
                int l = 0;
                while (i + l < d.Length && d[j + l] == d[i + l]) l++;
                if (l > best) { best = l; bd = i - j; }
            }
            return (best, bd);
        }
        static int Len(int v) { int n = 1; while (v >= 0x80) { v >>= 7; n++; } return n; }
        int litStart = 0, pos = 0;
        while (pos < d.Length)
        {
            var (len, dist) = Find(pos);
            if (len >= MinMatch)
            {
                // lazy: a longer match one byte later is worth a literal
                Insert(pos);
                var (l2, d2) = Find(pos + 1);
                if (l2 > len + 1) { pos++; len = l2; dist = d2; }
                else { head[Key(pos)] = prev[pos]; if (prev[pos] < 0) head.Remove(Key(pos)); }
            }
            if (len >= MinMatch && Len(len - MinMatch) + Len(dist) < len)
            {
                U(pos - litStart);
                for (int k = litStart; k < pos; k++) o.Add(d[k]);
                U(len - MinMatch); U(dist);
                for (int k = pos; k < pos + len; k++) Insert(k);
                pos += len;
                litStart = pos;
            }
            else { Insert(pos); pos++; }
        }
        U(d.Length - litStart);
        for (int k = litStart; k < d.Length; k++) o.Add(d[k]);
        return [.. o];
    }

    /// <summary>The expander, (src, dst, end): writes the bytes the compressed data at src stands for to dst..end.</summary>
    public static void EmitDecoder(Emitter e)
    {
        const int src = 0, dst = 1, end = 2;
        int n = e.AddLocal(ValType.I32), off = e.AddLocal(ValType.I32), sh = e.AddLocal(ValType.I32), by = e.AddLocal(ValType.I32);
        void Varint(int into)
        {
            e.I32(0); e.Set(into); e.I32(0); e.Set(sh);
            var l = e.Loop();
            e.Get(src); e.Load(Op.I32Load8U); e.Set(by);
            e.Get(src); e.I32(1); e.Op(Op.I32Add); e.Set(src);
            e.Get(into); e.Get(by); e.I32(0x7F); e.Op(Op.I32And); e.Get(sh); e.Op(Op.I32Shl); e.Op(Op.I32Or); e.Set(into);
            e.Get(sh); e.I32(7); e.Op(Op.I32Add); e.Set(sh);
            e.Get(by); e.I32(0x80); e.Op(Op.I32And); e.BrIf(l);
            e.End();
        }
        var exit = e.Block();
        var top = e.Loop();
        Varint(n);
        e.Get(dst); e.Get(src); e.Get(n); e.Emit(Op.MemoryCopy);
        e.Get(src); e.Get(n); e.Op(Op.I32Add); e.Set(src);
        e.Get(dst); e.Get(n); e.Op(Op.I32Add); e.Set(dst);
        e.Get(dst); e.Get(end); e.Op(Op.I32GeU); e.BrIf(exit);
        Varint(n);
        Varint(off);
        e.Get(n); e.I32(MinMatch); e.Op(Op.I32Add); e.Set(n);
        // byte by byte: a match may overlap what it copies
        var copy = e.Loop();
        e.Get(dst); e.Get(dst); e.Get(off); e.Op(Op.I32Sub); e.Load(Op.I32Load8U); e.Store(Op.I32Store8);
        e.Get(dst); e.I32(1); e.Op(Op.I32Add); e.Set(dst);
        e.Get(n); e.I32(1); e.Op(Op.I32Sub); e.Tee(n); e.BrIf(copy);
        e.End();
        e.Br(top);
        e.End();
        e.End();
    }
}
