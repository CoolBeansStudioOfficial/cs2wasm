// A temporary that borrows from a field (no count, FieldBorrows.cs) while a call it is passed to throws: the handler
// that releases a function's temporaries on the way out must not release the borrowed value. The temporaries of the
// switch's other cases are counted ones; when one of them had the same local as the borrow, the handler released
// the field's object for each exception that passed, and it was freed while the field still held it (seen as
// System.Delegate.DelegateBinding.Invoke in test 057, and as the self-hosted compiler freeing Roslyn's bound lambdas
// while their bodies lived on: proof 5).
using System;
using System.Runtime.InteropServices;

abstract class Meth
{
    public abstract object? Invoke(object? target, int flags, object? binder, object?[] args, object? culture);
}

sealed class Thrower : Meth
{
    public string Name = "thrower";
    public override object? Invoke(object? target, int flags, object? binder, object?[] args, object? culture)
    {
        if (args[^1] is int i) return i + 1;
        throw new InvalidCastException("not an int: " + args[^1]);
    }
}

// (an object that can be in a cycle: a member of its group's arena)
sealed class Node
{
    public Node? Next;
    public Meth? Method;
}

sealed class Binding(Meth method, object? first, int mode)
{
    public readonly Meth Method = method;

    public object? Invoke(object?[] args)
    {
        switch (mode)
        {
            case 0: return Call(Method, first, args);
            case 1: return Call(Method, args[0], args[1..]);
            case 2: return Call(Method, null, args);
            default:
                var all = new object?[args.Length + 1];
                all[0] = first;
                Array.Copy(args, 0, all, 1, args.Length);
                return Call(Method, null, all);
        }
    }

    static object? Call(Meth m, object? target, object?[] args) => m.Invoke(target, 4, null, args, null);
}

static class Program
{
    static void Run(Binding b, string label)
    {
        Console.WriteLine(label + " " + b.Invoke([41]));
        for (int i = 0; i < 4; i++)
            try { b.Invoke(["x" + i]); } catch (InvalidCastException e) { Console.WriteLine(label + " caught " + e.Message); }
        Console.WriteLine(label + " " + b.Invoke([1]) + " " + ((Thrower)b.Method).Name);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // the field's object held by the field alone
        Run(new Binding(new Thrower(), null, 2), "plain");
        Run(new Binding(new Thrower(), "first", 3), "closed");
        // and as a member of a cycle's group
        var n = new Node { Method = new Thrower() };
        n.Next = new Node { Next = n };
        var cyc = new Binding(n.Method, null, 2);
        n.Method = null;
        Run(cyc, "member");
        GC.KeepAlive(n);
    }
}
