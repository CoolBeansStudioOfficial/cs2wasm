// A store into an array in generic code, by what the type parameter is (Expressions.cs, the array element's place):
// an array of a sealed class (or of string, or of a delegate type) can only be of that very type, so its store needs
// no test of the array's type, in generic code as elsewhere; an array of any other reference type can be an array of
// a derived one, and what is stored must then be an element of the array that is there (ArrayTypeMismatchException).
// The same generic class and method are used for sealed classes, strings, delegates, open classes, interfaces,
// object and arrays, with arrays of the very type and of derived ones: every store that must throw is seen to, and
// every one that must not leaves its value. (With the test dropped for an element type that is not sealed, the
// lines "threw" become "stored".)
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

interface IThing { int V { get; } }
class Open : IThing { public int V { get; set; } }
class Derived : Open { }
sealed class Shut : IThing { public int V { get; set; } }
sealed class Other : IThing { public int V { get; set; } }
sealed class ShutDerived : Open { }
delegate int Fn(int x);

sealed class Bag<T> where T : class
{
    readonly T[] _a;
    int _n;
    public Bag(T[] a) { _a = a; }
    public void Set(int i, T v) { _a[i] = v; }
    public void Add(T v) { _a[_n++] = v; }
    public T Get(int i) => _a[i];
    public void Swap(int i, int j) { T t = _a[i]; _a[i] = _a[j]; _a[j] = t; }
    public void Fill(T v) { for (int i = 0; i < _a.Length; i++) _a[i] = v; }
}

static class Program
{
    static void Put<T>(T[] a, int i, T v) { a[i] = v; }
    static void PutBoth<T, U>(T[] a, U[] b, T v, U u) where T : class where U : class { a[0] = v; b[0] = u; }

