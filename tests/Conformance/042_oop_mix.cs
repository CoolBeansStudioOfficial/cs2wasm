using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

interface IRepository<T> where T : class, IEntity, new()
{
    T Create(Action<T>? init = null);
    IEnumerable<T> All { get; }
    T this[int id] { get; }
    int Count => All.Count();
}
interface IEntity { int Id { get; set; } }
interface IProducer<out T> { T Make(); }
interface IConsumer<in T> { void Eat(T item); }

class Item : IEntity { public int Id { get; set; } public string Label = ""; public override string ToString() => $"Item{Id}:{Label}"; }
class Special : Item { public new string ToString() => "hidden"; }

class Repo<T> : IRepository<T>, IDisposable where T : class, IEntity, new()
{
    readonly Dictionary<int, T> _items = new();
    int _next = 1;
    public bool Disposed { get; private set; }
    public T Create(Action<T>? init = null) { var t = new T { Id = _next++ }; init?.Invoke(t); _items[t.Id] = t; Changed?.Invoke(this, t.Id); return t; }
    public IEnumerable<T> All => _items.Values;
    public T this[int id] => _items.TryGetValue(id, out var v) ? v : throw new KeyNotFoundException($"no item {id}");
    public event EventHandler<int>? Changed;
    public void Dispose() { Disposed = true; Console.WriteLine("repo disposed"); }
}

abstract class Shape
{
    public abstract double Area();
    public virtual string Describe() => $"{GetType().Name} with area {Area():0.##}";
    public static Shape operator +(Shape a, Shape b) => new Composite(a, b);
}
sealed class Sq(double side) : Shape { public override double Area() => side * side; }
sealed class Composite(Shape a, Shape b) : Shape
{
    public override double Area() => a.Area() + b.Area();
    public override string Describe() => "composite: " + base.Describe();
}

class AppException : Exception { public int Code { get; } public AppException(string m, int code) : base(m) { Code = code; } }
class NotFound : AppException { public NotFound(string what) : base(what + " not found", 404) { } }

static class Ext
{
    public static string Shout(this string s, int times = 1) => string.Concat(Enumerable.Repeat(s.ToUpper() + "!", times));
    public static T? FirstOrNull<T>(this IEnumerable<T> xs, Func<T, bool> p) where T : struct { foreach (var x in xs) if (p(x)) return x; return null; }
    public static int Sum(params int[] xs) => xs.Sum();
}

static class Program
{
    static void Swap<T>(ref T a, ref T b) { (a, b) = (b, a); }
    static bool TryDivide(int a, int b, out int q, out int r) { if (b == 0) { q = r = 0; return false; } q = a / b; r = a % b; return true; }
    static double Scale(in double v, double factor = 2, bool negate = false) => negate ? -v * factor : v * factor;

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        using (var repo = new Repo<Item>())
        {
            var log = new List<string>();
            EventHandler<int> h = (s, id) => log.Add("made " + id);
            repo.Changed += h;
            repo.Changed += (s, id) => log.Add("again " + id);
            repo.Create(i => i.Label = "a"); repo.Create(i => i.Label = "b");
            repo.Changed -= h;
            repo.Create();
            Console.WriteLine(string.Join(", ", log));
            IRepository<Item> ir = repo;
            Console.WriteLine(ir.Count + " " + repo[2] + " " + string.Join(";", ir.All));
            try { var _ = repo[9]; } catch (KeyNotFoundException e) { Console.WriteLine(e.Message); }
        }
        IProducer<object> po = new StringMaker(); IConsumer<string> cs = new ObjEater();
        cs.Eat(po.Make().ToString()!);
        Item sp = new Special { Id = 7 };
        Console.WriteLine(sp + " " + ((Special)sp).ToString());
        Shape s = new Sq(2) + new Sq(1.5);
        Console.WriteLine(s.Describe());
        try { throw new NotFound("user"); }
        catch (AppException e) when (e.Code == 404) { Console.WriteLine($"{e.GetType().Name}: {e.Message} ({e.Code})"); }
        finally { Console.WriteLine("finally"); }
        Console.WriteLine("hey".Shout() + " " + "x".Shout(times: 3) + " " + Ext.Sum() + " " + Ext.Sum(1, 2, 3));
        Console.WriteLine(new[] { 1, 5, 9 }.FirstOrNull(x => x > 4) + " " + (new[] { 1 }.FirstOrNull(x => x > 4)?.ToString() ?? "none"));
        int a = 1, b = 2; Swap(ref a, ref b);
        Console.WriteLine($"{a}{b} {(TryDivide(17, 5, out var q, out var r) ? $"{q}r{r}" : "x")} {TryDivide(1, 0, out _, out _)} {Scale(3)} {Scale(3, negate: true)}");
        object[] things = { 3, "three", 3.0, null!, new Item { Id = 3 }, Perm.X, (1, "t") };
        foreach (var t in things)
            Console.Write(t switch
            {
                int i when i > 2 => "big-int ",
                string { Length: > 3 } str => $"long-string({str}) ",
                double d => $"double({d}) ",
                Item { Id: var id } => $"item#{id} ",
                Perm p => $"perm({p}) ",
                ValueTuple<int, string> tup => $"tuple({tup.Item2}) ",
                null => "null ",
                _ => "other ",
            });
        Console.WriteLine();
        int? maybe = null; maybe ??= 5; maybe += 1;
        string? name = null;
        Console.WriteLine($"{maybe} {name?.Length ?? -1} {name ?? "anon"} {maybe is not null and > 3}");
        try { int z = 0; Console.WriteLine(10 / z); } catch (DivideByZeroException e) { Console.WriteLine(e.Message); }
        try { checked { int big = int.MaxValue; big++; } } catch (OverflowException e) { Console.WriteLine(e.Message); }
        var raw = """
            {"k": "v"}
            """;
        Console.WriteLine(raw + " " + @"C:\path" + $"|{"x",4}|{"y",-3}|{12.5,8:F2}|");
        var counters = new List<Func<int>>();
        for (int i = 0; i < 3; i++) counters.Add(() => i * 10);
        foreach (var x in new[] { 1, 2 }) counters.Add(() => x);
        Console.WriteLine(string.Join(",", counters.Select(c => c())));
    }

    enum Perm { X, Y }
    class StringMaker : IProducer<string> { public string Make() => "made"; }
    class ObjEater : IConsumer<object> { public void Eat(object o) => Console.WriteLine("ate " + o); }
}
