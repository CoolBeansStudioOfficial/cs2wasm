// Ported from tests/Conformance/121_cycles_through_arrays.cs (the old suite): the same program, for the cycle groups (docs/MEMORY-IR.md S2).
// live: 0
// References between objects that can form cycles, held in array elements and in objects with many fields, stored
// while the holder is reachable from a static and taken out after it no longer is, in every order.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

class Node
{
    public static int Live;
    public string Name;
    public Node[] Arr;
    public Node Other;
    public Node(string n) { Name = n; Live++; }
    ~Node() { Live--; }
}

class Wide
{
    public static int Live;
    public long A0, A1, A2, A3, A4, A5, A6, A7, A8, A9;
    public Wide W0, W1, W2, W3, W4, W5, W6, W7, W8, W9, W10, W11, W12, W13, W14, W15, W16, W17, W18, W19;
    public string Name;
    public Wide(string n) { Name = n; Live++; }
    ~Wide() { Live--; }
}

static class P
{
    static Node[] s_nodes;
    static Wide s_wide;

    static string Churn() { var l = new List<Node>(); for (int i = 0; i < 40; i++) l.Add(new Node("churn" + i) { Arr = new Node[3] }); return l[39].Name; }

    static void ArrayHeldByStatic()
    {
        s_nodes = new Node[4];
        var c = new Node(new string('c', 9));
        s_nodes[0] = c;                 // stored while the array is held by the static
        var a = s_nodes; s_nodes = null; // the array lives on in a local
        c.Arr = a;                      // c -> array
        a[1] = c;                       // array -> c: a cycle
        a[0] = null;                    // the first reference, taken out
        Churn();
        Console.WriteLine(c.Name.Length + " " + (a[1] == c) + " " + a[1].Arr.Length);
        a[1] = null;
        Churn();
        Console.WriteLine(c.Name.Length + " " + (c.Arr == a));
        a[2] = c; a[3] = c; a[2] = null;
        Console.WriteLine(a[3].Name.Length);
    }

    static void WideHeldByStatic()
    {
        s_wide = new Wide(new string('w', 11));
        var x = new Wide(new string('x', 12));
        s_wide.W19 = x;                 // a slot far into the object, stored while the static holds it
        s_wide.W2 = x;
        var w = s_wide; s_wide = null;
        x.W18 = w; x.W1 = w;            // cycles through near and far slots
        w.W19 = null;
        Churn();
        Console.WriteLine(x.Name.Length + " " + (w.W2 == x) + " " + x.W18.Name.Length);
        w.W2 = null; x.W18 = null;
        Churn();
        Console.WriteLine(x.W1.Name.Length + " " + w.Name.Length);
        w.W17 = x; w.W16 = x; w.W17 = null; x.W1 = null;
        Console.WriteLine(w.W16.Name.Length);
    }

    static void Ring(int n)
    {
        var all = new Node[n];
        for (int i = 0; i < n; i++) all[i] = new Node(new string('r', 13 + i)) { Arr = new Node[n] };
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) all[i].Arr[j] = all[(i + j) % n];
        for (int i = 0; i < n; i++) all[i].Other = all[(i + 1) % n];
        // take them apart in an order of the data's own
        for (int k = 0; k < n * n; k++)
        {
            int i = (k * 7) % n, j = (k * 3) % n;
            all[i].Arr[j] = k % 3 == 0 ? all[j] : null;
            if (k % 5 == 0) Churn();
        }
        int sum = 0;
        for (int i = 0; i < n; i++) { sum += all[i].Name.Length + all[i].Other.Name.Length; foreach (var e in all[i].Arr) if (e != null) sum += e.Name.Length; }
        Console.WriteLine(sum);
    }

    static void Main()
    {
        ArrayHeldByStatic();
        WideHeldByStatic();
        Ring(int.Parse("6"));
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine(Node.Live + " " + Wide.Live);
    }
}
