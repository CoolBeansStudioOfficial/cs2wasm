// Generics: classes over value and reference types, generic methods, generic interfaces and virtual methods,
// statics per instantiation.
using System;

interface IBag<T> { void Add(T item); T Get(int i); int Count { get; } }

class Bag<T> : IBag<T>
{
    T[] _items = new T[2];
    int _n;
    public static int Made;
    public Bag() { Made++; }
    public void Add(T item)
    {
        if (_n == _items.Length)
        {
            var bigger = new T[_items.Length * 2];
            for (int i = 0; i < _n; i++) bigger[i] = _items[i];
            _items = bigger;
        }
        _items[_n++] = item;
    }
    public T Get(int i) => i < _n ? _items[i] : throw new IndexOutOfRangeException();
    public int Count => _n;
    public virtual string Describe() => "bag of " + _n;
}

class NamedBag<T> : Bag<T>
{
    readonly string _name;
    public NamedBag(string name) { _name = name; }
    public override string Describe() => _name + " with " + Count;
}

class Pair<A, B>
{
    public A First; public B Second;
    public Pair(A a, B b) { First = a; Second = b; }
    public Pair<B, A> Swap() => new Pair<B, A>(Second, First);
}

static class Program
{
    static T Last<T>(IBag<T> bag) => bag.Get(bag.Count - 1);
    static int Sum(Bag<int> b) { int s = 0; for (int i = 0; i < b.Count; i++) s += b.Get(i); return s; }
    static string Join<T>(IBag<T> bag) where T : class
    {
        string r = "";
        for (int i = 0; i < bag.Count; i++) r = r + bag.Get(i) + ";";
        return r;
    }
    static T Max<T>(T a, T b, Func2<T> greater) => greater.Greater(a, b) ? a : b;

    static void Main()
    {
        var ints = new Bag<int>();
        for (int i = 1; i <= 10; i++) ints.Add(i * i);
        Console.WriteLine(Sum(ints));
        Console.WriteLine(Last(ints));
        var words = new NamedBag<string>("words");
        words.Add("alpha"); words.Add("beta"); words.Add("gamma");
        Console.WriteLine(Join(words));
        Console.WriteLine(Last<string>(words));
        Bag<string> asBase = words;
        Console.WriteLine(asBase.Describe());
        Console.WriteLine(ints.Describe());
        Console.WriteLine(Bag<int>.Made + " " + Bag<string>.Made);
        var p = new Pair<int, string>(7, "seven");
        var q = p.Swap();
        Console.WriteLine(q.First + q.Second);
        var longs = new Bag<long>();
        longs.Add(1L << 40); longs.Add(3);
        Console.WriteLine(longs.Get(0) + longs.Get(1));
        Console.WriteLine(Max(3, 9, new IntGreater()));
        Console.WriteLine(Max("pear", "apple", new LongerString()));
    }
}

abstract class Func2<T> { public abstract bool Greater(T a, T b); }
sealed class IntGreater : Func2<int> { public override bool Greater(int a, int b) => a > b; }
sealed class LongerString : Func2<string> { public override bool Greater(string a, string b) => a.Length > b.Length; }
