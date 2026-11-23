// Threads (section 4.5): what a thread's lambda captures is handed to that thread. A captured local holds what the
// writes of it in the declaring body store, each of them: here a local of type object and one of a base class, both
// assigned twice (the second time inside a lambda), read by workers while the main thread counts the same objects.
// IL2WASM_EXP_DISPLAY_FIRST_ONLY=1 takes only the first write of each: with IL2WASM_SHARE_CHECK=1 the object the
// second write stored stops the program when a worker counts it (tools/thread-escape-breaks.sh).
using System;
using System.Runtime.InteropServices;
using System.Threading;

class Shape
{
    public static int Live;
    public int Sides;
    public Shape? Next;
    public Shape(int sides) { Sides = sides; Interlocked.Increment(ref Live); }
    ~Shape() { Interlocked.Decrement(ref Live); }
}
sealed class Square : Shape { public Square() : base(4) { } }
sealed class Triangle : Shape { public Triangle() : base(3) { } }
sealed class Token { public int Id; public Token(int id) { Id = id; } }
sealed class Marker { public string Name; public Marker(string n) { Name = n; } }

static class Program
{
    static long Run()
    {
        object gate = new Token(1);
        Shape shape = new Square();
        Action swap = () => { gate = new Marker("m"); shape = new Triangle { Next = shape }; };
        swap();
        long[] sums = new long[4];
        var threads = new Thread[4];
        for (int t = 0; t < threads.Length; t++)
        {
            int k = t;
            threads[t] = new Thread(() =>
            {
                long s = 0;
                for (int i = 0; i < 20000; i++)
                {
                    var sh = shape;
                    var nx = sh.Next;
                    object g = gate;
                    s += sh.Sides + (nx != null ? nx.Sides : 0) + (g is Marker m ? m.Name.Length : ((Token)g).Id * 100);
                }
                sums[k] = s;
            });
            threads[t].Start();
        }
        long mine = 0;
        for (int i = 0; i < 20000; i++)
        {
            var sh = shape;
            object g = gate;
            mine += sh.Sides + (g is Marker ? 1 : 0);
        }
        foreach (var th in threads) th.Join();
        return mine + sums[0] + sums[1] + sums[2] + sums[3];
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Run());
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(Shape.Live);
    }
}
