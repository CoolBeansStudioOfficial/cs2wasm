// Objects that refer to each other and die together: each finalizer runs while what its object refers to is still
// there (section 4.3: a cycle is freed with nothing a program can tell from .NET, a finalizer included).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
sealed class Res
{
    public static int Bad, Ran;
    public Res? Peer; public Owner? Up; public readonly List<Res> Kids = new(); public string Name;
    public Res(string n) { Name = n; }
    ~Res()
    {
        Ran++;
        // what the object refers to is still there when its finalizer runs
        if (Up == null || Up.Label != "owner") Bad++;
        if (Peer == null || Peer.Name.Length == 0) Bad++;
        foreach (var k in Kids) if (k.Name != "kid") Bad++;
        Peer = null; Up = null;
    }
}
sealed class Owner { public string Label = "owner"; public List<Res> All = new(); public Res? First; }
static class Program
{
    static Owner? s_keep;
    static void Unit()
    {
        var o = new Owner();
        var a = new Res("a") { Up = o }; var b = new Res("b") { Up = o };
        a.Peer = b; b.Peer = a;
        for (int i = 0; i < 3; i++) { var k = new Res("kid") { Up = o }; k.Peer = k; a.Kids.Add(k); }
        o.All.Add(a); o.All.Add(b); o.First = a;
    }
    static void Part()
    {
        var o = s_keep!;
        var a = new Res("a") { Up = o };
        a.Peer = a;
        for (int i = 0; i < 3; i++) { var k = new Res("kid") { Up = o }; k.Peer = k; a.Kids.Add(k); }
        o.All.Add(a); o.First = a;
        o.First = null; o.All.Clear();
    }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        for (int i = 0; i < 50; i++) Unit();
        s_keep = new Owner();
        for (int i = 0; i < 50; i++) Part();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); GC.WaitForPendingFinalizers();
        Console.WriteLine(Res.Bad + " " + (Res.Ran >= 400) + " " + s_keep.Label + s_keep.All.Count);
    }
}
