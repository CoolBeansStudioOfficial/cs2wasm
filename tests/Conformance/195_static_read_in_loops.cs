// A static's reference read in a loop, where the loop (or what it calls) stores the static meanwhile: the read must
// see every store. A callee, the loop itself, a store that only replaces a null (here and in a callee), a type's
// initializer that runs inside the loop (a first read under a condition, an initializer that reads its own static
// while it stores it, a function the initializer calls), an address taken (ref, out, Interlocked, Volatile),
// reflection, a delegate, a function pointer, a virtual and an interface call, a handler, a recursive call, nested loops, an iterator,
// a loop that stores between its turns, a generic type's statics, an auto-property; a static
// that is null (the exception at the same turn); and loops that store nothing, whose reads may be made once.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Node { public int V; public Node(int v) { V = v; } }

// (no initializers: the type has no initialization to check, so each loop below stands on its own rule)
#pragma warning disable CS8618
static class S
{
    public static List<Node> L;
    public static List<Node>? Lazy;
    public static List<Node>? Lazy2;
    public static Node R, R2, X, F, D, Vt, H, Rec, Vol, Out;
    public static List<Node> Inner;
    public static List<Node> It;
    public static List<Node>? Null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Setup()
    {
        L = new() { new Node(1), new Node(2), new Node(3), new Node(4) };
        R = new Node(5); R2 = new Node(12); X = new Node(6); F = new Node(7); D = new Node(8); Vt = new Node(9); H = new Node(10); Rec = new Node(11);
        Inner = new() { new Node(1), new Node(2) };
        It = new() { new Node(1) };
        Vol = new Node(30); Out = new Node(31);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void NewL(int v) { L = new List<Node> { new Node(v), new Node(v + 1), new Node(v + 2), new Node(v + 3) }; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Ensure() { Lazy2 ??= new List<Node> { new Node(50), new Node(51) }; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Set(ref Node n, int v) { n = new Node(v); }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Make(out Node n, int v) { n = new Node(v); }
}
#pragma warning restore CS8618
static class T
{
    public static List<Node> Prop { get; set; } = new() { new Node(20) };
    public static readonly List<Node> Ro = new() { new Node(40), new Node(41) };
    [MethodImpl(MethodImplOptions.NoInlining)] public static void NewProp(int v) { Prop = new List<Node> { new Node(v) }; }
}

class Late
{
    public static List<Node> L = Build("Late");
    public static List<Node> Build(string who) { Console.WriteLine("init " + who); return new List<Node> { new Node(60), new Node(61) }; }
}
class Late2 { public static List<Node> L = Late.Build("Late2"); }
class Late3
{
    public static List<Node> L = new() { new Node(0) };
    public static int Sum;
    static Late3()
    {
        // the initializer reads its own static in a loop that stores it
        for (int i = 0; i < 3; i++) { Sum += L[0].V; L = new List<Node> { new Node(i + 70) }; }
        Sum += Walk();
        L = new List<Node> { new Node(80), new Node(81) };
    }
    // (called by the initializer, before its last store, and by Main after it)
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Walk() { int s = 0; for (int i = 0; i < L.Count; i++) s += L[i].V; return s; }
}
class Late4
{
    public static List<Node> L = Late.Build("Late4");
    [MethodImpl(MethodImplOptions.NoInlining)] public static int First() => L[0].V;
}
static class G<T>
{
    public static List<T> L = new();
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Fresh(T v) { L = new List<T> { v }; }
}
abstract class Shape { public abstract void Touch(int v); }
sealed class Storing : Shape { public override void Touch(int v) { S.Vt = new Node(v); } }
sealed class Quiet : Shape { public override void Touch(int v) { } }
interface ITouch { void Touch(int v); }
sealed class StoringI : ITouch { public void Touch(int v) { S.Vt = new Node(v + 100); } }

static class Program
{
    static int Callee()
    {
        int sum = 0;
        for (int i = 0; i < S.L.Count; i++) { sum += S.L[i].V; if (i == 1) S.NewL(10 * (sum + 1)); }
        return sum;
    }
    static int Own()
    {
        int sum = 0;
        for (int i = 0; i < S.L.Count; i++) { if (i == 2) S.L = new List<Node> { new Node(100), new Node(200), new Node(300) }; sum += S.L[i].V; }
        return sum;
    }
    static int NullOnly()
    {
        int sum = 0;
        for (int i = 0; i < 3; i++) { S.Lazy ??= new List<Node> { new Node(i + 7) }; sum += S.Lazy[0].V + S.Lazy.Count; }
        for (int i = 0; i < 3; i++) { S.Ensure(); sum += S.Lazy2![i & 1].V; }
        return sum;
    }
    static int Inits(int n)
    {
        int sum = 0;
        for (int i = 0; i < n; i++) { Console.WriteLine("turn " + i); sum += Late.L[i & 1].V; }
        // (the first read under a condition: the initializer runs in the second turn)
        for (int i = 0; i < n; i++) { Console.WriteLine("turn2 " + i); if (i > 0) sum += Late2.L[i & 1].V; }
        // (the initializer runs in a callee the loop calls, then the loop reads)
        for (int i = 0; i < n; i++) { Console.WriteLine("turn4 " + i); sum += Late4.First(); sum += Late4.L.Count; }
        return sum;
    }
    static int Addresses()
    {
        int sum = 0;
        for (int i = 0; i < 3; i++) { sum += S.R.V; S.Set(ref S.R, 100 + i); sum += S.R.V; }
        for (int i = 0; i < 3; i++) { sum += S.X.V; var old = Interlocked.Exchange(ref S.X, new Node(200 + i)); sum += old.V + S.X.V; }
        for (int i = 0; i < 3; i++) { sum += S.Vol.V; Volatile.Write(ref S.Vol, new Node(300 + i)); sum += S.Vol.V; }
        for (int i = 0; i < 3; i++) { sum += S.Out.V; S.Make(out S.Out, 400 + i); sum += S.Out.V; }
        return sum;
    }
    // (the address was taken elsewhere: this function stores the static through its parameter, and nothing says which)
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ThroughRef(ref Node n)
    {
        int sum = 0;
        for (int i = 0; i < 3; i++) { sum += S.R2.V; n = new Node(450 + i); sum += S.R2.V; }
        return sum;
    }
    static int Reflected()
    {
        int sum = 0;
        var f = typeof(S).GetField("F", BindingFlags.Public | BindingFlags.Static)!;
        for (int i = 0; i < 3; i++) { sum += S.F.V; f.SetValue(null, new Node(500 + i)); sum += S.F.V; }
        return sum;
    }
    static int Indirect()
    {
        int sum = 0;
        Action<int> a = v => S.D = new Node(v);
        for (int i = 0; i < 3; i++) { sum += S.D.V; a(600 + i); sum += S.D.V; }
        Shape[] shapes = { new Quiet(), new Storing(), new Quiet() };
        for (int i = 0; i < shapes.Length; i++) { sum += S.Vt.V; shapes[i].Touch(700 + i); sum += S.Vt.V; }
        ITouch t = new StoringI();
        for (int i = 0; i < 2; i++) { sum += S.Vt.V; t.Touch(i); sum += S.Vt.V; }
        return sum;
    }
    static void StoreD(int v) { S.D = new Node(v); }
    static unsafe int Pointers()
    {
        int sum = 0;
        delegate*<int, void> p = &StoreD;
        for (int i = 0; i < 3; i++) { sum += S.D.V; p(650 + i); sum += S.D.V; }
        return sum;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Throws(int v) { S.H = new Node(v); throw new InvalidOperationException("x" + v); }
    static int Handlers()
    {
        int sum = 0;
        for (int i = 0; i < 3; i++)
        {
            try { sum += S.H.V; if (i == 1) Throws(800); sum += S.H.V; }
            catch (InvalidOperationException e) { sum += e.Message.Length + S.H.V; S.H = new Node(900); }
            sum += S.H.V;
        }
        return sum;
    }
    static int Recursive(int depth)
    {
        int sum = 0;
        for (int i = 0; i < 2; i++)
        {
            sum += S.Rec.V;
            if (depth > 0) sum += Recursive(depth - 1); else S.Rec = new Node(S.Rec.V + 1);
            sum += S.Rec.V;
        }
        return sum;
    }
    static int Nested()
    {
        int sum = 0;
        for (int o = 0; o < 3; o++)
        {
            for (int i = 0; i < S.Inner.Count; i++) sum += S.Inner[i].V;
            S.Inner = new List<Node> { new Node(o + 3), new Node(o + 4), new Node(o + 5) };
        }
        return sum;
    }
    static IEnumerable<int> Iterate()
    {
        for (int i = 0; i < 3; i++) { yield return S.It[0].V + S.It.Count; }
    }
    static int Generic()
    {
        int sum = 0;
        G<int>.Fresh(1); G<string>.Fresh("a");
        for (int i = 0; i < 3; i++) { sum += G<int>.L[0]; G<string>.Fresh("bb" + i); sum += G<string>.L[0].Length; if (i == 1) G<int>.Fresh(50); sum += G<int>.L[0]; }
        return sum;
    }
    static int Props()
    {
        int sum = 0;
        for (int i = 0; i < 3; i++) { sum += T.Prop[0].V; if (i == 1) T.NewProp(21); sum += T.Prop.Count; }
        for (int i = 0; i < 3; i++) { sum += T.Prop[0].V; T.Prop = new List<Node> { new Node(22 + i), new Node(0) }; sum += T.Prop.Count; }
        return sum;
    }
    static string Nulls(int n)
    {
        string r = "";
        try { for (int i = 0; i < n; i++) { r += "t" + i; if (i == 2) r += S.Null!.Count; } }
        catch (NullReferenceException) { r += " null"; }
        // (no turn: no read, no exception)
        for (int i = 0; i < 0; i++) r += S.Null!.Count;
        // (the condition reads it: the exception before the first turn)
        try { for (int i = 0; i < S.Null!.Count; i++) r += "never"; } catch (NullReferenceException) { r += " cond"; }
        return r;
    }
    // loops that store nothing: the compaction of a list a static holds, a readonly static, a list changed in place
    static int Quiet()
    {
        int sum = 0, w = 0;
        for (int i = 0; i < S.L.Count; i++) { var p = S.L[i]; if ((p.V & 1) == 0) { S.L[w++] = p; sum += p.V; } }
        S.L.RemoveRange(w, S.L.Count - w);
        for (int i = 0; i < T.Ro.Count; i++) sum += T.Ro[i].V;
        for (int i = 0; i < 5; i++) { T.Ro.Add(new Node(i)); sum += T.Ro.Count; }
        for (int i = 0; i < T.Ro.Count; i++) { if (T.Ro[i].V == 41) { T.Ro.RemoveAt(i); T.Ro.Insert(0, new Node(1000)); } sum += T.Ro[i].V; }
        return sum + S.L.Count;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        S.Setup();
        Console.WriteLine("callee " + Callee());
        Console.WriteLine("own " + Own());
        Console.WriteLine("null only " + NullOnly());
        Console.WriteLine("inits " + Inits(3));
        Console.WriteLine("late3 " + Late3.Sum + " " + Late3.Walk());
        Console.WriteLine("addresses " + Addresses() + " " + ThroughRef(ref S.R2));
        Console.WriteLine("reflected " + Reflected());
        Console.WriteLine("indirect " + Indirect() + " " + Pointers());
        Console.WriteLine("handlers " + Handlers());
        Console.WriteLine("recursive " + Recursive(3));
        Console.WriteLine("nested " + Nested());
        int it = 0, n = 0;
        foreach (var v in Iterate()) { it += v; S.It = new List<Node> { new Node(++n * 10), new Node(0) }; }
        Console.WriteLine("iterator " + it);
        Console.WriteLine("generic " + Generic());
        Console.WriteLine("props " + Props());
        Console.WriteLine("nulls " + Nulls(4));
        Console.WriteLine("quiet " + Quiet());
    }
}
