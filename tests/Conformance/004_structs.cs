using System;
using System.Runtime.InteropServices;

struct Vec2
{
    public float X, Y;
    public Vec2(float x, float y) { X = x; Y = y; }
    public float Length => MathF.Sqrt(X * X + Y * Y);
    public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
    public static bool operator ==(Vec2 a, Vec2 b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Vec2 a, Vec2 b) => !(a == b);
    public override bool Equals(object o) => o is Vec2 v && v == this;
    public override int GetHashCode() => X.GetHashCode() ^ Y.GetHashCode();
    public void Scale(float s) { X *= s; Y *= s; }
    public override string ToString() => "(" + X + ", " + Y + ")";
}

struct Holder
{
    public string Label;
    public Vec2 Pos;
    public Holder(string l, Vec2 p) { Label = l; Pos = p; }
}

enum Color { Red, Green = 5, Blue }
[Flags] enum Perm { None = 0, Read = 1, Write = 2, Exec = 4 }

static class Program
{
    static Vec2 Mid(Vec2 a, Vec2 b) => (a + b) * 0.5f;
    static void Bump(ref Vec2 v) { v.X += 1; }
    static void Make(out Vec2 v) { v = new Vec2(9, 9); }
    static int Twice(in int x) => x * 2;

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var a = new Vec2(3, 4);
        Console.WriteLine(a.Length);
        var b = a;
        b.X = 10;
        Console.WriteLine(a + " " + b);
        Console.WriteLine(Mid(a, b));
        a.Scale(2);
        Console.WriteLine(a);
        Bump(ref a); Console.WriteLine(a);
        Make(out var m); Console.WriteLine(m);
        Console.WriteLine(a == b); Console.WriteLine(a != b); Console.WriteLine(a.Equals((object)new Vec2(7, 8)));
        var arr = new Vec2[3];
        arr[1] = new Vec2(1, 2);
        arr[1].Scale(3);
        Console.WriteLine(arr[0] + " " + arr[1]);
        var h = new Holder("pt", new Vec2(1, 1));
        var h2 = h;
        h2.Label = "copy"; h2.Pos.X = 5;
        Console.WriteLine(h.Label + h.Pos + " " + h2.Label + h2.Pos);
        object boxed = a;
        a.X = 0;
        Console.WriteLine(boxed);
        Vec2 unboxed = (Vec2)boxed;
        Console.WriteLine(unboxed.X);
        Console.WriteLine(Color.Green); Console.WriteLine((int)Color.Blue); Console.WriteLine(Color.Red + 1);
        Console.WriteLine(Perm.Read | Perm.Write); Console.WriteLine((Perm)7); Console.WriteLine((Perm)8);
        Color c = Color.Blue;
        switch (c) { case Color.Red: Console.WriteLine("r"); break; case Color.Blue: Console.WriteLine("b"); break; default: Console.WriteLine("?"); break; }
        int n = 21; Console.WriteLine(Twice(n));
        object oi = 42; Console.WriteLine(oi); Console.WriteLine((int)oi + 1); Console.WriteLine(oi is int); Console.WriteLine(oi is long);
        Console.WriteLine(oi.Equals(42)); Console.WriteLine(3.Equals(3)); Console.WriteLine(5.CompareTo(7));
        Console.WriteLine(((object)Color.Green).ToString());
        Console.WriteLine(true.ToString() + 'x');
    }
}
