// tests/CoreLibNatives: differential tests of the runtime's natives under .NET (see the csproj).
//   deflate: every case is compressed by .NET's shipped native zlib-ng (System.IO.Compression.Native's
//            CompressionNative_* exports) and by the C# port, with the same calls (input and output chunks, flush
//            codes), and the outputs, return codes and stream positions must be identical. The gzip header's OS byte
//            is the one known difference (Windows' zlib-ng writes 10, Emscripten's 3: zutil.h's OS_CODE) and is
//            compared as that.
//   errno:   spot checks of Errno.g.cs against Emscripten's and System.Native's values.
using System.Runtime.InteropServices;
using Il2Wasm.Runtime.Natives;

static unsafe class Program
{
    [StructLayout(LayoutKind.Sequential)]
    struct PalZStream
    {
        public byte* NextIn, NextOut;
        public byte* Msg;
        public void* State;
        public uint AvailIn, AvailOut;
    }

    const string Lib = "System.IO.Compression.Native";
    [DllImport(Lib)] static extern int CompressionNative_DeflateInit2_(PalZStream* s, int level, int method, int windowBits, int memLevel, int strategy);
    [DllImport(Lib)] static extern int CompressionNative_Deflate(PalZStream* s, int flush);
    [DllImport(Lib)] static extern int CompressionNative_DeflateEnd(PalZStream* s);
    [DllImport(Lib)] static extern int CompressionNative_DeflateReset(PalZStream* s);
    [DllImport(Lib)] static extern uint CompressionNative_Crc32(uint crc, byte* buffer, int len);

    static int s_cases, s_failed;
    static long s_bytes, s_calls;

    static int Main(string[] args)
    {
        ErrnoChecks();
        Crc32Checks();
        DeflateChecks(args.Contains("--quick"));
        Console.WriteLine($"{s_cases} cases, {s_failed} failed; deflate: {s_bytes} compressed bytes and {s_calls} calls compared");
        Console.WriteLine($"inflate: {s_inflateCases} streams; per-call positions differ from native in {s_inflateTraceDiffs}");
        return s_failed == 0 ? 0 : 1;
    }

    static void Check(bool ok, string what)
    {
        s_cases++;
        if (ok) return;
        s_failed++;
        if (s_failed <= 40) Console.WriteLine("FAIL " + what);
    }

    static void ErrnoChecks()
    {
        // Emscripten's numbers (WASI's) and System.Native's PAL codes
        Check(Errno.EBADF == 8 && Errno.ENOENT == 44 && Errno.EINVAL == 28 && Errno.EAGAIN == 6 && Errno.ENOTSUP == 138, "errno numbers");
        Check(Errno.PlatformToPal(8) == 0x10008 && Errno.PlatformToPal(44) == 0x1002D && Errno.PlatformToPal(0) == 0, "PlatformToPal");
        Check(Errno.PlatformToPal(9999) == 0x1FFFF, "PlatformToPal default ENONSTANDARD");
        Check(Errno.PalToPlatform(0x10008) == 8 && Errno.PalToPlatform(0x1FFFF) == -1, "PalToPlatform");
        for (int e = 0; e < 140; e++)
        {
            int pal = Errno.PlatformToPal(e);
            if (pal != 0x1FFFF) Check(Errno.PalToPlatform(pal) == e || e == Errno.EAGAIN || pal == Errno.PlatformToPal(Errno.PalToPlatform(pal)), $"round trip {e}");
        }
        Check(Errno.Message(8) == "Bad file descriptor" && Errno.Message(44) == "No such file or directory" && Errno.Message(0) == "Success" && Errno.Message(5000) == "Unknown error", "messages");
    }

    static void Crc32Checks()
    {
        var rng = new Random(7);
        foreach (int n in new[] { 0, 1, 7, 100, 4096, 100000 })
        {
            var data = new byte[n];
            rng.NextBytes(data);
            fixed (byte* p = data)
            {
                uint seed = (uint)rng.Next();
                Check(CompressionNative_Crc32(seed, p, n) == Il2Wasm.Runtime.Natives.Crc32.Update(seed, p, n), "crc32 " + n);
            }
        }
    }

