// A second sweep: layouts and pointers, operators, classes and variance, newer syntax, reflection, streams and
// files, timers.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
sealed class TagAttribute<T> : Attribute { public string Name { get; } public T? Value { get; init; } public TagAttribute(string name) { Name = name; } }

[StructLayout(LayoutKind.Explicit)]
struct Union { [FieldOffset(0)] public float F; [FieldOffset(0)] public uint U; [FieldOffset(0)] public byte B0; [FieldOffset(3)] public byte B3; }
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct Packed { public byte A; public int B; public short C; }
[InlineArray(4)] struct Four { int _e; }

readonly struct Money : IEquatable<Money>, IComparable<Money>, IFormattable
{
    public readonly decimal Amount; public readonly string Cur;
    public Money(decimal a, string c) { Amount = a; Cur = c; }
    public static implicit operator Money(int v) => new(v, "EUR");
    public static explicit operator decimal(Money m) => m.Amount;
    public static Money operator +(Money a, Money b) => a.Cur == b.Cur ? new(a.Amount + b.Amount, a.Cur) : throw new InvalidOperationException("currency");
    public static Money operator checked +(Money a, Money b) => new(checked(a.Amount + b.Amount), a.Cur);
    public static bool operator ==(Money a, Money b) => a.Equals(b);
    public static bool operator !=(Money a, Money b) => !a.Equals(b);
    public static bool operator true(Money a) => a.Amount != 0;
    public static bool operator false(Money a) => a.Amount == 0;
    public static Money operator ++(Money a) => new(a.Amount + 1, a.Cur);
    public bool Equals(Money o) => Amount == o.Amount && Cur == o.Cur;
    public override bool Equals(object? o) => o is Money m && Equals(m);
    public override int GetHashCode() => HashCode.Combine(Amount, Cur);
    public int CompareTo(Money o) => Amount.CompareTo(o.Amount);
    public string ToString(string? f, IFormatProvider? p) => f == "S" ? $"{Amount:0}{Cur}" : $"{Amount:F2} {Cur}";
    public override string ToString() => ToString(null, null);
}

class Base<T> { public static int Count; public virtual string Who() => "Base<" + typeof(T).Name + ">"; static Base() { Count = 100; } }
class Derived : Base<int>, IEnumerable<int>, IDisposable, IAsyncDisposable
{
    public required string Name { get; init; }
    public int this[int i] => i * 2;
    public string this[string a, int b = 3] => a + b;
    int _x = 5; public ref int X => ref _x;
    public override string Who() => "Derived:" + base.Who();
    public IEnumerator<int> GetEnumerator() { try { yield return 1; yield return 2; } finally { Program.Log("enum-finally"); } }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() => Program.Log("dispose " + Name);
    public ValueTask DisposeAsync() { Program.Log("dispose-async " + Name); return default; }
}
interface ICovariant<out T> { T Get(); }
interface IContra<in T> { string Take(T t); }
class Box<T>(T value) : ICovariant<T>, IContra<T> { public T Get() => value; public string Take(T t) => "took " + t; }
class Person { public string Name { get; set; } = ""; public int Age { get; set; } public List<string> Tags { get; set; } = new(); public Dictionary<string, double>? Scores { get; set; } public string Secret { get; set; } = "x"; public Person? Friend { get; set; } }
file static class Ext { public static string Twice(this string s) => s + s; public static IEnumerable<T> EveryOther<T>(this IEnumerable<T> xs) { bool k = true; foreach (var x in xs) { if (k) yield return x; k = !k; } } }

