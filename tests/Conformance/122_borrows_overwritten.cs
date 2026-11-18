// A value read from a field, an element, a property or a collection, whose source is overwritten while the value is
// still in use: by the loop's own body, by a method called in between, by a setter, through another name for the
// same object. The value must stay alive for as long as the code can use it.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

class Box
{
    public string Name; public int[] Data; public Box Next;
    public Box(string n) { Name = n; Data = new int[48]; for (int i = 0; i < 48; i++) Data[i] = n.Length; }
    public int Sum() => Data[0] + Data[47] + Name.Length;
}

class Owner
{
    public Box Head;
    Box _item;
    public Box Item { get => _item; set => _item = value; }
    public Box[] Slots = new Box[4];
    public List<Box> List = new();
    public Dictionary<string, Box> Map = new();
    public void DropHead() { Head = null; }
    public void DropAll() { Head = null; _item = null; Array.Clear(Slots); List.Clear(); Map.Clear(); }
    public Box Take() { var b = _item; _item = null; return b; }
}

static class P
{
    static string Churn(int k) { var l = new List<Box>(); for (int i = 0; i < 40; i++) l.Add(new Box("churn" + (i + k))); return l[39].Name; }
    static string Mk(char c, int n) => new string(c, n + int.Parse("20"));
    static int Use(Box a, int n) { Churn(n); return a.Sum() + n; }
    static Owner s_owner;

    static void Fields()
    {
        var o = new Owner { Head = new Box(Mk('a', 1)) };
        var h = o.Head;            // a local read from a field
        o.DropHead();              // the field cleared by a call
        Churn(1);
        Console.WriteLine(h.Sum());
        o.Head = new Box(Mk('b', 2));
        var h2 = o.Head;
        o.Head = new Box(Mk('c', 3)); // overwritten directly
        Churn(2);
        Console.WriteLine(h2.Sum() + " " + o.Head.Sum());
        // through another name for the same owner
        var p = o; var h3 = o.Head; p.Head = null; Churn(3);
        Console.WriteLine(h3.Sum());
    }

    static void Properties()
    {
        var o = new Owner { Item = new Box(Mk('d', 4)) };
        var it = o.Item; o.Item = null; Churn(4);
        Console.WriteLine(it.Sum());
        o.Item = new Box(Mk('e', 5));
        Console.WriteLine(Use(o.Item, (o.Item = null) == null ? 1 : 2));
        o.Item = new Box(Mk('f', 6));
        var t = o.Take(); Churn(5);
        Console.WriteLine(t.Sum() + " " + (o.Item == null));
    }

    static void Elements()
    {
        var arr = new Box[3];
        for (int i = 0; i < 3; i++) arr[i] = new Box(Mk('g', 7 + i));
        int sum = 0, k = 0;
        foreach (var e in arr) { arr[k++] = null; sum += Use(e, 6); }
        Console.WriteLine(sum);
        for (int i = 0; i < 3; i++) arr[i] = new Box(Mk('h', 10 + i));
        for (int i = 0; i < arr.Length; i++) { var e = arr[i]; arr[i] = new Box("x"); sum += Use(e, 7); }
        Console.WriteLine(sum);
        var e0 = arr[0]; Array.Clear(arr); Churn(8);
        Console.WriteLine(e0.Sum());
    }

    static void Collections()
    {
        var o = new Owner();
        for (int i = 0; i < 4; i++) { o.List.Add(new Box(Mk('i', 13 + i))); o.Map["k" + i] = new Box(Mk('j', 17 + i)); }
        var first = o.List[0]; o.List.RemoveAt(0); Churn(9);
        Console.WriteLine(first.Sum());
        var last = o.List[^1]; o.List.Clear(); Churn(10);
        Console.WriteLine(last.Sum());
        var m = o.Map["k2"]; o.Map.Remove("k2"); Churn(11);
        Console.WriteLine(m.Sum());
        if (o.Map.TryGetValue("k1", out var m1)) { o.Map.Clear(); Churn(12); Console.WriteLine(m1.Sum()); }
        var q = new Queue<Box>(); q.Enqueue(new Box(Mk('k', 21)));
        var dq = q.Peek(); q.Clear(); Churn(13);
        Console.WriteLine(dq.Sum());
        // a list whose items are replaced while one of them is in use
        var list = new List<Box> { new Box(Mk('l', 22)), new Box(Mk('m', 23)) };
        int s = 0;
        for (int i = 0; i < list.Count; i++) { var b = list[i]; list[i] = new Box("y"); s += Use(b, 14); }
        Console.WriteLine(s);
    }

    static void Chains()
    {
        Box head = null;
        for (int i = 0; i < 5; i++) head = new Box(Mk('n', 24 + i)) { Next = head };
        int sum = 0;
        // each node is cut from the next while the walk holds it
        for (var n = head; n != null;) { var next = n.Next; n.Next = null; sum += Use(n, 15); n = next; }
        Console.WriteLine(sum);
        // the walk's own node is unlinked by a call
        var o = new Owner { Head = new Box(Mk('o', 30)) { Next = new Box(Mk('p', 31)) } };
        for (var n = o.Head; n != null; n = n.Next) { o.DropHead(); sum += Use(n, 16); }
        Console.WriteLine(sum);
    }

    static void Statics()
    {
        s_owner = new Owner { Head = new Box(Mk('q', 32)), Item = new Box(Mk('r', 33)) };
        var h = s_owner.Head; var o = s_owner;
        s_owner = null; Churn(17);
        Console.WriteLine(h.Sum() + " " + o.Item.Sum());
        s_owner = o;
        var it = s_owner.Item;
        s_owner.DropAll(); Churn(18);
        Console.WriteLine(it.Sum());
        Console.WriteLine(Use(s_owner.Slots.Length == 4 ? (s_owner.Head = new Box(Mk('s', 34))) : null, Drop()));
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Drop() { s_owner.Head = null; s_owner = null; return 3; }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Fields(); Properties(); Elements(); Collections(); Chains(); Statics();
    }
}