    static byte[] Corpus(string kind, int n, int seed)
    {
        var rng = new Random(seed);
        var b = new byte[n];
        switch (kind)
        {
            case "zeros": break;
            case "random": rng.NextBytes(b); break;
            case "text":
                {
                    string[] words = ["the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "deflate", "window", "match", "length", "distance", "literal", "Huffman", "tree", "\n", ", ", ". "];
                    int i = 0;
                    while (i < n) { var w = words[rng.Next(words.Length)] + " "; foreach (char c in w) { if (i >= n) break; b[i++] = (byte)c; } }
                    break;
                }
            case "runs": for (int i = 0; i < n;) { byte v = (byte)rng.Next(4); int r = rng.Next(1, 300); while (r-- > 0 && i < n) b[i++] = v; } break;
            case "mixed":
                for (int i = 0; i < n; i++) b[i] = (i / 997 % 3) switch { 0 => (byte)rng.Next(256), 1 => (byte)(i % 7), _ => (byte)"abcabcabd"[i % 9] };
                break;
            case "periodic": for (int i = 0; i < n; i++) b[i] = (byte)(i * 31 % 251 ^ (i >> 12)); break;
        }
        return b;
    }

    /// <summary>One compression with a call plan, on one implementation: the output, and a trace of every call's
    /// return code and stream positions.</summary>
    static (byte[] Out, List<string> Trace) Run(bool native, byte[] input, int level, int windowBits, int memLevel, int strategy, int seed, int pattern)
    {
        var rng = new Random(seed);
        var output = new List<byte>();
        var trace = new List<string>();
        byte* outp = (byte*)NativeMemory.Alloc(70000);
        byte* inp = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, input.Length));
        input.CopyTo(new Span<byte>(inp, input.Length));
        PalZStream* zp = (PalZStream*)NativeMemory.AllocZeroed((nuint)sizeof(PalZStream));

