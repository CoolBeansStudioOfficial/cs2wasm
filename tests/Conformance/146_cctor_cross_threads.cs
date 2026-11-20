// Two types whose static initializers use each other, first touched by two threads at once: each thread runs one
// initializer and needs the other's. .NET sees that waiting would never end and lets the thread go on (it sees the
// other type's statics as they are so far), as it does for one thread; neither may wait forever.
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class A
{
    public static readonly int Value;
    static A() { Thread.Sleep(50); Value = B.Value + 1; }
    public static void Touch() { }
}

static class B
{
    public static readonly int Value;
    static B() { Thread.Sleep(50); Value = A.Value + 1; }
    public static void Touch() { }
}

static class C
{
    public static readonly int Value;
    static C() { Thread.Sleep(50); Value = D.Value + 1; }
}

static class D
{
    public static readonly int Value;
    static D() { Thread.Sleep(50); Value = E.Value + 1; }
}

static class E
{
    public static readonly int Value;
    static E() { Thread.Sleep(50); Value = C.Value + 1; }
}

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        int a = 0, b = 0;
        var t1 = new Thread(() => { a = A.Value; });
        var t2 = new Thread(() => { b = B.Value; });
        t1.Start(); t2.Start();
        t1.Join(); t2.Join();
        // (which thread saw the other's statics unset depends on timing: only that both ended and agree)
        Console.WriteLine((a == A.Value && b == B.Value && A.Value + B.Value >= 1) ? "two ended" : "wrong " + a + " " + b);

        int c = 0, d = 0, e = 0;
        var u1 = new Thread(() => { c = C.Value; });
        var u2 = new Thread(() => { d = D.Value; });
        var u3 = new Thread(() => { e = E.Value; });
        u1.Start(); u2.Start(); u3.Start();
        u1.Join(); u2.Join(); u3.Join();
        Console.WriteLine((c == C.Value && d == D.Value && e == E.Value) ? "three ended" : "wrong " + c + " " + d + " " + e);
    }
}