    static string Try(Action a)
    {
        try { a(); return "stored"; }
        catch (ArrayTypeMismatchException) { return "threw"; }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        // sealed element types: the array is of the very type
        var shut = new Bag<Shut>(new Shut[3]);
        shut.Set(0, new Shut { V = 1 }); shut.Add(new Shut { V = 2 }); shut.Set(2, new Shut { V = 3 }); shut.Swap(0, 2);
        Console.WriteLine("Shut " + shut.Get(0).V + shut.Get(1)?.V + shut.Get(2).V);
        var other = new Bag<Other>(new Other[2]);
        other.Fill(new Other { V = 7 });
        Console.WriteLine("Other " + other.Get(0).V + other.Get(1).V);
        var strings = new Bag<string>(new string[2]);
        strings.Set(1, "s"); strings.Add("t");
        Console.WriteLine("string " + strings.Get(0) + strings.Get(1));
        var fns = new Bag<Fn>(new Fn[1]);
        fns.Set(0, x => x + 1);
        Console.WriteLine("Fn " + fns.Get(0)(41));
        var sd = new Bag<ShutDerived>(new ShutDerived[1]);
        sd.Set(0, new ShutDerived { V = 9 });
        Console.WriteLine("ShutDerived " + sd.Get(0).V);

        // open element types: an array of the very type takes any element of it
        var open = new Bag<Open>(new Open[2]);
        Console.WriteLine("Open[] as Open[]: " + Try(() => open.Set(0, new Open { V = 4 })) + " " + Try(() => open.Set(1, new Derived { V = 5 })) + " " + open.Get(0).V + open.Get(1).V);
        // an array of a derived type where the generic code has the base's
        var derived = new Bag<Open>(new Derived[2]);
        Console.WriteLine("Derived[] as Open[]: " + Try(() => derived.Set(0, new Open())) + " " + Try(() => derived.Set(1, new Derived { V = 6 })) + " " + (derived.Get(0) == null) + " " + derived.Get(1).V);
        var shutAsOpen = new Bag<Open>(new ShutDerived[2]);
        Console.WriteLine("ShutDerived[] as Open[]: " + Try(() => shutAsOpen.Add(new Derived())) + " " + Try(() => shutAsOpen.Add(new ShutDerived { V = 8 })) + " " + Try(() => shutAsOpen.Fill(new Open())) + " " + (shutAsOpen.Get(0) == null) + " " + shutAsOpen.Get(1).V);
        var things = new Bag<IThing>(new Shut[2]);
        Console.WriteLine("Shut[] as IThing[]: " + Try(() => things.Set(0, new Other())) + " " + Try(() => things.Set(1, new Shut { V = 2 })) + " " + Try(() => things.Swap(0, 1)) + " " + things.Get(0).V);
        var objects = new Bag<object>(new string[2]);
        Console.WriteLine("string[] as object[]: " + Try(() => objects.Set(0, 1)) + " " + Try(() => objects.Set(0, "a")) + " " + Try(() => objects.Fill(new object())) + " " + objects.Get(0));
        var arrays = new Bag<object[]>(new string[1][]);
        Console.WriteLine("string[][] as object[][]: " + Try(() => arrays.Set(0, new object[1])) + " " + Try(() => arrays.Set(0, new string[] { "q" })) + " " + arrays.Get(0)[0]);
        var fnsAsDelegates = new Bag<Delegate>(new Fn[1]);
        Console.WriteLine("Fn[] as Delegate[]: " + Try(() => fnsAsDelegates.Set(0, new Action(() => { }))) + " " + Try(() => fnsAsDelegates.Set(0, new Fn(x => x * 2))) + " " + ((Fn)fnsAsDelegates.Get(0))(21));

        // a generic method
        var sa = new Shut[1]; var oa = new Open[1]; Open[] da = new Derived[1]; object[] so = new string[1]; IThing[] ti = new Other[1];
        Console.WriteLine("Put: " + Try(() => Put(sa, 0, new Shut { V = 1 })) + " " + Try(() => Put(oa, 0, new Derived())) + " " + Try(() => Put(da, 0, new Open())) + " " + Try(() => Put(da, 0, new Derived()))
            + " " + Try(() => Put(so, 0, new object())) + " " + Try(() => Put(so, 0, "z")) + " " + Try(() => Put(ti, 0, new Shut())) + " " + Try(() => Put(ti, 0, new Other())) + " " + Try(() => Put<object>(sa, 0, "no")));
        Console.WriteLine("PutBoth: " + Try(() => PutBoth(sa, da, new Shut { V = 3 }, new Open())) + " " + sa[0].V + " " + Try(() => PutBoth(da, sa, new Open(), new Shut { V = 4 })) + " " + sa[0].V
            + " " + Try(() => PutBoth(so, ti, "k", new Shut())) + " " + so[0]);

        // the library's own: List<T> and Dictionary values of each kind (their arrays are of the very type)
        var ls = new List<Shut>(); var lo = new List<Open>(); var lt = new List<IThing>(); var lstr = new List<string>();
        for (int i = 0; i < 5; i++) { ls.Add(new Shut { V = i }); lo.Add(i % 2 == 0 ? new Open { V = i } : new Derived { V = i }); lt.Add(i % 2 == 0 ? new Shut { V = i } : new Other { V = i }); lstr.Add("s" + i); }
        ls[0] = ls[4]; lo[1] = lo[3]; lt[2] = lt[1]; lstr[3] = lstr[0];
        int sum = 0;
        for (int i = 0; i < 5; i++) sum += ls[i].V * 1000 + lo[i].V * 100 + lt[i].V * 10 + lstr[i].Length;
        Console.WriteLine("lists " + sum + " " + lo[1].GetType().Name + " " + lt[2].GetType().Name + " " + lstr[3]);
        var stack = new Stack<Shut>(); var queue = new Queue<Open>();
        for (int i = 0; i < 9; i++) { stack.Push(new Shut { V = i }); queue.Enqueue(new Derived { V = i }); }
        Console.WriteLine("stack and queue " + stack.Pop().V + stack.Peek().V + queue.Dequeue().V + queue.Peek().V + " " + stack.ToArray().GetType().Name + " " + queue.ToArray().GetType().Name);
    }
}