        var pz = (CompressionNative.PalZStream*)zp;
        try
        {
            int r = native ? CompressionNative_DeflateInit2_(zp, level, 8, windowBits, memLevel, strategy)
                : CompressionNative.CompressionNative_DeflateInit2_(pz, level, 8, windowBits, memLevel, strategy);
            trace.Add("init " + r);
            if (r != 0) { trace.Add("end " + (native ? CompressionNative_DeflateEnd(zp) : CompressionNative.CompressionNative_DeflateEnd(pz))); return (output.ToArray(), trace); }
            int fed = 0;
            int Call(int flush, int outSize)
            {
                zp->NextOut = outp; zp->AvailOut = (uint)outSize;
                int res = native ? CompressionNative_Deflate(zp, flush) : CompressionNative.CompressionNative_Deflate(pz, flush);
                int produced = outSize - (int)zp->AvailOut;
                for (int i = 0; i < produced; i++) output.Add(outp[i]);
                string msg = zp->Msg == null ? "-" : Marshal.PtrToStringAnsi((IntPtr)zp->Msg)!;
                trace.Add($"deflate {flush} {res} in {zp->AvailIn} out {zp->AvailOut} msg {msg}");
                return res;
            }
            void Feed(int n) { zp->NextIn = inp + fed; zp->AvailIn = (uint)n; fed += n; }
            uint AvailIn() => zp->AvailIn;
            if (pattern == 0)
            {
                // DeflateStream's: the whole input, NoFlush with 8192-byte buffers until it is taken, then Finish
                Feed(input.Length);
                while (AvailIn() != 0) Call(0, 8192);
                int g = 0;
                while (Call(4, 8192) != 1 && g++ < 100000) { }
            }
            else
            {
                // random chunks of input and output, and flushes now and then
                int guard = 0;
                while (fed < input.Length && guard++ < 1000000)
                {
                    int n = Math.Min(input.Length - fed, rng.Next(1, pattern == 1 ? 50000 : 3000));
                    Feed(n);
                    int flush = rng.Next(10) switch { 0 => 2, 1 => 3, 2 => 1, 3 => 5, _ => 0 };
                    while (true)
                    {
                        int res = Call(flush, rng.Next(1, pattern == 1 ? 20000 : 600));
                        if (res < 0 || (AvailIn() == 0 && (flush == 0 || zp->AvailOut != 0))) break;
                    }
                }
                int g = 0;
                while (Call(4, rng.Next(1, 5000)) != 1 && g++ < 1000000) { }
            }
            trace.Add("end " + (native ? CompressionNative_DeflateEnd(zp) : CompressionNative.CompressionNative_DeflateEnd(pz)));
        }
        finally
        {
            NativeMemory.Free(outp); NativeMemory.Free(inp); NativeMemory.Free(zp);
        }
        return (output.ToArray(), trace);
    }

    [DllImport(Lib)] static extern int CompressionNative_InflateInit2_(PalZStream* s, int windowBits);
    [DllImport(Lib)] static extern int CompressionNative_Inflate(PalZStream* s, int flush);
    [DllImport(Lib)] static extern int CompressionNative_InflateEnd(PalZStream* s);

    static int s_inflateTraceDiffs, s_inflateCases;

    /// <summary>Inflate as DeflateStream reads: 8192-byte input buffers, the caller's output buffer (here 4096).</summary>
    static (byte[] Out, List<string> Trace) InflateRun(bool native, byte[] input, int windowBits)
    {
        var output = new List<byte>();
        var trace = new List<string>();
        byte* outp = (byte*)NativeMemory.Alloc(4096);
        byte* inp = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, input.Length));
        input.CopyTo(new Span<byte>(inp, input.Length));
        PalZStream* zp = (PalZStream*)NativeMemory.AllocZeroed((nuint)sizeof(PalZStream));
        var pz = (CompressionNative.PalZStream*)zp;
        try
        {
            int r = native ? CompressionNative_InflateInit2_(zp, windowBits) : CompressionNative.CompressionNative_InflateInit2_(pz, windowBits);
            trace.Add("init " + r);
            int fed = 0, guard = 0;
            // (DeflateStream.ReadCore: inflate; output: return it; none and the input used up: read more, or stop at its end)
            while (guard++ < 1000000)
            {
                zp->NextOut = outp; zp->AvailOut = 4096;
                r = native ? CompressionNative_Inflate(zp, 0) : CompressionNative.CompressionNative_Inflate(pz, 0);
                int produced = 4096 - (int)zp->AvailOut;
                for (int i = 0; i < produced; i++) output.Add(outp[i]);
                trace.Add($"inflate {r} in {zp->AvailIn} out {zp->AvailOut}");
                if (r != 0 && r != -5) break;
                if (produced != 0) continue;
                if (zp->AvailIn == 0)
                {
                    if (fed >= input.Length) break;
                    int n = Math.Min(8192, input.Length - fed);
                    zp->NextIn = inp + fed; zp->AvailIn = (uint)n; fed += n;
                }
            }
            trace.Add("end " + (native ? CompressionNative_InflateEnd(zp) : CompressionNative.CompressionNative_InflateEnd(pz)));
        }
        finally
        {
            NativeMemory.Free(outp); NativeMemory.Free(inp); NativeMemory.Free(zp);
        }
        return (output.ToArray(), trace);
    }

    static void InflateCheck(byte[] compressed, byte[] original, int windowBits, string what)
    {
        var a = InflateRun(true, compressed, windowBits);
        var b = InflateRun(false, compressed, windowBits);
        s_inflateCases++;
        Check(a.Out.AsSpan().SequenceEqual(original) && b.Out.AsSpan().SequenceEqual(original), "inflate output " + what);
        // (the final return code is part of the output's meaning; the stream positions of each call are reported)
        Check(a.Trace[^2].Split(' ')[1] == b.Trace[^2].Split(' ')[1], $"inflate result {what}: {a.Trace[^2]} / {b.Trace[^2]}");
        if (!a.Trace.SequenceEqual(b.Trace)) s_inflateTraceDiffs++;
    }

    static void DeflateChecks(bool quick)
    {
        string[] kinds = ["zeros", "random", "text", "runs", "mixed", "periodic"];
        int[] sizes = quick ? [0, 1, 3, 1000, 70000] : [0, 1, 2, 3, 5, 100, 1000, 33000, 70000, 300000];
        int seed = 1;
        foreach (var kind in kinds)
            foreach (int size in sizes)
            {
                var input = Corpus(kind, size, seed++);
                for (int level = 0; level <= 9; level++)
                    for (int strategy = 0; strategy <= 4; strategy++)
                        foreach (int wb in quick ? [-15, 31] : new[] { -15, 15, 31, -9, 12 })
                            foreach (int pattern in quick ? [0, 2] : new[] { 0, 1, 2 })
                            {
                                if (pattern != 0 && strategy != 0 && level % 3 != 0) continue;
                                int memLevel = level == 0 ? 7 : (wb == 12 ? 5 : 8);
                                var a = Run(true, input, level, wb, memLevel, strategy, seed * 31 + pattern, pattern);
                                var b = Run(false, input, level, wb, memLevel, strategy, seed * 31 + pattern, pattern);
                                // (the gzip header's OS byte: Windows 10, Emscripten 3)
                                if (wb > 15 && a.Out.Length > 9 && a.Out[9] == 10 && b.Out.Length > 9 && b.Out[9] == 3) b.Out[9] = 10;
                                string what = $"{kind} {size} level {level} strategy {strategy} windowBits {wb} pattern {pattern}";
                                s_bytes += a.Out.Length; s_calls += a.Trace.Count;
                                bool same = a.Out.AsSpan().SequenceEqual(b.Out) && a.Trace.SequenceEqual(b.Trace);
                                if (!same)
                                {
                                    int at = 0;
                                    while (at < a.Out.Length && at < b.Out.Length && a.Out[at] == b.Out[at]) at++;
                                    int t = 0;
                                    while (t < a.Trace.Count && t < b.Trace.Count && a.Trace[t] == b.Trace[t]) t++;
                                    what += $": native {a.Out.Length} bytes, port {b.Out.Length}, first difference at byte {at}; trace {(t < a.Trace.Count ? a.Trace[t] : "-")} / {(t < b.Trace.Count ? b.Trace[t] : "-")}";
                                }
                                Check(same, what);
                                if (pattern == 0 && strategy == 0 && a.Trace[0] == "init 0") InflateCheck(a.Out, input, wb, what);
                            }
            }
    }
}
