// Local functions that are only called (never a delegate, never async or an iterator) keep their captured variables
// in the frame of the function that declares them, as Roslyn's own compiler puts them in a struct: no allocation. What
// those variables hold is still released at every way out (a return, a loop's next iteration, a break, an
// exception), and freed objects are counted by their finalizers.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

class Node
{
    public static int Freed;
    public string Name;
    public Node(string n) { Name = n; }
    ~Node() { Freed++; }
}

static class P
{
    static int Count(List<string> xs, string prefix)
    {
        var keep = new Node(prefix);
        int n = 0;
        foreach (var x in xs)
        {
            var tag = new Node(x);
            if (x == "stop") break;
            if (Match(x)) n++;
            bool Match(string s) => s.StartsWith(keep.Name) && tag.Name.Length > 0;
        }
        return n + Twice();
        int Twice() { return keep.Name.Length * 2; }
    }

    static int Throws(string s)
    {
        var holder = new Node("h" + s);
        try { return Inner(); } catch (InvalidOperationException) { return -1; }
        int Inner() { if (holder.Name.Length > 2) throw new InvalidOperationException(); return holder.Name.Length; }
    }

    static int Nested(int k)
    {
        var a = new Node("a");
        return Outer(k);
        int Outer(int i) { var b = new Node("b" + i); return i <= 0 ? a.Name.Length : Inner2(i) + Outer(i - 1); int Inner2(int j) => b.Name.Length + j; }
    }

    static string Mutate()
    {
        var acc = new Node("");
        Add("x"); Add("y"); acc = new Node(acc.Name + "!");
        return acc.Name;
        void Add(string t) { acc = new Node(acc.Name + t); }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Count(new List<string> { "ab", "ac", "b", "stop", "ad" }, "a"));
        Console.WriteLine(Throws("x"));
        Console.WriteLine(Throws("xyz"));
        Console.WriteLine(Nested(3));
        Console.WriteLine(Mutate());
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine("freed " + Node.Freed);
    }
}
