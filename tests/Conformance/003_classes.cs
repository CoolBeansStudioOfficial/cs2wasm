using System;
using System.Runtime.InteropServices;

abstract class Shape
{
    public string Name { get; }
    protected Shape(string name) { Name = name; }
    public abstract double Area();
    public virtual string Describe() => Name + " with area " + Area().ToString("F2");
    public override string ToString() => "Shape(" + Name + ")";
}

class Circle : Shape
{
    readonly double _r;
    public Circle(double r) : base("circle") { _r = r; }
    public override double Area() => Math.PI * _r * _r;
}

class Rect : Shape
{
    public double W { get; set; }
    public double H { get; set; }
    public Rect(double w, double h) : base("rect") { W = w; H = h; }
    public override double Area() => W * H;
    public override string Describe() => "Rectangle " + W + "x" + H + ": " + base.Describe();
}

sealed class Square : Rect
{
    public Square(double s) : base(s, s) { }
    public override string ToString() => "Square!";
}

interface IGreeter { string Greet(string who); string Twice(string who) => Greet(who) + Greet(who); }
class Hello : IGreeter { public string Greet(string who) => "Hello " + who + ". "; }
class Hi : IGreeter { public string Greet(string who) => "Hi " + who + ". "; public string Twice(string who) => "custom"; }

class Counter
{
    public static int Instances;
    public int Id;
    static Counter() { Console.WriteLine("Counter static ctor"); }
    public Counter() { Id = ++Instances; }
}

class Node
{
    public int Value;
    public Node Next;
    public Node(int v, Node next = null) { Value = v; Next = next; }
}

static class Program
{
    static int Sum(Node n) { int s = 0; while (n != null) { s += n.Value; n = n.Next; } return s; }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Shape[] shapes = { new Circle(1.5), new Rect(2, 3), new Square(4) };
        foreach (var s in shapes) Console.WriteLine(s.Describe());
        foreach (var s in shapes) Console.WriteLine(s.ToString());
        object o = shapes[2];
        Console.WriteLine(o is Rect); Console.WriteLine(o is Circle); Console.WriteLine((o as Shape)?.Name);
        IGreeter[] gs = { new Hello(), new Hi() };
        foreach (var g in gs) Console.WriteLine(g.Twice("Bob"));
        Console.WriteLine("before counter");
        var c1 = new Counter(); var c2 = new Counter();
        Console.WriteLine(c1.Id + " " + c2.Id + " " + Counter.Instances);
        Node list = null;
        for (int i = 1; i <= 10; i++) list = new Node(i, list);
        Console.WriteLine(Sum(list));
        list = list.Next.Next;
        Console.WriteLine(Sum(list));
        var r = new Rect(1, 1) { W = 5, H = 6 };
        Console.WriteLine(r.Area());
        r.W *= 2; r.H++;
        Console.WriteLine(r.Area());
        try { Shape bad = (Shape)(object)"nope"; Console.WriteLine(bad); } catch (InvalidCastException e) { Console.WriteLine(e.Message); }
        try { Node n = null; Console.WriteLine(n.Value); } catch (NullReferenceException e) { Console.WriteLine(e.Message); }
        Console.WriteLine(new object().ToString());
    }
}
