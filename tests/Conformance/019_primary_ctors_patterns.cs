using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

class Player(string name, int hp)
{
    public string Name => name;
    public int Health { get; private set; } = hp;
    public void Hit(int damage) { hp -= damage; Health = Math.Max(0, Health - damage); }
    public string Describe() => $"{name}: {Health} (raw {hp})";
    public Func<string> Greeter() => () => "hi " + name;
}

class Boss(string name) : Player(name, 500)
{
    public string Title => "Boss " + Name;
}

struct Range(int lo, int hi)
{
    public int Width => hi - lo;
    public bool Contains(int x) => x >= lo && x < hi;
}

class Pt
{
    public int X, Y;
    public Pt(int x, int y) { X = x; Y = y; }
    public void Deconstruct(out int x, out int y) { x = X; y = Y; }
}

static class Program
{
    static string Quadrant(Pt p) => p switch
    {
        (0, 0) => "origin",
        ( > 0, > 0) => "first",
        ( < 0, var y) when y > 0 => "second",
        (_, < 0) => "below",
        _ => "axis",
    };

    static string Shape(int[] a) => a switch
    {
        [] => "empty",
        [var only] => "one " + only,
        [1, .., 9] => "1 to 9",
        [var first, .. var mid, var last] => $"first {first} last {last} middle {mid.Length}",
    };

    static string Tup((int, string) t) => t switch
    {
        (0, _) => "zero",
        (var n, "x") => "x" + n,
        var (n, s) => s + n,
    };

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var p = new Player("Ann", 100);
        p.Hit(30);
        Console.WriteLine(p.Describe() + " " + p.Name + " " + p.Greeter()());
        var b = new Boss("Zed");
        b.Hit(5);
        Console.WriteLine(b.Title + " " + b.Health + " " + b.Describe());
        var r = new Range(3, 10);
        Console.WriteLine(r.Width + " " + r.Contains(5) + " " + r.Contains(10));
        foreach (var pt in new[] { new Pt(0, 0), new Pt(2, 3), new Pt(-1, 4), new Pt(5, -2), new Pt(0, 7) }) Console.Write(Quadrant(pt) + "; ");
        Console.WriteLine();
        foreach (var a in new[] { new int[0], new[] { 4 }, new[] { 1, 5, 9 }, new[] { 2, 3, 4, 5 } }) Console.Write(Shape(a) + "; ");
        Console.WriteLine();
        Console.WriteLine(Tup((0, "q")) + " " + Tup((3, "x")) + " " + Tup((4, "y")));
        var list = new List<int> { 1, 2, 3 };
        if (list is [1, .. var rest]) Console.WriteLine("starts with 1, then " + rest.Count);
        object o = new Pt(1, 1);
        if (o is Pt(1, var yy)) Console.WriteLine("pt y " + yy);
        var words = new[] { "a", "b" };
        Console.WriteLine(words is ["a", _] ? "two starting with a" : "no");
    }
}
