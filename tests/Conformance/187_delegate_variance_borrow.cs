// A delegate made as one instance of a generic delegate and invoked through another that variance converts it to
// (Action<object> held as an Action<string>, Func<string> as a Func<object>) runs the target it was made with: a
// borrow of a field across such an invocation must see the target's stores (FieldBorrows.cs, DelegateConvertible).
// Each target here lets go of the object the caller is running on and makes a new one of the same size, which takes
// its memory if the borrow were wrongly taken.
using System;
using System.Runtime.InteropServices;

class Node
{
    public string Name;
    public Node(string name) { Name = name; }
    public void Run(Action<string> a) { a("go"); Console.WriteLine("still " + Name); }
    public object Get(Func<object> f) { var r = f(); Console.WriteLine("kept " + Name); return r; }
}

class Holder
{
    public Node? F;
    public static Holder? Current;
    public static Node? Spare;

    static void Clear(object o)
    {
        Current!.F = null;
        Spare = new Node("other " + o);
    }

    static string Make()
    {
        Current!.F = null;
        Spare = new Node("made");
        return "value";
    }

    public void Step()
    {
        Action<object> ao = Clear;
        Action<string> a = ao;
        F!.Run(a);
    }

    public void Step2()
    {
        Func<string> fs = Make;
        Func<object> fo = fs;
        Console.WriteLine(F!.Get(fo));
    }
}

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var h = new Holder { F = new Node("first") };
        Holder.Current = h;
        h.Step();
        Console.WriteLine(Holder.Spare!.Name + " " + (h.F == null));
        h.F = new Node("second");
        h.Step2();
        Console.WriteLine(Holder.Spare!.Name + " " + (h.F == null));
    }
}
