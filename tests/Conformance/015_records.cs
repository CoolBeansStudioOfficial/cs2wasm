using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

record Person(string Name, int Age);
record Employee(string Name, int Age, string Role) : Person(Name, Age);
record struct Point(int X, int Y)
{
    public int Sum => X + Y;
}
record Settings
{
    public string Theme { get; init; } = "dark";
    public int Volume { get; init; } = 7;
}

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var a = new Person("Ann", 30);
        var b = new Person("Ann", 30);
        var c = a with { Age = 31 };
        Console.WriteLine(a);
        Console.WriteLine(c);
        Console.WriteLine(a == b); Console.WriteLine(a != c); Console.WriteLine(a.Equals(b)); Console.WriteLine(ReferenceEquals(a, b));
        Console.WriteLine(a.GetHashCode() == b.GetHashCode());
        var (name, age) = c;
        Console.WriteLine(name + " " + age);
        var e = new Employee("Bob", 40, "dev");
        Console.WriteLine(e);
        Console.WriteLine(e == new Employee("Bob", 40, "dev"));
        Person asPerson = e;
        Console.WriteLine(asPerson == new Person("Bob", 40));
        Console.WriteLine((e with { Role = "lead" }).Role);
        var p = new Point(2, 3);
        var q = p with { Y = 10 };
        Console.WriteLine(p + " " + q + " " + q.Sum);
        Console.WriteLine(p == new Point(2, 3)); Console.WriteLine(p.Equals(q));
        var s = new Settings();
        var s2 = s with { Volume = 3 };
        Console.WriteLine(s + " " + s2);
        var set = new HashSet<Person> { a, b, c };
        Console.WriteLine(set.Count);
        var dict = new Dictionary<Point, string> { [new Point(1, 1)] = "one" };
        Console.WriteLine(dict[new Point(1, 1)]);
    }
}
