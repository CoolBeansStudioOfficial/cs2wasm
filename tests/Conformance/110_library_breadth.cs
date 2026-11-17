// The library beyond the core: compression, JSON (the document model, the writer and nodes; serialization of the
// program's own types by reflection is not here: it makes generic converters at run time), regular expressions, big
// numbers, text encodings, formatting.
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

enum Level { Low, Mid, High }
static class Program
{
    static void T(string name, Func<string> f)
    {
        try { Console.WriteLine(name + ": " + f()); } catch (Exception e) { Console.WriteLine(name + " threw " + e.GetType().Name + ": " + e.Message); }
    }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var text = string.Join(" ", Enumerable.Range(0, 400).Select(i => "word" + (i % 17)));
        var raw = Encoding.UTF8.GetBytes(text);
        T("gzip", () =>
        {
            var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(raw, 0, raw.Length);
            var packed = ms.ToArray();
            using var back = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
            var outp = new MemoryStream(); back.CopyTo(outp);
            return (packed.Length < raw.Length / 4) + " " + outp.ToArray().AsSpan().SequenceEqual(raw) + " " + packed[0].ToString("x2") + packed[1].ToString("x2");
        });
        T("deflate", () =>
        {
            var ms = new MemoryStream();
            using (var d = new DeflateStream(ms, CompressionLevel.Fastest, true)) d.Write(raw);
            ms.Position = 0;
            using var back = new DeflateStream(ms, CompressionMode.Decompress);
            return new StreamReader(back).ReadToEnd() == text ? "round trip" : "differs";
        });
        T("inflate fixed", () =>
        {
            // "hello hello hello hello" as a raw deflate stream
            byte[] fixedStream = Convert.FromBase64String("y0jNyclXyEAnAQ==");
            using var d = new DeflateStream(new MemoryStream(fixedStream), CompressionMode.Decompress);
            return new StreamReader(d).ReadToEnd();
        });
        T("zlib", () =>
        {
            var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, true)) z.Write(raw);
            ms.Position = 0;
            using var back = new ZLibStream(ms, CompressionMode.Decompress);
            var outp = new MemoryStream(); back.CopyTo(outp);
            return outp.Length + " " + ms.ToArray()[0].ToString("x2");
        });
        T("zip", () =>
        {
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                using (var w = new StreamWriter(zip.CreateEntry("a/one.txt").Open())) w.Write(text);
                using (var w = new StreamWriter(zip.CreateEntry("two.txt", CompressionLevel.NoCompression).Open())) w.Write("second");
            }
            ms.Position = 0;
            using var read = new ZipArchive(ms, ZipArchiveMode.Read);
            return string.Join(",", read.Entries.Select(e => e.FullName + ":" + e.Length + ":" + new StreamReader(e.Open()).ReadToEnd().Length)) + " " + read.GetEntry("two.txt")!.CompressedLength;
        });
        T("crc", () => Hashing_Crc(raw));

        T("json dom", () => { using var doc = JsonDocument.Parse("{\"a\":[1,2,{\"b\":null,\"c\":true,\"d\":1e3,\"e\":\"\\u00e9\\n\"}],\"n\":-0.5}"); var a = doc.RootElement.GetProperty("a"); return a.GetArrayLength() + " " + a[2].GetProperty("d").GetDouble().ToString(CultureInfo.InvariantCulture) + " " + a[2].GetProperty("e").GetString()!.Length + " " + doc.RootElement.GetProperty("n").GetRawText() + " " + a[2].GetProperty("b").ValueKind + " " + string.Join("|", a[2].EnumerateObject().Select(p => p.Name)); });
        T("json writer", () => { var ms = new MemoryStream(); using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true })) { w.WriteStartObject(); w.WriteNumber("pi", 3.14159); w.WriteStartArray("xs"); w.WriteStringValue("a<b"); w.WriteNullValue(); w.WriteBooleanValue(false); w.WriteEndArray(); w.WriteEndObject(); } return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n").Replace("\n", "\\n"); });
        T("json node", () => { var n = System.Text.Json.Nodes.JsonNode.Parse("{\"x\":{\"y\":[10,20]}}")!; n["x"]!["y"]![1] = 99; n["z"] = "new"; return n.ToJsonString() + " " + (int)n["x"]!["y"]![0]!; });

        T("regex", () => { var m = Regex.Match("order 2024-02-29 #A17, 2023-12-01 #B2", @"(?<y>\d{4})-(?<m>\d\d)-(?<d>\d\d)\s+#(?<id>[A-Z]\d+)"); return m.Success + " " + m.Groups["y"].Value + m.Groups["id"].Value + " " + m.Index + " " + m.NextMatch().Groups["id"].Value + " " + m.NextMatch().NextMatch().Success; });
        T("regex replace", () => Regex.Replace("a1b22c333", @"\d+", m => "<" + m.Value.Length + ">") + " " + Regex.Replace("John Smith", @"(\w+) (\w+)", "$2, $1") + " " + string.Join("|", Regex.Split("a, b;c  d", @"[,;\s]+")));
        T("regex options", () => Regex.IsMatch("HELLO\nworld", "^world$", RegexOptions.Multiline | RegexOptions.IgnoreCase) + " " + Regex.Matches("aAbBcC", "[a-c]", RegexOptions.IgnoreCase).Count + " " + Regex.Match("xyz", "(?<=x)y(?=z)").Value + " " + Regex.Match("aaa", "a+?").Length + " " + Regex.Match("<a><b>", "<.+?>").Value + " " + Regex.Escape("1+1=2?") + " " + new Regex(@"\bcat\b", RegexOptions.Compiled).Matches("cat concat cat.").Count);
        T("regex backref", () => Regex.IsMatch("abcabc", @"^(abc)\1$") + " " + Regex.Match("2+3*4", @"(\d)([+*])(\d)").Groups.Count + " " + Regex.Match("key = value", @"^\s*(?<k>\w+)\s*=\s*(?<v>.*)$").Groups["v"].Value + " " + Regex.Replace("  trim  me ", @"^\s+|\s+$", "") + ".");
        T("regex bad", () => new Regex("(unclosed").ToString());

        T("biginteger", () => { var f = BigInteger.One; for (int i = 2; i <= 40; i++) f *= i; return f + " " + BigInteger.Pow(2, 200) % 1000000007 + " " + BigInteger.Parse("-123456789012345678901234567890") / 987654321 + " " + BigInteger.GreatestCommonDivisor(f, BigInteger.Pow(6, 30)) + " " + f.ToString("x").Length + " " + (double)f + " " + BigInteger.ModPow(7, 1000, 13) + " " + f.GetBitLength(); });
        T("complex", () => { var c = new Complex(3, 4); return c.Magnitude.ToString(CultureInfo.InvariantCulture) + " " + (c * Complex.Conjugate(c)).Real + " " + Complex.Sqrt(-1).Imaginary + " " + Complex.Exp(new Complex(0, Math.PI)).Real.ToString("F3", CultureInfo.InvariantCulture); });
        T("int128", () => { Int128 a = Int128.MaxValue; UInt128 b = UInt128.MaxValue / 3; return a + " " + b + " " + (a / 7 % 1000) + " " + Int128.Parse("-170141183460469231731687303715884105728") + " " + ((Half)1.5 + (Half)2).ToString(CultureInfo.InvariantCulture) + " " + BitOperations.PopCount(0xF0F0u) + BitOperations.Log2(1000) + BitOperations.RotateLeft(1u, 33); });
        T("vectors", () => { var v = new Vector3(1, 2, 3); var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2); var r = Vector3.Transform(v, q); var m = Matrix4x4.CreateTranslation(1, 2, 3) * Matrix4x4.CreateScale(2); return Vector3.Dot(v, v) + " " + Vector3.Cross(v, Vector3.UnitX) + " " + r.X.ToString("F3", CultureInfo.InvariantCulture) + r.Y.ToString("F3", CultureInfo.InvariantCulture) + " " + m.M41 + " " + Vector2.Normalize(new Vector2(3, 4)).X.ToString("F2", CultureInfo.InvariantCulture) + " " + Vector4.Lerp(Vector4.Zero, Vector4.One, 0.25f).W + " " + Vector<int>.Count.ToString().Length; });

        T("encodings", () => Encoding.UTF8.GetByteCount("héllo €𝄞") + " " + Encoding.Unicode.GetBytes("a€").Length + " " + Encoding.UTF32.GetString(Encoding.UTF32.GetBytes("𝄞x")).Length + " " + Encoding.ASCII.GetString(new byte[] { 72, 200, 105 }) + " " + Encoding.Latin1.GetString(new byte[] { 0xE9 }) + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes("any carnal pleas")) + " " + Convert.ToHexString(new byte[] { 1, 0xAB }) + " " + BitConverter.ToString(BitConverter.GetBytes(1.0f)) + " " + new Rune(0x1D11E).Utf16SequenceLength + " " + "é".Normalize(NormalizationForm.FormD).Length + " " + string.Concat("x𝄞y".EnumerateRunes().Select(r => r.Value.ToString("x") + ",")));
        T("binary", () => { Span<byte> buf = stackalloc byte[16]; BinaryPrimitives.WriteInt32BigEndian(buf, 0x01020304); BinaryPrimitives.WriteDoubleLittleEndian(buf[4..], 1.5); var ms = new MemoryStream(); using (var bw = new BinaryWriter(ms, Encoding.UTF8, true)) { bw.Write(42); bw.Write("hé"); bw.Write(3.5m); bw.Write7BitEncodedInt(300); } ms.Position = 0; using var br = new BinaryReader(ms); return buf[0] + " " + BinaryPrimitives.ReadDoubleLittleEndian(buf[4..]).ToString(CultureInfo.InvariantCulture) + " " + br.ReadInt32() + br.ReadString() + br.ReadDecimal().ToString(CultureInfo.InvariantCulture) + br.Read7BitEncodedInt() + " " + ms.Length + " " + BinaryPrimitives.ReverseEndianness((ushort)0x1234).ToString("x"); });
        T("formatting", () => string.Create(CultureInfo.InvariantCulture, $"{1234567.891:N2} {0.1234:P1} {255:X4} {-5:D3} {1e-7:G3} {12345.678:0,0.0#;(0.0);zero} {Math.PI:E3} {1.0 / 3:R} {new DateTime(2024, 2, 29, 13, 5, 9):yyyy-MM-dd'T'HH:mm:ss ddd MMM} {TimeSpan.FromSeconds(93784.5):c} {new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)):zzz} {Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"):N} {new Version(1, 2, 3)} {(decimal)1 / 3} {double.MaxValue} {float.Epsilon} {long.MinValue:N0}"));
        T("parsing", () => int.Parse(" -42 ", CultureInfo.InvariantCulture) + " " + double.Parse("1e-3", CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + " " + decimal.Parse("1,234.50", NumberStyles.Number, CultureInfo.InvariantCulture) + " " + int.TryParse("12x", out _) + " " + DateTime.Parse("2024-02-29T13:05:09Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).Kind + " " + TimeSpan.Parse("1.02:03:04", CultureInfo.InvariantCulture).TotalMinutes + " " + Enum.Parse<Level>("mid", true) + " " + Convert.ToInt32("ff", 16) + " " + bool.Parse("TRUE") + " " + long.Parse("7fffffff", NumberStyles.HexNumber) + " " + DateOnly.ParseExact("29/02/2024", "dd/MM/yyyy", CultureInfo.InvariantCulture).DayOfYear + " " + TimeOnly.Parse("23:59", CultureInfo.InvariantCulture).AddMinutes(2).Hour);
        T("immutable", () => { var l = ImmutableList.Create(1, 2, 3).Add(4).RemoveAt(0); var d = ImmutableDictionary<string, int>.Empty.Add("a", 1).SetItem("a", 2).Add("b", 3); var s = ImmutableSortedSet.Create(5, 1, 3).Union(new[] { 2 }); var a = ImmutableArray.Create("x", "y").Insert(1, "m"); var b = ImmutableArray.CreateBuilder<int>(); b.AddRange(1, 2); return string.Join("", l) + " " + d["a"] + d.Count + " " + string.Join("", s) + " " + string.Join("", a) + " " + b.ToImmutable().Length + " " + ImmutableStack.Create(1).Push(2).Peek() + " " + ImmutableHashSet.Create(1, 1, 2).Count + " " + ImmutableQueue.Create(1, 2).Dequeue().Peek(); });
        T("collections", () => { var pq = new PriorityQueue<string, int>(); pq.Enqueue("c", 3); pq.Enqueue("a", 1); pq.Enqueue("b", 2); var ll = new LinkedList<int>(new[] { 1, 2, 3 }); ll.AddBefore(ll.Last!, 9); var ss = new SortedSet<int> { 5, 1, 3 }; var sd = new SortedDictionary<string, int> { ["b"] = 2, ["a"] = 1 }; var st = new Stack<int>(new[] { 1, 2 }); var q = new Queue<int>(new[] { 1, 2 }); var bits = new System.Collections.BitArray(new[] { true, false, true }); var pool = ArrayPool<int>.Shared.Rent(10); ArrayPool<int>.Shared.Return(pool); return pq.Dequeue() + pq.Peek() + " " + string.Join("", ll) + " " + ss.Min + ss.Max + string.Join("", ss.GetViewBetween(2, 5)) + " " + sd.First().Key + " " + st.Pop() + q.Dequeue() + " " + bits.Cast<bool>().Count(x => x) + " " + (pool.Length >= 10) + " " + new HashSet<int> { 1, 2 }.IsSubsetOf(new[] { 1, 2, 3 }) + " " + new List<int> { 3, 1, 2 }.BinarySearch(1); });
        T("env", () => (Environment.NewLine.Length >= 1) + " " + (Environment.ProcessorCount > 0) + " " + (Environment.GetEnvironmentVariable("CS2WASM_SURELY_NOT_SET") == null) + " " + (Environment.TickCount64 >= 0) + " " + (Environment.Version.Major >= 11) + " " + Path.Combine("a", "b", "..", "c.txt").Replace('\\', '/') + " " + Path.GetExtension("x.tar.gz") + " " + Path.GetFileNameWithoutExtension("/p/q/file.name.cs") + " " + Path.ChangeExtension("a.b", "c") + " " + Path.IsPathRooted("rel/x"));
    }
    static string Hashing_Crc(byte[] data)
    {
        // CRC-32 (the zip one) by table, against the value a zip entry records
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; }
        crc = ~crc;
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true)) using (var s = zip.CreateEntry("x").Open()) s.Write(data);
        ms.Position = 0;
        using var read = new ZipArchive(ms, ZipArchiveMode.Read);
        return (read.Entries[0].Crc32 == crc).ToString();
    }
}
