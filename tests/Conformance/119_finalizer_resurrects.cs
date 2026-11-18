// A finalizer that stores `this` where the program can reach it: the object lives on, with its fields, and is not
// finalized a second time unless it asks to be (GC.ReRegisterForFinalize).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

class Res
{
    public static Res Kept;
    public static int Finalized;
    public string Tag = new string('r', int.Parse("5"));
    public List<string> Items = new() { "one", "two" };
    ~Res() { Finalized++; Kept = this; }
}

class Again
{
    public static Again Kept;
    public static int Finalized;
    public int Round;
    ~Again()
    {
        Finalized++;
        if (Round++ == 0) { Kept = this; GC.ReRegisterForFinalize(this); }
    }
}

class Node
{
    public static Node Kept;
    public Node Other; public string Name;
    ~Node() { if (Name == "a") Kept = this; }
}

static class P
{
    static void Make() { var r = new Res(); r.Tag += "!"; }
    static void MakeAgain() { new Again(); }
    static void MakeCycle() { var a = new Node { Name = "a" }; var b = new Node { Name = "b" }; a.Other = b; b.Other = a; }
    static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    static void Churn() { var junk = new List<string>(); for (int i = 0; i < 200; i++) junk.Add(new string('j', 6 + i % 3)); }

    // (each look at a kept object in a method of its own: under .NET a method's temporaries can keep an object
    // alive until the method returns)
    [MethodImpl(MethodImplOptions.NoInlining)] static void ShowRes() { Console.WriteLine(Res.Kept.Tag); Console.WriteLine(string.Join(",", Res.Kept.Items)); Res.Kept = null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void ShowAgain() { Console.WriteLine(Again.Kept.Round + " " + Again.Finalized); Again.Kept = null; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void ShowNode() { Console.WriteLine(Node.Kept.Name + Node.Kept.Other.Name + Node.Kept.Other.Other.Name); }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Make();
        Collect(); Churn();
        ShowRes();
        Console.WriteLine(Res.Finalized);
        // let go again: no second finalization
        Collect(); Churn();
        Console.WriteLine(Res.Finalized);

        MakeAgain();
        Collect(); Churn();
        ShowAgain();
        // it asked to be finalized again
        Collect(); Churn();
        Console.WriteLine(Again.Finalized);

        // one object of a cycle brought back: the other one it refers to is still there
        MakeCycle();
        Collect(); Churn();
        ShowNode();
    }
}
