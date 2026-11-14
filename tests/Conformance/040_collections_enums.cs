using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

[Flags] enum Perm { None = 0, Read = 1, Write = 2, Exec = 4, All = Read | Write | Exec }
enum Color : byte { Red = 1, Green, Blue }

readonly record struct Cell(int X, int Y);

class Employee : IComparable<Employee>
{
    public string Name { get; init; } = "";
    public string Dept { get; init; } = "";
    public int Salary { get; init; }
    public int CompareTo(Employee other) => Salary.CompareTo(other.Salary);
    public override string ToString() => Name;
}

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var p = Perm.Read | Perm.Exec;
        Console.WriteLine($"{p} {p.HasFlag(Perm.Read)} {(p & Perm.Write) == 0} {Perm.All} {(Perm)6} {(Perm)8}");
        Console.WriteLine(string.Join(",", Enum.GetValues<Color>()) + " " + string.Join(",", Enum.GetNames(typeof(Perm))) + " " + Enum.Parse<Color>("Blue") + " " + (int)Color.Green + " " + Enum.IsDefined(typeof(Color), (byte)7));
        Console.WriteLine(Enum.TryParse<Perm>("Write, Read", out var parsed) + " " + parsed + " " + Color.Blue.CompareTo(Color.Red) + " " + Color.Red.ToString("D"));

        var pq = new PriorityQueue<string, int>();
        pq.Enqueue("low", 5); pq.Enqueue("high", 1); pq.Enqueue("mid", 3); pq.Enqueue("high2", 1);
        var order = new List<string>();
        while (pq.TryDequeue(out var item, out var prio)) order.Add(item + prio);
        Console.WriteLine(string.Join(" ", order));
        var ll = new LinkedList<int>(new[] { 1, 2, 3 });
        ll.AddFirst(0); ll.AddLast(4); ll.Remove(2); ll.AddAfter(ll.Find(3)!, 35);
        Console.WriteLine(string.Join(",", ll) + " " + ll.First!.Value + " " + ll.Last!.Previous!.Value + " " + ll.Count);
        var visited = new HashSet<Cell> { new(0, 0), new(1, 0) };
        Console.WriteLine(visited.Add(new Cell(0, 0)) + " " + visited.Contains(new Cell(1, 0)) + " " + visited.Count);
        var a = new HashSet<int> { 1, 2, 3, 4 }; var b = new HashSet<int> { 3, 4, 5 };
        a.IntersectWith(b); Console.WriteLine(string.Join(",", a) + " " + a.IsSubsetOf(b) + " " + b.Overlaps(new[] { 9, 5 }));
        var stack = new Stack<char>("abc"); var queue = new Queue<int>(Enumerable.Range(1, 3));
        Console.WriteLine(new string(stack.ToArray()) + " " + stack.Peek() + " " + queue.Dequeue() + queue.Peek() + " " + queue.Count);

        var staff = new List<Employee>
        {
            new() { Name = "Ann", Dept = "Eng", Salary = 120 }, new() { Name = "Bob", Dept = "Eng", Salary = 100 },
            new() { Name = "Cid", Dept = "Ops", Salary = 90 }, new() { Name = "Dee", Dept = "Ops", Salary = 95 },
            new() { Name = "Eve", Dept = "Art", Salary = 80 },
        };
        staff.Sort();
        Console.WriteLine(string.Join(",", staff));
        var depts = new[] { ("Eng", "Building 1"), ("Ops", "Building 2"), ("HR", "Building 3") };
        var joined = staff.Join(depts, e => e.Dept, d => d.Item1, (e, d) => e.Name + "@" + d.Item2);
        Console.WriteLine(string.Join(" ", joined));
        var gj = depts.GroupJoin(staff, d => d.Item1, e => e.Dept, (d, es) => d.Item1 + ":" + es.Count());
        Console.WriteLine(string.Join(" ", gj));
        var lookup = staff.ToLookup(e => e.Dept);
        Console.WriteLine(lookup["Ops"].Count() + " " + lookup["Nope"].Count() + " " + string.Join(",", lookup.Select(g => g.Key)));
        Console.WriteLine(string.Join(",", staff.SelectMany(e => e.Name.Take(2)).Distinct()));
        Console.WriteLine(string.Join(" ", staff.Zip(Enumerable.Range(1, 10), (e, i) => i + e.Name)));
        Console.WriteLine(string.Join(" ", staff.OrderByDescending(e => e.Dept).ThenBy(e => e.Name).Select(e => e.Dept[0] + e.Name)));
        Console.WriteLine(staff.Any(e => e.Salary > 110) + " " + staff.All(e => e.Salary > 50) + " " + staff.Count(e => e.Dept == "Eng") + " " + staff.FirstOrDefault(e => e.Dept == "HR")?.Name ?? "none");
        var byDept = staff.GroupBy(e => e.Dept).Select(g => new { Dept = g.Key, Total = g.Sum(e => e.Salary), Top = g.MaxBy(e => e.Salary)!.Name });
        foreach (var x in byDept) Console.WriteLine(x);
        Console.WriteLine(string.Join(",", Enumerable.Range(1, 10).Where(i => i % 2 == 1).Reverse().SkipWhile(i => i > 5).TakeWhile(i => i > 1)));
        int[] arr = { 5, 3, 8, 1 };
        Array.Sort(arr); Console.WriteLine(string.Join(",", arr) + " " + Array.BinarySearch(arr, 5) + " " + Array.IndexOf(arr, 8));
        var dict = staff.ToDictionary(e => e.Name, e => e.Salary);
        Console.WriteLine(string.Join(",", dict.Keys) + " " + dict.Values.Sum() + " " + dict.GetValueOrDefault("Zed", -1) + " " + dict.ContainsValue(80));
    }
}
