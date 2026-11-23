// Objects: fields, constructors, virtual and interface calls, casts, statics and a static constructor.
using System;

interface IShape { int Area(); string Name { get; } }

abstract class Shape : IShape
{
    public static int Made;
    protected Shape() { Made++; }
    public abstract int Area();
    public virtual string Name => "shape";
    public override string ToString() => Name + ":" + Area();
}

sealed class Rect : Shape
{
    readonly int _w, _h;
    public Rect(int w, int h) { _w = w; _h = h; }
    public override int Area() => _w * _h;
    public override string Name => "rect";
}

class Square : Shape
{
    protected readonly int Side;
    public Square(int s) { Side = s; }
    public override int Area() => Side * Side;
}

class Cube : Square
{
    public Cube(int s) : base(s) { }
    public override int Area() => 6 * base.Area();
    public override string Name => "cube";
}

class Node
{
    public int Value;
    public Node Next;
    public Node(int v, Node next) { Value = v; Next = next; }
}

static class Config
{
    public static readonly int Limit;
    public static string Label = "cfg";
    static Config() { Limit = 3; Console.WriteLine("Config initialised"); }
}

static class Program
{
    static int Total(IShape[] shapes)
    {
        int t = 0;
        foreach (var s in shapes) t += s.Area();
        return t;
    }

    static void Main()
    {
        var shapes = new IShape[] { new Rect(2, 3), new Square(4), new Cube(2) };
        Console.WriteLine(Total(shapes));
        foreach (var s in shapes) Console.WriteLine(s.Name);
        foreach (var s in shapes) Console.WriteLine(s.ToString());
        Console.WriteLine(Shape.Made);
        object o = shapes[2];
        Console.WriteLine(o is Square);
        Console.WriteLine(o is Rect);
        var sq = o as Square;
        Console.WriteLine(sq != null);
        Console.WriteLine(((Shape)o).Area());
        Node list = null;
        for (int i = 0; i < 5; i++) list = new Node(i, list);
        int sum = 0;
        for (var n = list; n != null; n = n.Next) sum += n.Value;
        Console.WriteLine(sum);
        list.Next = list.Next.Next;
        Console.WriteLine(list.Next.Value);
        Console.WriteLine("before config");
        Console.WriteLine(Config.Limit);
        Console.WriteLine(Config.Label);
        object boxed = 41;
        Console.WriteLine((int)boxed + 1);
        Console.WriteLine(boxed is int);
    }
}
