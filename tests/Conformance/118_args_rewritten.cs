// A variable that is written while a value read from it earlier in the same call is still in use: by a later
// argument (`F(x, x = y)`), by the callee through `ref` or `out`, by a local function or lambda the call runs, by a
// static, an element or a list the callee clears. The first argument's object must live until the call returns.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

class Box
{
    public string Name; public int[] Data;
    public Box(string n) { Name = n; Data = new int[64]; for (int i = 0; i < 64; i++) Data[i] = n.Length; }
}

static class P
{
    // blocks of the same sizes are allocated and dropped, so a freed Box's memory would be taken again
    static string Churn(int k)
    {
        var l = new List<Box>();
        for (int i = 0; i < 50; i++) l.Add(new Box("churn" + (i + k)));
        return l[49].Name;
    }
    static int Use(Box a, Box b) { Churn(1); return a.Data[5] + a.Name.Length * 100 + b.Name.Length * 10000; }
    static int UseRef(Box a, ref Box r) { r = null; Churn(2); return a.Data[7] + a.Name.Length * 100; }
    static int UseOut(Box a, out Box r) { r = new Box("zz"); Churn(3); return a.Data[9] + a.Name.Length * 100; }
    static Box s_box;
    static int UseStatic(Box a) { s_box = null; Churn(4); return a.Data[1] + a.Name.Length * 100; }
    static int Run(Box a, Action clear) { clear(); Churn(5); return a.Data[2] + a.Name.Length * 100; }
    static int Two(Box a, int n) { Churn(6); return a.Data[3] + a.Name.Length * 100 + n; }

    class Holder { public Box B; public int Call(Box a) { B = null; Churn(7); return a.Data[4] + a.Name.Length * 100; } }
    struct Pair { public Box A, B; }
    static int UsePair(Box a, ref Pair p) { p.A = null; p = default; Churn(8); return a.Data[6] + a.Name.Length * 100; }

    static Box Next(ref int i) { i++; return i < 4 ? new Box(new string('n', 10 + i)) : null; }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        int n = int.Parse("3");
        Box x = new Box(new string('a', n + 8)), y = new Box(new string('b', n + 9));
        Console.WriteLine(Use(x, x = y));
        Box p = new Box(new string('c', n + 10));
        Console.WriteLine(UseRef(p, ref p));
        Console.WriteLine(p == null);
        Box q = new Box(new string('d', n + 11));
        Console.WriteLine(UseOut(q, out q));
        Console.WriteLine(q.Name);
        s_box = new Box(new string('e', n + 12));
        Console.WriteLine(UseStatic(s_box));
        Box c = new Box(new string('f', n + 13));
        Console.WriteLine(Run(c, () => c = null));
        Console.WriteLine(c == null);
        var arr = new Box[] { new Box(new string('g', n + 14)) };
        Console.WriteLine(Run(arr[0], () => arr[0] = null));
        var list = new List<Box> { new Box(new string('h', n + 15)) };
        Console.WriteLine(Run(list[0], () => list.Clear()));
        // a local function that writes the local
        Box lf = new Box(new string('i', n + 16));
        int Drop() { lf = null; return 1; }
        Console.WriteLine(Two(lf, Drop()));
        // the receiver's own field, cleared by the method called with it
        var h = new Holder { B = new Box(new string('j', n + 17)) };
        Console.WriteLine(h.Call(h.B));
        // a struct local passed by reference, one of whose fields is the other argument
        var pair = new Pair { A = new Box(new string('k', n + 18)) };
        Console.WriteLine(UsePair(pair.A, ref pair));
        // increments and assignments inside a condition
        int i = 0; Box cur; int sum = 0;
        while ((cur = Next(ref i)) != null) sum += Two(cur, i);
        Console.WriteLine(sum);
        // the receiver rewritten by its own argument
        Box r1 = new Box(new string('l', n + 19)), r2 = new Box("m");
        Console.WriteLine(r1.Name.Insert(0, (r1 = r2).Name).Length);
        // a conditional and a coalescing assignment inside a call
        Box m1 = new Box(new string('o', n + 20)), m2 = null;
        Console.WriteLine(Use(m1, m2 ??= (m1 = new Box("pp"))));
        Console.WriteLine(m1.Name + m2.Name);
    }
}