static class Program
{
    static readonly StringBuilder s_log = new();
    public static void Log(string s) => s_log.Append(s).Append(';');
    static void T(string name, Action a)
    {
        try { a(); } catch (Exception e) { Console.WriteLine(name + " threw " + e.GetType().Name + ": " + e.Message); }
    }
    static ref int Pick(int[] a, int i) => ref a[i];
    static int SumIn(in Packed p) => p.A + p.B + p.C;
    static unsafe int Apply(delegate*<int, int, int> f, int a, int b) => f(a, b);
    static int Mul(int a, int b) => a * b;
    static string Sw(string s) { switch (s) { case "a": case "b": return "ab"; case "c": goto case "a"; case var x when x.Length > 3: return "long"; default: return "?"; } }
    static T Max<T>(params ReadOnlySpan<T> xs) where T : IComparable<T> { T m = xs[0]; foreach (var x in xs) if (x.CompareTo(m) > 0) m = x; return m; }
    [Tag<int>("m", Value = 7)] [Tag<string>("n", Value = "v")] static void Tagged([CallerMemberName] string who = "", [CallerLineNumber] int line = 0) => Console.Write(who + (line > 0) + " ");
    static bool TryGet([NotNullWhen(true)] out string? s) { s = "ok"; return true; }

    static unsafe void UnsafeTest() { var u = new Union { F = 1.0f }; int[] arr = { 1, 2, 3, 4 }; long sum = 0; fixed (int* p = arr) { for (int* q = p; q < p + 4; q++) sum += *q; p[1] = 20; } byte* st = stackalloc byte[4]; *(int*)st = 0x01020304; var pk = new Packed { A = 1, B = 2, C = 3 }; var four = new Four(); four[2] = 9; Span<int> fs = four; Pick(arr, 3) = 40; ref int r = ref Pick(arr, 0); r++; Console.WriteLine(u.U.ToString("X") + " " + u.B3 + " " + sum + " " + arr[1] + " " + st[0] + st[3] + " " + sizeof(Packed) + " " + Marshal.SizeOf<Packed>() + " " + SumIn(pk) + " " + fs[2] + fs.Length + " " + arr[3] + arr[0] + " " + Apply(&Mul, 6, 7) + " " + Unsafe.SizeOf<Union>() + " " + Unsafe.As<float, int>(ref u.F) + " " + BitConverter.SingleToInt32Bits(2f) + " " + (nint)(void*)null + " " + Unsafe.AreSame(ref arr[0], ref r)); }
    // ("timing", the timer: it waits for the first tick, with a bound, where it slept 30 ms and then asked whether one had
    // come. A timer's due time is the earliest it fires, not the latest: .NET on Windows gives the first tick of a
    // 1 ms timer at 10 to 30 ms on a quiet machine (the clock's 15.6 ms), and at 30 to 100 ms in 114 runs of 400 with
    // eight processes on four cores; cs2wasm's is later still when the timer's thread is started right after others
    // (a Node worker takes 20 to 40 ms to start, and only one is kept ready). The line was False in one loaded run
    // of the suite in eight.)
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        T("unsafe", UnsafeTest);
        T("operators", () => { Money a = 5, b = new(2.5m, "EUR"); var c = a + b; c++; var l = new List<Money> { c, a, b }; l.Sort(); Console.Write($"{c} {c:S} {(decimal)c} {a == 5} {a != b} {(a ? "t" : "f")} {l[0]:S} {l.Max():S} {new HashSet<Money> { a, 5, b }.Count} "); try { _ = a + new Money(1, "USD"); } catch (InvalidOperationException e) { Console.Write(e.Message + " "); } try { _ = checked(new Money(decimal.MaxValue, "E") + new Money(1, "E")); } catch (OverflowException) { Console.WriteLine("overflow"); } });
        T("classes", () => { using (var d = new Derived { Name = "d1" }) { Console.Write(d.Who() + " " + d[4] + d["k"] + d["k", 9] + " " + Base<int>.Count + Base<string>.Count + " " + d.Sum() + " "); d.X += 10; Console.Write(d.X + " "); foreach (var x in d) { if (x == 1) break; } } async Task A() { await using var d2 = new Derived { Name = "d2" }; await Task.Yield(); } A().Wait(); ICovariant<object> co = new Box<string>("s"); IContra<string> contra = new Box<object>(1); object[] oa = new string[] { "x" }; try { oa[0] = 1; } catch (ArrayTypeMismatchException) { Log("mismatch"); } IEnumerable<object> eo = new List<string> { "q" }; Func<object, string> fo = o => "f" + o; Func<string, object> fs = fo; Console.WriteLine(co.Get() + " " + contra.Take("z") + " " + eo.First() + " " + fs("p") + " " + s_log); });
        T("language", () => { int[] xs = [1, 2, 3]; List<int> ys = [.. xs, 4, .. xs.Select(x => x * 10)]; Span<char> cs = ['a', 'b']; var anon = new { A = 1, B = "two" }; var anon2 = anon with { A = 5 }; (int n, string s) named = (1, "one"); var (h, (i, j)) = (1, (2, 3)); int? ni = null; int? nj = 5; var sw = ys switch { [1, .., var last] when last > 20 => "ends " + last, [] => "empty", _ => "other" }; static int Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1); int cap = 3; Func<int, int> add = x => x + cap; cap = 10; var raw = """
            a "quoted" \ line
              indented {{x}}
            """; var utf8 = "hé"u8; string? nul = null; Tagged(); Console.WriteLine(string.Join(",", ys) + " " + cs.Length + " " + anon2 + " " + anon.Equals(new { A = 1, B = "two" }) + " " + named.s + (named == (1, "one")) + " " + (h + i + j) + " " + (ni + nj) + (ni > nj) + (ni ?? -1) + (nj!.Value << 2) + " " + sw + " " + Fact(10) + " " + add(1) + " " + raw.Replace("\r", "").Replace("\n", "|") + " " + utf8.Length + " " + (nul?.Length ?? -1) + nul?.ToUpper() + " " + Sw("c") + Sw("long!") + Sw("z") + " " + Max(3, 9, 4) + Max("a", "c", "b") + " " + "ab".Twice() + string.Concat(xs.EveryOther()) + " " + (TryGet(out var got) ? got.Length : 0) + " " + nameof(List<int>.Count) + " " + (xs is [_, 2, _]) + " " + default(Money).Cur + (default(Money) == default) + " " + (object)1.5f + " " + (1, 2).CompareTo((1, 3)) + " " + typeof(Derived).GetInterfaces().Length + " " + (sizeof(int) + sizeof(char) + sizeof(bool) + sizeof(double))); });
        T("reflection", () => { var t = typeof(Derived); var m = typeof(Program).GetMethod("Tagged", BindingFlags.NonPublic | BindingFlags.Static)!; var d = (Derived)Activator.CreateInstance(t)!; var p = typeof(Person).GetProperty("Age")!; var per = new Person(); p.SetValue(per, 33); var mi = typeof(string).GetMethod("Substring", new[] { typeof(int), typeof(int) })!; var gt = typeof(Dictionary<,>).MakeGenericType(typeof(string), typeof(int)); IDictionary dict = new Dictionary<string, int>(); dict["k"] = 1; Expression<Func<int, int, int>> ex = (a, b) => a * b + 2; var body = (BinaryExpression)ex.Body; var del = Delegate.CreateDelegate(typeof(Func<int, int, int>), typeof(Program).GetMethod("Mul", BindingFlags.NonPublic | BindingFlags.Static)!); Console.WriteLine(t.BaseType!.Name + " " + t.BaseType.GetGenericArguments()[0].Name + " " + string.Join(",", m.GetCustomAttributes().Where(a => a.GetType().IsGenericType).Select(a => a.GetType().Name)) + " " + m.GetCustomAttribute<TagAttribute<int>>()!.Value + " " + per.Age + " " + mi.Invoke("hello", new object[] { 1, 3 }) + " " + dict.Count + gt.Name + " " + " " + ex.Compile()(3, 4) + body.NodeType + " " + ((Func<int, int, int>)del)(3, 5) + " " + string.Join(",", typeof(Person).GetProperties().Select(x => x.Name + ":" + x.PropertyType.Name)) + " " + typeof(Money).IsValueType + typeof(Money).GetFields().Length + " " + " " + typeof(List<>).GetGenericArguments()[0].Name + " " + Type.GetType("System.Int32")!.IsPrimitive + " " + typeof(Program).Assembly.GetTypes().Count(x => x.Name == "Person") + " " + Enum.GetValues(typeof(DayOfWeek)).Length + " " + typeof(int).GetMethod("Parse", new[] { typeof(string) })!.Invoke(null, new object[] { "12" })); });
        T("io", () => { var ms = new MemoryStream(); using (var bw = new BinaryWriter(ms, Encoding.UTF8, true)) { bw.Write(42); bw.Write(3.5); bw.Write("héllo"); bw.Write((byte)7); bw.Write7BitEncodedInt(300); } ms.Position = 0; using var br = new BinaryReader(ms); var a = br.ReadInt32(); var b = br.ReadDouble(); var c = br.ReadString(); var d = br.ReadByte(); var e = br.Read7BitEncodedInt(); var sr = new StringReader("line1\nline2\r\nline3"); var lines = new List<string>(); while (sr.ReadLine() is { } ln) lines.Add(ln); var sw = new StringWriter(); sw.Write("{0}-{1}", 1, 2); sw.WriteLine(true); var path = Path.Combine("dir", "sub", "..", "f.txt"); var tmp = "probe902.tmp"; File.WriteAllText(tmp, "abc\ndef"); File.AppendAllLines(tmp, new[] { "", "ghi" }); var read = File.ReadAllLines(tmp); using (var fs = new FileStream(tmp, FileMode.Open)) { fs.Seek(-3, SeekOrigin.End); Log(((char)fs.ReadByte()).ToString() + fs.Length); } var ex = File.Exists(tmp); File.Delete(tmp); using var w = new StreamWriter(new MemoryStream(), new UTF8Encoding(false)); w.Write("ü"); w.Flush(); Console.WriteLine(a + " " + b + " " + c + " " + d + " " + e + " " + ms.Length + " " + string.Join("|", lines) + " " + sw.ToString().TrimEnd() + " " + Path.GetFileNameWithoutExtension(path) + Path.GetExtension(path) + Path.GetFileName(Path.GetDirectoryName(path)) + " " + read.Length + read[1] + " " + ex + File.Exists(tmp) + " " + w.BaseStream.Length + " " + Encoding.Latin1.GetBytes("é")[0] + Encoding.ASCII.GetString(new byte[] { 72, 105 }) + Encoding.UTF32.GetByteCount("a") + Encoding.BigEndianUnicode.GetBytes("a")[1]); });
        T("timing", () => { var sw = System.Diagnostics.Stopwatch.StartNew(); Thread.Sleep(5); var el = sw.ElapsedMilliseconds; int fired = 0; using (var timer = new Timer(_ => Interlocked.Increment(ref fired), null, 1, 2)) { /* (until the first tick, not for a fixed 30 ms: .NET promises no tick by then, see the note at Main) */ SpinWait.SpinUntil(() => Volatile.Read(ref fired) > 0, 10000); } var now = DateTime.UtcNow; var tick = Environment.TickCount64; var cts = new CancellationTokenSource(10); var waited = cts.Token.WaitHandle.WaitOne(2000); var t = Task.Delay(5).ContinueWith(_ => 7); Console.WriteLine((el >= 4) + " " + (fired > 0) + " " + (now.Year >= 2024) + " " + (tick > 0) + " " + waited + " " + t.Result + " " + Task.WhenAny(Task.Delay(1), Task.Delay(5000)).Result.IsCompleted + " " + (Environment.ProcessorCount > 0) + " " + (Environment.CurrentManagedThreadId > 0) + " " + GC.GetTotalMemory(false).GetType().Name + GC.MaxGeneration.GetType().Name); });
    }
}
