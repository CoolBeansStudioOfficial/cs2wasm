using System;
using System.Linq.Expressions;
using System.Runtime.InteropServices;

sealed class Point
{
    public int X, Y;
    public Point(int x, int y) { X = x; Y = y; }
    public int Sum => X + Y;
    public int Scale(int k) => (X + Y) * k;
    public static int Twice(int v) => v * 2;
    public override string ToString() => "(" + X + "," + Y + ")";
}

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // built by hand
        var x = Expression.Parameter(typeof(int), "x");
        var y = Expression.Parameter(typeof(int), "y");
        var body = Expression.Condition(Expression.GreaterThan(x, y), Expression.Subtract(x, y), Expression.Multiply(Expression.Add(x, Expression.Constant(1)), y));
        var f = Expression.Lambda<Func<int, int, int>>(body, x, y);
        Console.WriteLine(f);
        var fc = f.Compile();
        Console.WriteLine(fc(7, 3) + " " + fc(2, 5));

        // from C# lambdas
        Expression<Func<int, int>> sq = n => n * n + 1;
        Console.WriteLine(sq + " -> " + sq.Compile()(9));
        Expression<Func<string, int>> len = s => s.Length * 2;
        Console.WriteLine(len + " -> " + len.Compile()("hello"));
        Expression<Func<Point, int>> sum = p => p.Sum + p.X - p.Y;
        Console.WriteLine(sum + " -> " + sum.Compile()(new Point(4, 1)));
        Expression<Func<Point, int, int>> call = (p, k) => p.Scale(k) + Point.Twice(k);
        Console.WriteLine(call + " -> " + call.Compile()(new Point(1, 2), 10));
        Expression<Func<int, int, Point>> make = (a, b) => new Point(a, b);
        Console.WriteLine(make + " -> " + make.Compile()(3, 4));
        Expression<Func<int[], int, int>> idx = (arr, i) => arr[i] + arr.Length;
        Console.WriteLine(idx + " -> " + idx.Compile()(new[] { 10, 20, 30 }, 1));
        Expression<Func<bool, bool, bool>> logic = (a, b) => !a && b || a;
        Console.WriteLine(logic + " -> " + logic.Compile()(false, true) + " " + logic.Compile()(false, false));
        Expression<Func<double, long>> conv = d => (long)d + 1;
        Console.WriteLine(conv + " -> " + conv.Compile()(2.9));
        Expression<Func<int, string>> str = v => "v=" + v;
        Console.WriteLine(str + " -> " + str.Compile()(5));
        Expression<Func<int, Func<int, int>>> curry = a => b => a + b;
        Console.WriteLine(curry + " -> " + curry.Compile()(2)(40));
        Expression<Func<int, int>> neg = v => -v ^ 3;
        Console.WriteLine(neg + " -> " + neg.Compile()(4));

        // the tree's shape
        var be = (BinaryExpression)sq.Body;
        Console.WriteLine(be.NodeType + " " + be.Left.NodeType + " " + be.Right + " " + sq.Parameters[0].Type.Name + " " + sq.ReturnType.Name);
        Expression<Action<int>> act = v => Console.WriteLine(v);
        Console.WriteLine(act);
        act.Compile()(77);
    }
}
