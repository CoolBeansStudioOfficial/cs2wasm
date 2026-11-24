// Generic virtual methods: class overrides, interface implementations (explicit, generic owner, default method),
// a delegate of one, and the constrained call on a struct.
using System;
using System.Collections.Generic;
using System.Linq;

interface IVisitor { string Visit<T>(T value); }
interface IShape<TOwner> { string Name<T>(T x) => "default " + typeof(TOwner).Name + " " + typeof(T).Name + " " + x; }

class Base { public virtual string Describe<T>(T x) => "Base " + typeof(T).Name + " " + x; }
class Mid : Base { public override string Describe<T>(T x) => "Mid " + base.Describe(x); }
sealed class Leaf : Mid { public override string Describe<T>(T x) => "Leaf " + typeof(T).Name + "/" + x; }
class Printer : IVisitor { public string Visit<T>(T value) => "Printer " + value; }
class Explicit : IVisitor { string IVisitor.Visit<T>(T value) => "Explicit<" + typeof(T).Name + "> " + value; }
class Square : IShape<int> { }
class Circle : IShape<string> { public string Name<T>(T x) => "Circle " + x; }
struct Counter : IVisitor { public int N; public string Visit<T>(T value) { N++; return "Counter " + N + " " + value; } }

static class Program
{
    static string Call<V>(V v, int x) where V : IVisitor => v.Visit(x) + "|" + v.Visit("s");

    static void Main()
    {
        Base[] all = [new Base(), new Mid(), new Leaf()];
        foreach (var b in all) { Console.WriteLine(b.Describe(1)); Console.WriteLine(b.Describe("two")); Console.WriteLine(b.Describe(3.5)); }
        IVisitor[] vs = [new Printer(), new Explicit(), new Counter()];
        foreach (var v in vs) { Console.WriteLine(v.Visit(42)); Console.WriteLine(v.Visit<object>(null)); Console.WriteLine(v.Visit(new List<int> { 1 }.Count)); }
        Console.WriteLine(((IShape<int>)new Square()).Name('c'));
        Console.WriteLine(((IShape<string>)new Circle()).Name(7L));
        Func<int, string> f = all[2].Describe<int>;
        Console.WriteLine(f(9));
        var c = new Counter();
        Console.WriteLine(Call(c, 5));
        Console.WriteLine(Call(new Printer(), 6));
        // LINQ's iterators: Select over Where (a generic virtual on Enumerable's Iterator)
        var q = new[] { 1, 2, 3, 4, 5 }.Where(x => x % 2 == 1).Select(x => x * 10).Select(x => "n" + x);
        Console.WriteLine(string.Join(",", q));
        Console.WriteLine(string.Join(",", new List<string> { "a", "bb" }.Select(s => s.Length)));
    }
}
