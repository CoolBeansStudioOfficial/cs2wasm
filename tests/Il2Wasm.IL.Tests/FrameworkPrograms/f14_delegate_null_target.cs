// A delegate of an instance method on a null target: ArgumentException (ldftn) or NullReferenceException
// (ldvirtftn, a virtual method); a static method's delegate takes no target.
using System;

class N { public int A = 3; public int Get() => A; public virtual int V() => A + 1; }

static class Program
{
    static void T(Func<Func<int>> make)
    {
        try { var f = make(); Console.WriteLine("made " + f()); }
        catch (Exception e) { Console.WriteLine(e.GetType().Name + ": " + e.Message); }
    }
    static int S() => 9;
    static void Main()
    {
        N n = null, m = new N();
        T(() => n.Get);
        T(() => n.V);
        T(() => m.Get);
        T(() => m.V);
        T(() => S);
    }
}
