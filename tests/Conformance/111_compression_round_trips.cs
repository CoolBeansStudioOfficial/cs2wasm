// Compression round trips: every level and format, inputs that are empty, tiny, text, random, runs and mixed, written
// and read in pieces of odd sizes, flushed in the middle; streams made by zlib itself read back; several gzip members
// in a row; zip archives made, read and updated. (What is printed does not depend on the compressed bytes: those are
// any valid deflate stream, here as on .NET.)
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

static class Program
{
    const string RefGzip =
        "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";
    const string RefZlib =
        "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";

    static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; }
        return ~crc;
    }
    static uint s_seed = 12345;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }

    static byte[] Make(string kind, int n)
    {
        var d = new byte[n];
        switch (kind)
        {
            case "random": for (int i = 0; i < n; i++) d[i] = (byte)Next(); break;
            case "zeros": break;
            case "runs": for (int i = 0; i < n;) { byte v = (byte)Next(); int run = 1 + (int)(Next() % 700); while (run-- > 0 && i < n) d[i++] = v; } break;
            case "text": { var words = new[] { "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "and", "runs", "away" }; int i = 0; while (i < n) { var w = words[Next() % words.Length]; foreach (var c in w) if (i < n) d[i++] = (byte)c; if (i < n) d[i++] = (byte)(Next() % 9 == 0 ? '\n' : ' '); } break; }
            case "mixed": for (int i = 0; i < n; i++) d[i] = (i / 4096) % 3 == 0 ? (byte)Next() : (i / 4096) % 3 == 1 ? (byte)(i % 251) : (byte)'x'; break;
            case "far": { for (int i = 0; i < n; i++) d[i] = i < 40000 ? (byte)Next() : d[i - 32768 + (i % 7 == 0 ? 1 : 0)]; break; }
        }
        return d;
    }

    static Stream Wrap(string format, Stream s, CompressionLevel? level) => format switch
    {
        "deflate" => level is { } l ? new DeflateStream(s, l, true) : new DeflateStream(s, CompressionMode.Decompress, true),
        "zlib" => level is { } l ? new ZLibStream(s, l, true) : new ZLibStream(s, CompressionMode.Decompress, true),
        _ => level is { } l ? new GZipStream(s, l, true) : new GZipStream(s, CompressionMode.Decompress, true),
    };

    static byte[] Pack(string format, CompressionLevel level, byte[] data, int chunk, bool flushMiddle)
    {
        var ms = new MemoryStream();
        using (var z = Wrap(format, ms, level))
        {
            for (int at = 0; at < data.Length; at += chunk)
            {
                z.Write(data, at, Math.Min(chunk, data.Length - at));
                if (flushMiddle && at <= data.Length / 2 && at + chunk > data.Length / 2) z.Flush();
            }
        }
        return ms.ToArray();
    }

    static byte[] Unpack(string format, byte[] packed, int chunk)
    {
        using var z = Wrap(format, new MemoryStream(packed), null);
        var outp = new MemoryStream();
        var buf = new byte[chunk];
        int n;
        while ((n = z.Read(buf, 0, buf.Length)) > 0) outp.Write(buf, 0, n);
        return outp.ToArray();
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        foreach (var (name, b64, format) in new[] { ("gzip", RefGzip, "gzip"), ("zlib", RefZlib, "zlib") })
        {
            var back = Unpack(format, Convert.FromBase64String(b64), 333);
            Console.WriteLine("zlib's " + name + ": " + back.Length + " " + Crc(back) + " " + Encoding.ASCII.GetString(back, 0, 24));
        }
        var levels = new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize };
        var inputs = new (string kind, int n)[] { ("zeros", 0), ("text", 1), ("text", 2), ("text", 300), ("text", 70000), ("random", 200000), ("zeros", 300000), ("runs", 250000), ("mixed", 400000), ("far", 150000) };
        int[] writeChunks = { 1, 7, 1000, 65537, 1 << 20 }, readChunks = { 1, 13, 4096, 100000 };
        int cases = 0, failures = 0;
        foreach (var (kind, n) in inputs)
        {
            var data = Make(kind, n);
            uint crc = Crc(data);
            var line = new StringBuilder(kind + " " + n + ":");
            foreach (var format in new[] { "deflate", "zlib", "gzip" })
                foreach (var level in levels)
                {
                    int wc = writeChunks[cases % writeChunks.Length], rc = readChunks[cases % readChunks.Length];
                    if (wc == 1 && n > 100000) wc = 977;
                    if (rc == 1 && n > 100000) rc = 211;
                    cases++;
                    var packed = Pack(format, level, data, wc, cases % 3 == 0);
                    var back = Unpack(format, packed, rc);
                    bool same = back.Length == data.Length && Crc(back) == crc;
                    if (!same) { failures++; line.Append(" FAIL(" + format + "," + level + "," + wc + "," + rc + ")"); }
                    // (what any deflate does: long text, runs and zeros get much smaller at every level but the
                    // first; nothing grows by much)
                    bool shrinks = level != CompressionLevel.NoCompression && n >= 70000 && kind is "text" or "zeros" or "runs";
                    if (shrinks && packed.Length * 2 > data.Length) { failures++; line.Append(" BIG(" + format + "," + level + "," + packed.Length + ")"); }
                    if (packed.Length > data.Length + data.Length / 8 + 64) { failures++; line.Append(" GREW(" + format + "," + level + "," + packed.Length + ")"); }
                }
            Console.WriteLine(line.Append(" ok").ToString());
        }
        Console.WriteLine(cases + " cases, " + failures + " failures");

        // several gzip members in a row are one stream of their contents
        var a = Pack("gzip", CompressionLevel.Optimal, Encoding.ASCII.GetBytes("first member, "), 5, false);
        var b = Pack("gzip", CompressionLevel.Fastest, Encoding.ASCII.GetBytes("second member, "), 500, false);
        var c = Pack("gzip", CompressionLevel.NoCompression, Encoding.ASCII.GetBytes("third."), 2, true);
        Console.WriteLine(Encoding.ASCII.GetString(Unpack("gzip", a.Concat(b).Concat(c).ToArray(), 3)));
        // data that is no stream
        try { Unpack("gzip", Encoding.ASCII.GetBytes("this is not gzip data at all"), 10); Console.WriteLine("read"); } catch (InvalidDataException) { Console.WriteLine("InvalidDataException"); }
        try { Unpack("zlib", new byte[] { 0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, 10); Console.WriteLine("read"); } catch (InvalidDataException) { Console.WriteLine("InvalidDataException"); }
        // an empty input is an empty stream that reads as nothing
        Console.WriteLine(Unpack("deflate", Pack("deflate", CompressionLevel.Optimal, Array.Empty<byte>(), 1, false), 10).Length + " " + Unpack("gzip", Array.Empty<byte>(), 10).Length);

        // zip archives
        var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
        {
            for (int i = 0; i < 20; i++)
            {
                var e = zip.CreateEntry("dir" + (i % 3) + "/file" + i + ".bin", i % 4 == 0 ? CompressionLevel.NoCompression : i % 4 == 1 ? CompressionLevel.Fastest : CompressionLevel.Optimal);
                e.LastWriteTime = new DateTimeOffset(2024, 2, 29, 12, 0, 2 * (i % 30), TimeSpan.Zero);
                using var s = e.Open();
                var d = Make(i % 2 == 0 ? "text" : "runs", i * 3000);
                s.Write(d);
            }
            zip.CreateEntry("empty/");
        }
        zipBytes.Position = 0;
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Update, true))
        {
            Console.WriteLine(zip.Entries.Count + " " + zip.Entries.Sum(e => e.Length) + " " + zip.Entries.All(e => e.Length == 0 || e.CompressedLength <= e.Length + 16) + " " + zip.GetEntry("dir1/file4.bin")!.LastWriteTime.Second + " " + zip.Entries[^1].FullName);
            long sum = 0;
            foreach (var e in zip.Entries) { using var s = e.Open(); var m = new MemoryStream(); s.CopyTo(m); sum += Crc(m.ToArray()) == e.Crc32 ? m.Length : -1000000; }
            Console.WriteLine(sum);
            zip.GetEntry("dir0/file3.bin")!.Delete();
            using (var w = new StreamWriter(zip.CreateEntry("added.txt").Open())) w.Write("added later");
            using (var s = zip.GetEntry("dir2/file2.bin")!.Open()) { s.SetLength(0); s.Write(Encoding.ASCII.GetBytes("rewritten")); }
        }
        zipBytes.Position = 0;
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Read))
        {
            Console.WriteLine(zip.Entries.Count + " " + (zip.GetEntry("dir0/file3.bin") == null) + " " + new StreamReader(zip.GetEntry("added.txt")!.Open()).ReadToEnd() + " " + new StreamReader(zip.GetEntry("dir2/file2.bin")!.Open()).ReadToEnd() + " " + zip.GetEntry("dir1/file19.bin")!.Length);
        }
    }
}
