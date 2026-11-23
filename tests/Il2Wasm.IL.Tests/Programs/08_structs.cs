// Structs: values copied on assignment, passed and returned by value, mutated through refs, in arrays, fields and
// boxes, with references inside them (counted), and generic structs.
using System;

struct Vec
{
    public int X, Y;
    public Vec(int x, int y) { X = x; Y = y; }
    public Vec Add(Vec o) => new Vec(X + o.X, Y + o.Y);
    public int Dot(Vec o) => X * o.X + Y * o.Y;
    public void Scale(int k) { X *= k; Y *= k; }
    public override string ToString() => "<" + X + "," + Y + ">";
}

struct Named
{
    public string Name;
    public Vec At;
    public Named(string n, Vec at) { Name = n; At = at; }
}

struct Holder<T>
{
    public T Value;
    public int Version;
    public void Set(T v) { Value = v; Version++; }
}

class Body
{
    public Vec Position;
    public Named Tag;
}

static class Program
{
    static Vec Twice(Vec v) { v.Scale(2); return v; }
    static void Bump(ref Vec v) { v.X += 100; }
    static Named Rename(Named n, string s) { n.Name = s; return n; }

    static void Main()
    {
        var a = new Vec(1, 2);
        var b = a;
        b.X = 10;
        Console.WriteLine(a.X + " " + b.X);
        var c = a.Add(b);
        Console.WriteLine(c.ToString());
        Console.WriteLine(a.Dot(b));
        var d = Twice(c);
        Console.WriteLine(c + " " + d);
        Bump(ref d);
        Console.WriteLine(d.X);
        var arr = new Vec[3];
        arr[1] = new Vec(5, 6);
        arr[1].Scale(3);
        Console.WriteLine(arr[1] + " " + arr[0]);
        var body = new Body();
        body.Position = new Vec(7, 8);
        body.Position.Scale(2);
        Console.WriteLine(body.Position.Y);
        var n = new Named("first", a);
        var m = Rename(n, "second");
        Console.WriteLine(n.Name + " " + m.Name + " " + m.At.Y);
        body.Tag = m;
        body.Tag.Name = "third";
        Console.WriteLine(m.Name + " " + body.Tag.Name);
        var names = new Named[2];
        names[0] = new Named("in array", new Vec(3, 3));
        names[1] = names[0];
        names[0].Name = "changed";
        Console.WriteLine(names[0].Name + " / " + names[1].Name);
        object boxed = a;
        a.X = 99;
        Console.WriteLine(((Vec)boxed).X + " " + a.X);
        Console.WriteLine(boxed.ToString());
        object boxedNamed = n;
        Console.WriteLine(((Named)boxedNamed).Name);
        var h = new Holder<string>();
        h.Set("one"); h.Set("two");
        Console.WriteLine(h.Value + " " + h.Version);
        var hv = new Holder<Vec>();
        hv.Set(new Vec(4, 5));
        Console.WriteLine(hv.Value.Y + hv.Version);
        Vec sum = default;
        for (int i = 0; i < 4; i++) sum = sum.Add(new Vec(i, i * i));
        Console.WriteLine(sum);
    }
}
