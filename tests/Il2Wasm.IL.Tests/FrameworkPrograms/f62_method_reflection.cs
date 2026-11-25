// Method reflection: typeof(X).GetMethod(name) finds a public method the program names (static and instance); MethodInfo.Invoke
// calls it with object[] arguments, value-type arguments and results included; an exception the method throws comes out of
// Invoke wrapped in TargetInvocationException; a name the type does not have gives null; Delegate.CreateDelegate takes a
// MethodInfo and makes a delegate of the method.
using System;
using System.Reflection;

struct Pt
{
    public int X; public int Y;
    public Pt(int x, int y) { X = x; Y = y; }
}

class Calc
{
    public int Factor = 3;
    public static int Add(int a, int b) => a + b;
    public int Scale(int v) => v * Factor;
    public static double Half(double d) => d / 2;
    public static int Boom(int v) => throw new InvalidOperationException("boom " + v);
    public static Pt Mk(int x, int y) => new Pt(x, y);
    public static int Dot(Pt p, Pt q) => p.X * q.X + p.Y * q.Y;
}

static class Program
{
    static void Main()
    {
        var add = typeof(Calc).GetMethod("Add");
        Console.WriteLine("found: " + (add != null) + " " + add!.Name + " static " + add.IsStatic);
        Console.WriteLine("Add: " + add.Invoke(null, new object[] { 2, 40 }));
        var scale = typeof(Calc).GetMethod("Scale")!;
        Console.WriteLine("instance: " + scale.Invoke(new Calc { Factor = 5 }, new object[] { 7 }));
        Console.WriteLine("double: " + typeof(Calc).GetMethod("Half")!.Invoke(null, new object[] { 9.0 }));
        Pt made = (Pt)typeof(Calc).GetMethod("Mk")!.Invoke(null, new object[] { 1, 2 })!;
        Console.WriteLine("struct result: " + made.X + " " + made.Y);
        Console.WriteLine("struct args: " + typeof(Calc).GetMethod("Dot")!.Invoke(null, new object[] { new Pt(2, 3), new Pt(4, 5) }));
        try
        {
            typeof(Calc).GetMethod("Boom")!.Invoke(null, new object[] { 4 });
            Console.WriteLine("no exception");
        }
        catch (TargetInvocationException e)
        {
            Console.WriteLine("wrapped: " + e.InnerException!.GetType().Name + " " + e.InnerException.Message);
        }
        Console.WriteLine("missing: " + (typeof(Calc).GetMethod("Nope") == null));
        var f = (Func<int, int, int>)Delegate.CreateDelegate(typeof(Func<int, int, int>), add);
        Console.WriteLine("delegate: " + f(20, 22));
    }
}
