// A sweep over what the language defines exactly: struct copies, interface members, initialization and evaluation
// order, refs, closures, exception flow, iterators, records, number conversions, text, generics, nullables, async,
// locks. One line of output per area, against .NET.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

struct Pt { public int X, Y; public Pt(int x, int y) { X = x; Y = y; } public void Move(int d) { X += d; } public override string ToString() => $"({X},{Y})"; }
struct Counter : IDisposable, IEnumerator<int> { public int N; public int Current => N; object IEnumerator.Current => N; public bool MoveNext() => ++N < 4; public void Reset() { N = 0; } public void Dispose() { Program.Log("cd" + N); } }
class Holder { public Pt P; public Pt[] Arr = new Pt[2]; public Pt Prop { get; set; } public readonly Pt RO = new(1, 1); public List<Pt> L = new() { new Pt(1, 2) }; }
interface IGreeter { string Greet() => "hello from " + Name; string Name { get; } static string Shared() => "shared"; }
interface ILoud : IGreeter { string IGreeter.Greet() => "HELLO FROM " + Name; }
class G1 : IGreeter { public string Name => "g1"; }
class G2 : ILoud { public string Name => "g2"; }
class G3 : IGreeter { public string Name => "g3"; public string Greet() => "own g3"; }
class G4 : G3, IGreeter { string IGreeter.Greet() => "explicit g4"; public new string Greet() => "new g4"; }
abstract class Shape { public static int Made; protected Shape() { Made++; Init(); } protected virtual void Init() { } public abstract double Area { get; } }
class Sq : Shape { double _s = 2; public string Trace = "field"; protected override void Init() { Trace += "+init(" + _s + ")"; } public Sq() { Trace += "+ctor"; } public override double Area => _s * _s; }
class StaticOrder { public static string Log = ""; public static int A = Note("A"); public static int B = Note("B"); static StaticOrder() { Log += "cctor;"; } static int Note(string s) { Log += s + ";"; return 1; } }
class Lazy1 { public static readonly string V = Program.Log2("lazy1"); }
record Person(string Name, int Age) { public virtual string Kind => "person"; }
record Student(string Name, int Age, string School) : Person(Name, Age) { public override string Kind => "student"; }
record struct RS(int A, string B);
class Node<T> : IEnumerable<T> where T : IComparable<T>
{
    public T Val; public Node<T>? L, R; public Node(T v) { Val = v; }
    public void Add(T v) { if (v.CompareTo(Val) < 0) { if (L == null) L = new(v); else L.Add(v); } else { if (R == null) R = new(v); else R.Add(v); } }
    public IEnumerator<T> GetEnumerator() { if (L != null) foreach (var x in L) yield return x; yield return Val; if (R != null) foreach (var x in R) yield return x; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
static class Ext { public static T Tap<T>(this T x, Action<T> a) { a(x); return x; } public static int WordCount(this string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length; }

static class Program
{
    static readonly StringBuilder s_log = new();
    public static void Log(string s) => s_log.Append(s).Append(';');
    public static string Log2(string s) { Log(s); return s; }
    static string Flush() { var s = s_log.ToString(); s_log.Clear(); return s; }
    static void T(string name, Action a)
    {
        try { a(); } catch (Exception e) { Console.WriteLine(name + " threw " + e.GetType().Name + ": " + e.Message); }
    }
    static int F(string tag, int v) { Log(tag); return v; }
    static int Named(int a, int b = 10, params int[] rest) => a * 100 + b * 10 + rest.Sum();
    static void Swap<T>(ref T a, ref T b) { (a, b) = (b, a); }
    static bool TryDiv(int a, int b, out int q) { if (b == 0) { q = -1; return false; } q = a / b; return true; }
    static int Finally1() { try { return F("try", 1); } finally { Log("fin"); } }
    static int Finally2() { int x = 1; try { return x; } finally { x = 5; Log("x" + x); } }
    static string Nested() { try { try { throw new InvalidOperationException("inner"); } finally { Log("f1"); } } catch (Exception e) when (F("filter", 1) == 1) { try { throw new ArgumentException("second", e); } catch (ArgumentException e2) { return e2.InnerException!.Message + "/" + e2.Message; } finally { Log("f2"); } } finally { Log("f3"); } }
    static IEnumerable<int> Iter(int n) { Log("start"); try { for (int i = 0; i < n; i++) { try { yield return i; } finally { Log("inner" + i); } } } finally { Log("outer"); } }
    static async Task<int> Slow(int v, int ms) { await Task.Delay(ms); return v; }
    static async Task<string> AsyncFlow()
    {
        var sb = new StringBuilder();
        try { await Task.WhenAll(Slow(1, 5), Task.Run(() => throw new InvalidOperationException("bg"))); } catch (InvalidOperationException e) { sb.Append(e.Message + ";"); }
        using var cts = new CancellationTokenSource();
        var t = Task.Run(async () => { await Task.Delay(5000, cts.Token); return 1; });
        cts.Cancel();
        try { await t; } catch (OperationCanceledException) { sb.Append("cancelled;" + t.Status + ";"); }
        var first = await Task.WhenAny(Slow(1, 200), Slow(2, 1));
        sb.Append(first.Result + ";");
        var al = new AsyncLocal<int> { Value = 5 };
        await Task.Run(() => { sb.Append(al.Value + ";"); al.Value = 9; });
        sb.Append(al.Value + ";");
        int total = 0; await foreach (var x in Gen()) total += x; sb.Append(total);
        return sb.ToString();
        async IAsyncEnumerable<int> Gen([EnumeratorCancellation] CancellationToken ct = default) { for (int i = 1; i <= 3; i++) { await Task.Delay(1, ct); yield return i; } }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        T("structs", () => { var h = new Holder(); h.P.Move(3); h.Arr[1].Move(4); h.Prop.Move(5); h.RO.Move(6); var copy = h.P; copy.X = 99; var l = h.L; var e = l[0]; e.X = 50; object boxed = h.P; ((Pt)boxed).Move(1); var arr2 = (Pt[])h.Arr.Clone(); arr2[1].X = 7; Pt? np = h.P; var np2 = np; var d = new Dictionary<int, Pt> { [1] = new Pt(1, 1) }; var dp = d[1]; dp.X = 8; ref Pt rp = ref h.Arr[0]; rp.Y = 42; Span<Pt> sp = h.Arr; sp[0].X = 13; var tup = (h.P, 1); tup.Item1.X = 77; Console.WriteLine($"{h.P} {h.Arr[1]} {h.Prop} {h.RO} {copy} {l[0]} {boxed} {arr2[1]} {np2!.Value.X} {d[1]} {h.Arr[0]} {tup.Item1} {default(Pt)} {new Pt().Equals(default(Pt))} {h.P.Equals(new Pt(3, 0))} {h.P.GetHashCode() == new Pt(3, 0).GetHashCode()}"); });
        T("using struct", () => { using (var c = new Counter()) { var c2 = c; c2.MoveNext(); } var cc = new Counter(); while (cc.MoveNext()) { } IEnumerator<int> ie = new Counter(); ie.MoveNext(); ie.MoveNext(); ie.Dispose(); cc.Dispose(); Console.WriteLine(Flush() + ie.Current); });
        T("interfaces", () => { IGreeter[] gs = { new G1(), new G2(), new G3(), new G4() }; Console.WriteLine(string.Join("|", gs.Select(g => g.Greet())) + "|" + new G4().Greet() + "|" + ((G3)new G4()).Greet() + "|" + IGreeter.Shared()); });
        T("init order", () => { var s = new Sq(); Console.WriteLine(s.Trace + " " + Shape.Made + " " + s.Area + " " + StaticOrder.A + StaticOrder.Log + " " + Flush() + Lazy1.V + Flush()); });
        T("eval order", () => { int[] a = new int[3]; int i = 0; a[i++] = i++ + i++; a[F("idx", 2)] = F("val", 5); var r = Named(b: F("b", 2), a: F("a", 1)); var r2 = Named(1, 2, 3, 4); var r3 = Named(rest: new[] { 1 }, a: 2); int x = 5; x += x++ * ++x; string? s = null; var len = s?.Length + F("rhs", 1); var t = F("l", 1) + F("m", 2) * F("r", 3); bool sc = F("f", 0) == 1 && F("skipped", 1) == 1 || F("or", 1) == 1; Console.WriteLine($"{a[0]},{a[1]},{a[2]} {i} {r} {r2} {r3} {x} {len.HasValue} {t} {sc} {Flush()}"); });
        T("refs", () => { int p = 1, q = 2; Swap(ref p, ref q); string s1 = "a", s2 = "b"; Swap(ref s1, ref s2); TryDiv(7, 2, out var d1); var ok = TryDiv(1, 0, out int d2); int[] arr = { 1, 2, 3 }; ref int mid = ref arr[1]; mid *= 10; ref readonly int ro = ref arr[2]; var span = arr.AsSpan(); ref int first = ref MemoryMarshal.GetReference(span); first = 100; int local = 5; ref int rl = ref local; rl++; Func<int> cap = () => local * 2; local = 50; Console.WriteLine($"{p}{q} {s1}{s2} {d1} {ok}{d2} {string.Join(",", arr)} {ro} {local} {cap()}"); });
        T("closures", () => { var fs = new List<Func<int>>(); for (int i = 0; i < 3; i++) fs.Add(() => i); var gs = new List<Func<int>>(); foreach (var v in new[] { 1, 2, 3 }) gs.Add(() => v * 10); int acc = 0; Action<int> add = n => acc += n; add(5); add(6); Func<int, Func<int, int>> curry = a => b => a * b + acc; Func<int, int>? fact = null; fact = n => n <= 1 ? 1 : n * fact!(n - 1); var counters = Enumerable.Range(0, 2).Select(k => { int c = k * 100; return (Func<int>)(() => ++c); }).ToList(); counters[0](); counters[0](); static int Twice(Func<int, int> f, int v) => f(f(v)); Action multi = () => Log("m1"); multi += () => Log("m2"); multi += multi; multi -= () => Log("m1"); multi(); Console.WriteLine($"{string.Join("", fs.Select(f => f()))} {string.Join("", gs.Select(f => f()))} {acc} {curry(3)(4)} {fact(6)} {counters[0]()}{counters[1]()} {Twice(z => z * 3, 2)} {multi.GetInvocationList().Length} {Flush()}"); });
        T("exceptions", () => { Console.WriteLine(Finally1() + " " + Finally2() + " " + Nested() + " " + Flush()); try { try { throw new InvalidOperationException("a"); } catch (InvalidOperationException) { throw; } } catch (Exception e) { Log(e.Message + (e.StackTrace != null)); } try { try { throw new Exception("orig"); } finally { Log("cleanup"); } } catch (Exception e) { Log(e.Message); } try { checked { byte b = 255; b++; } } catch (OverflowException) { Log("ovf"); } try { object o = 5; var sx = (string)o; } catch (InvalidCastException e) { Log(e.Message.Contains("Int32") ? "cast-msg" : "cast"); } var ex = new ArgumentNullException("p"); Log(ex.ParamName + ":" + ex.Message); try { throw new AggregateException("agg", new Exception("i1")).Flatten(); } catch (AggregateException e) { Log(e.Message); } Console.WriteLine(Flush()); });
        T("iterators", () => { foreach (var x in Iter(3)) { if (x == 1) break; } Log("|"); using (var e = Iter(2).GetEnumerator()) { e.MoveNext(); } Log("|"); var lazy = Iter(5).Where(x => x % 2 == 0).Select(x => x * x); Log("built"); var first = lazy.First(); Log("|"); var tree = new Node<int>(5); foreach (var v in new[] { 3, 8, 1, 4, 9, 7 }) tree.Add(v); Console.WriteLine(Flush() + first + " " + string.Join(",", tree) + " " + tree.Max() + " " + string.Join(",", new Node<string>("m").Tap(n => { n.Add("z"); n.Add("a"); }))); });
        T("records", () => { var p = new Person("Ann", 30); var s = new Student("Ann", 30, "MIT"); Person ps = s; var p2 = p with { Age = 31 }; var (n, a) = p; var rs = new RS(1, "x"); var rs2 = rs with { A = 2 }; var set = new HashSet<Person> { p, new Person("Ann", 30), s, ps }; Console.WriteLine($"{p} {s} {p == new Person("Ann", 30)} {p.Equals(ps)} {ps == s} {p2} {n}{a} {ps.Kind} {rs} {rs == new RS(1, "x")} {rs2.A} {set.Count} {p.GetHashCode() == new Person("Ann", 30).GetHashCode()} {ps is Student { School: "MIT" }} {(ps with { Name = "Bo" }).GetType().Name}"); });
        T("numbers", () => { unchecked { int big = int.MaxValue; big += 1; uint u = (uint)-1; long l = (long)u << 33; short sh = (short)70000; byte by = (byte)-1; int shl = 1 << 33; int shr = -16 >> 2; int ushr = -16 >>> 28; long lshl = 1L << 65; ulong mulhi = Math.BigMul(ulong.MaxValue, 3, out ulong lo); double dz = 0.0; Console.WriteLine($"{big} {u} {l} {sh} {by} {shl} {shr} {ushr} {lshl} {mulhi}{lo} {1 / dz} {-1 / dz} {dz / dz} {(int)double.NaN} {(int)1e20} {(long)-1e30} {(uint)-1.5} {(byte)300.7} {(char)65601} {5 % -3} {-5 % 3} {-5.5 % 2} {7 / 2 * 2.0} {(float)0.1 == 0.1} {0.1 + 0.2 == 0.3} {(decimal)0.1 + (decimal)0.2 == 0.3m} {1e16 + 1} {float.Epsilon} {(double)float.MaxValue} {double.MaxValue:R} {long.MinValue / -1L == long.MinValue} {int.MinValue % -1} {Math.Abs(-7.5)} {Math.Sign(-0.0)} {(sbyte)0x80} {0b1010_1010 ^ 0xFF} {~5} {'a' + 1} {(char)('a' + 1)} {1_000_000 * 3L} {decimal.MaxValue} {1m / 3} {(1m / 3) * 3 == 1} {10m % 3} {(int)2.5m} {Math.Round(2.5m)} {decimal.Parse("1e3", NumberStyles.Float)} {0.1m + 0.2m}"); } });
        T("strings", () => { string a = "hello", b = "hel" + "lo".ToString(), c = string.Copy(a); object oa = a, ob = b; var sb = new StringBuilder("x"); string? nul = null; Console.WriteLine($"{a == b} {oa == ob} {ReferenceEquals(a, c)} {a.Equals(ob)} {string.IsInterned(a) != null} {"" + null + 1 + 'c' + 2.50 + true + (object?)null} {nul + "x" + nul} {$"{1,5}|{2,-5}|{3.14159:F2}|{255:X}|{nul}|{(nul ?? "d")}|{{}}"} {"a\tb\\n"}{@"c:\d"}{"\u00e9\x41"} {"abc".CompareTo("ABC") > 0} {string.Compare("a", "B", StringComparison.Ordinal) > 0} {"ǅ".ToLower() + "ǅ".ToUpper()} {"i".ToUpperInvariant()} {"straße".Length} {"a,b,,c".Split(',').Length} {string.Join("-", "a b  c".Split(' '))} {"  pad ".Trim().PadRight(6, '.')}| {"hello world foo".WordCount()} {"abc"[1]} {"x".PadLeft(3)}{new string('z', 3)} {"Hello".Replace('l', 'L').IndexOf("LL")} {"{0} {1:N1} {0}".Length} {string.Format("{0} {1:N1} {0}", "q", 1234.56)} {"tschüß".EndsWith("ß")} {"abc".Contains('b')} {char.ToUpper('ß')} {char.IsDigit('٣')} {(int)char.GetNumericValue('٣')} {"a".CompareTo(null)} {"e\u0301".Normalize().Length} {"x".GetHashCode() == "x".GetHashCode()} {sb.Append(1).Append('y').Append((string?)null).Append(2.5).Insert(0, true)}"); });
        T("generics", () => { static T Id<T>(T x) => x; static string TypeName<T>() => typeof(T).Name; static bool IsDefault<T>(T x) => EqualityComparer<T>.Default.Equals(x, default!); static T Create<T>() where T : new() => new T(); static int Count<T>(IEnumerable<T> xs) where T : struct => xs.Count(); static TOut Conv<TIn, TOut>(TIn x, Func<TIn, TOut> f) => f(x); var d = new Dictionary<string, List<(int, string?)>> { ["k"] = new() { (1, "a"), (2, null) } }; var nested = new List<Dictionary<int, HashSet<string>>> { new() { [1] = new() { "x" } } }; Console.WriteLine($"{Id(5)}{Id("s")}{Id<object?>(null) == null} {TypeName<int>()}{TypeName<List<string>>()}{TypeName<int?>()}{TypeName<(int, string)>()}{TypeName<int[]>()} {IsDefault(0)}{IsDefault("")}{IsDefault<string?>(null)}{IsDefault(default(Pt))}{IsDefault<int?>(null)} {Create<List<int>>().Count}{Create<Pt>()}{Create<int>()} {Count(new[] { 1, 2 })} {Conv(3, x => x.ToString() + "!")} {d["k"][1].Item2 ?? "null"} {nested[0][1].Contains("x")} {typeof(Dictionary<,>).GetGenericArguments().Length} {new List<int> { 1 } is IEnumerable<int>}{new List<string>() is IEnumerable<object>}{(object)new List<int>() is IEnumerable<object>}{new int[0] is IList<int>}{(object)new int[0] is IEnumerable<long>}{(object)new string[0] is object[]}{(object)new int[0] is object[]} {Comparer<string>.Default.Compare("a", "b")}{EqualityComparer<Pt>.Default.Equals(new Pt(1, 2), new Pt(1, 2))}{Nullable.Compare<int>(null, 1)}"); });
        T("nullable", () => { int? a = null, b = 3; bool? t = true, n = null; DateTime? dt = null; Pt? pt = new Pt(1, 2); object? o = a; object ob = b; Console.WriteLine($"{a + b}|{a > b}{a < b}{a == null}{a != b}{a == a} {b * 2} {a ?? b} {(a ?? 0) + (b ?? 0)} {t & n}|{t | n}{n == null} {dt?.Year}|{dt.HasValue} {pt?.X} {pt.Value} {o == null} {ob.GetType().Name} {(int?)ob + 1} {a.GetValueOrDefault(7)} {b.Equals(3)} {a.Equals(null)} {a.ToString() == ""} {b.GetHashCode()} {Nullable.GetUnderlyingType(typeof(int?))!.Name} {(b is int i ? i : -1)} {a is null} {default(int?) is { } z} {new int?[] { 1, null, 3 }.Sum()} {new int?[] { 1, null, 3 }.Max()} {new int?[] { null }.Min() == null} {(b > 2 ? b : null) is int}"); });
        T("async", () => Console.WriteLine(AsyncFlow().GetAwaiter().GetResult()));
        T("locks", () => { object gate = new(); int v = 0; lock (gate) { lock (gate) { v++; } } bool taken = false; Monitor.TryEnter(gate, ref taken); if (taken) Monitor.Exit(gate); var rw = new ReaderWriterLockSlim(); rw.EnterReadLock(); rw.ExitReadLock(); rw.EnterWriteLock(); rw.ExitWriteLock(); var ev = new CountdownEvent(3); var ts = Enumerable.Range(0, 3).Select(_ => Task.Run(() => { Interlocked.Increment(ref v); ev.Signal(); })).ToArray(); ev.Wait(); var bc = new System.Collections.Concurrent.BlockingCollection<int>(2); var prod = Task.Run(() => { for (int i = 0; i < 5; i++) bc.Add(i); bc.CompleteAdding(); }); int sum = bc.GetConsumingEnumerable().Sum(); var ch = System.Threading.Channels.Channel.CreateUnbounded<int>(); ch.Writer.TryWrite(4); ch.Writer.Complete(); int got = ch.Reader.ReadAsync().AsTask().Result; var barrier = 0; Parallel.ForEach(Enumerable.Range(1, 50), new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => Interlocked.Add(ref barrier, i)); var lazy = new Lazy<int>(() => Interlocked.Increment(ref v), LazyThreadSafetyMode.ExecutionAndPublication); Parallel.Invoke(() => _ = lazy.Value, () => _ = lazy.Value); Console.WriteLine($"{v} {taken} {sum} {got} {barrier} {lazy.Value} {Monitor.IsEntered(gate)} {Thread.CurrentThread.IsBackground} {new ThreadLocal<string>(() => "tl").Value}"); });
    }
}
