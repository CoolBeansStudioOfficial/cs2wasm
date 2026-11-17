// A tree held by a static and then let go: its nodes refer to each other in cycles (parent links), and a static is
// only one more reference: when it is cleared the tree goes (section 4.3: nothing that was once in a static is kept).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

sealed class N
{
    public static int Live;
    public N? Parent; public N? Buddy; public readonly List<N> Kids = new();
    public N() { Live++; }
    ~N() { Live--; }
    public N Add(N c) { c.Parent = this; Kids.Add(c); return c; }
}

static class Program
{
    static N? s_root;
    static readonly List<N> s_all = new();

    static void Build()
    {
        var root = new N();
        s_root = root;
        for (int i = 0; i < 50; i++)
        {
            var c = root.Add(new N());
            s_all.Add(c);
            for (int j = 0; j < 3; j++) c.Add(new N());
        }
        // (two of them refer to each other as well)
        s_all[0].Buddy = s_all[1]; s_all[1].Buddy = s_all[0];
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        for (int round = 0; round < 200; round++)
        {
            Build();
            if (s_root!.Kids.Count != 50 || s_root.Kids[7].Kids[2].Parent!.Parent != s_root) Console.WriteLine("wrong");
            s_all.Clear();
            s_root = null;
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        // (.NET may not have collected the last tree yet: 201 nodes)
        Console.WriteLine(N.Live <= 201 ? "gone" : "left: " + N.Live);
    }
}
