// A derived class's finalizer runs its base class's afterwards, as the C# compiler writes it for .NET
// (`~Derived` then `~Base`, through a class between them that has none, through a generic base, when the derived
// finalizer returns early, and for a member of a cycle). Each object's finalizers write to its own log, so the
// order between objects (which .NET does not promise) does not show.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Log { public string Text = ""; }

class Base
{
    public static int Count;
    public Log Log;
    public Base(Log log) { Log = log; }
    ~Base() { Log.Text += " ~Base"; Count++; }
}

class Middle : Base { public Middle(Log log) : base(log) { } }

class Derived : Middle
{
    public bool Early;
    public Derived(Log log) : base(log) { }
    ~Derived() { if (Early) { Log.Text += " ~Derived(early)"; return; } Log.Text += " ~Derived"; }
}

sealed class Most : Derived
{
    public Most(Log log) : base(log) { }
    ~Most() { Log.Text += " ~Most"; }
}

class GenericBase<T>
{
    public Log Log; public T Value;
    public GenericBase(Log log, T value) { Log = log; Value = value; }
    ~GenericBase() { Log.Text += " ~GenericBase<" + typeof(T).Name + ">(" + Value + ")"; }
}

sealed class OfInt : GenericBase<int>
{
    public OfInt(Log log) : base(log, 7) { }
    ~OfInt() { Log.Text += " ~OfInt"; }
}

class Node
{
    public Node? Next; public Log Log;
    public Node(Log log) { Log = log; }
    ~Node() { Log.Text += " ~Node"; }
}

sealed class Leaf : Node
{
    public Leaf(Log log) : base(log) { }
    ~Leaf() { Log.Text += " ~Leaf"; }
}

static class Program
{
    static void Collect() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Make(Log[] logs)
    {
        new Base(logs[0]);
        new Middle(logs[1]);
        new Derived(logs[2]);
        new Derived(logs[3]) { Early = true };
        new Most(logs[4]);
        new OfInt(logs[5]);
        object o = new Most(logs[6]);
        Base b = new Derived(logs[7]);
        var list = new List<Base> { new Most(logs[8]), new Middle(logs[9]) };
        // (a cycle: two nodes that refer to each other, and one that refers to itself)
        var x = new Leaf(logs[10]); var y = new Node(logs[11]); x.Next = y; y.Next = x;
        var z = new Leaf(logs[12]); z.Next = z;
        GC.KeepAlive(o); GC.KeepAlive(b); GC.KeepAlive(list);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        var logs = new Log[13];
        for (int i = 0; i < logs.Length; i++) logs[i] = new Log();
        Make(logs);
        Collect();
        for (int i = 0; i < logs.Length; i++) Console.WriteLine(i + ":" + logs[i].Text);
        Console.WriteLine("base finalizers " + Base.Count);
    }
}
