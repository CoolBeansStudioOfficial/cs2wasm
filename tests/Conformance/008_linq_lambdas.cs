using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

class Person
{
    public string Name;
    public int Age;
    public Person(string n, int a) { Name = n; Age = a; }
}

static class Program
{
    static Func<int, int> MakeAdder(int n) => x => x + n;

    static Action MakeCounter(string label)
    {
        int count = 0;
        return () => { count++; Console.WriteLine(label + " " + count); };
    }


    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var nums = Enumerable.Range(1, 10).ToList();
        Console.WriteLine(string.Join(",", nums.Where(n => n % 2 == 0).Select(n => n * n)));
        Console.WriteLine(nums.Sum() + " " + nums.Count(n => n > 3) + " " + nums.Max() + " " + nums.Min() + " " + nums.Average());
        Console.WriteLine(nums.Any(n => n > 9) + " " + nums.All(n => n > 0) + " " + nums.First(n => n > 4) + " " + nums.FirstOrDefault(n => n > 40));
        var people = new List<Person> { new("Ann", 31), new("Bob", 25), new("Cid", 31), new("Dee", 19) };
        foreach (var p in people.OrderBy(p => p.Age).ThenBy(p => p.Name)) Console.Write(p.Name + " ");
        Console.WriteLine();
        foreach (var p in people.OrderByDescending(p => p.Age)) Console.Write(p.Name + " ");
        Console.WriteLine();
        foreach (var g in people.GroupBy(p => p.Age)) Console.WriteLine(g.Key + ": " + string.Join("/", g.Select(p => p.Name)));
        var names = people.Select(p => p.Name).Reverse().Take(3).Skip(1).ToArray();
        Console.WriteLine(string.Join(" ", names));
        Console.WriteLine(people.Aggregate(0, (acc, p) => acc + p.Age));
        Console.WriteLine(string.Join(",", new[] { 3, 1, 3, 2, 1 }.Distinct()));
        var dict = people.ToDictionary(p => p.Name, p => p.Age);
        Console.WriteLine(dict["Cid"]);
        // deferred execution
        int calls = 0;
        var q = nums.Where(n => { calls++; return n < 4; });
        Console.WriteLine("calls before: " + calls);
        Console.WriteLine(q.Count() + " calls after: " + calls);
        nums.Add(0);
        Console.WriteLine(q.Count());
        var add5 = MakeAdder(5);
        Console.WriteLine(add5(10));
        var c1 = MakeCounter("a"); var c2 = MakeCounter("b");
        c1(); c1(); c2(); c1();
        var actions = new List<Action>();
        for (int i = 0; i < 3; i++) actions.Add(() => Console.Write(i + " "));
        foreach (var a in actions) a();
        Console.WriteLine();
        actions.Clear();
        foreach (var w in new[] { "x", "y", "z" }) actions.Add(() => Console.Write(w));
        foreach (var a in actions) a();
        Console.WriteLine();
        Func<int, int, int> mul = (a, b) => a * b;
        Console.WriteLine(mul(6, 7));
        Predicate<string> isEmpty = string.IsNullOrEmpty;
        Console.WriteLine(isEmpty("") + " " + isEmpty("a"));
        Action multi = () => Console.Write("one ");
        multi += () => Console.Write("two ");
        multi += () => Console.Write("three ");
        multi();
        Console.WriteLine();
        int Square(int v) => v * v;
        int total = 0;
        void AddTo(int v) { total += v; }
        foreach (var n in nums) AddTo(Square(n));
        Console.WriteLine(total);
        var zipped = nums.Take(3).Zip(new[] { "a", "b", "c" }, (n, s) => s + n);
        Console.WriteLine(string.Join(" ", zipped));
        Console.WriteLine(string.Join(",", Enumerable.Repeat("ab", 3)));
        Console.WriteLine(nums.Select((n, i) => n * i).Sum());
        var flat = new[] { new[] { 1, 2 }, new[] { 3 } }.SelectMany(a => a).ToList();
        Console.WriteLine(flat.Count + " " + flat.Last());
        try { new List<int>().First(); } catch (InvalidOperationException e) { Console.WriteLine(e.Message); }
        Comparison<Person> byName = (x, y) => string.Compare(x.Name, y.Name);
        people.Sort(byName);
        Console.WriteLine(people[0].Name + people[3].Name);
    }
}
