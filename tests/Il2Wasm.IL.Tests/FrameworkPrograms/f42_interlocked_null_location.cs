// Interlocked on a null location throws NullReferenceException (the CLR dereferences the ref first), for every overload
// the program can call: the reference, int, long and generic forms (the generic CompareExchange<string?> was not refused).
using System;
using System.Runtime.CompilerServices;
using System.Threading;

static class Program
{
    static string Outcome(Action a)
    {
        try { a(); return "ok"; }
        catch (NullReferenceException) { return "NRE"; }
        catch (Exception e) { return e.GetType().Name; }
    }

    static void Main()
    {
        Console.WriteLine("cmp string: " + Outcome(() => Interlocked.CompareExchange<string?>(ref Unsafe.NullRef<string?>(), "a", null)));
        Console.WriteLine("cmp object: " + Outcome(() => Interlocked.CompareExchange(ref Unsafe.NullRef<object?>(), new object(), null)));
        Console.WriteLine("cmp int: " + Outcome(() => Interlocked.CompareExchange(ref Unsafe.NullRef<int>(), 1, 0)));
        Console.WriteLine("cmp long: " + Outcome(() => Interlocked.CompareExchange(ref Unsafe.NullRef<long>(), 1L, 0L)));
        Console.WriteLine("exchange object: " + Outcome(() => Interlocked.Exchange(ref Unsafe.NullRef<object?>(), new object())));
        Console.WriteLine("exchange int: " + Outcome(() => Interlocked.Exchange(ref Unsafe.NullRef<int>(), 3)));
        Console.WriteLine("exchange long: " + Outcome(() => Interlocked.Exchange(ref Unsafe.NullRef<long>(), 3L)));
        Console.WriteLine("increment: " + Outcome(() => Interlocked.Increment(ref Unsafe.NullRef<int>())));
        Console.WriteLine("add long: " + Outcome(() => Interlocked.Add(ref Unsafe.NullRef<long>(), 1L)));
        Console.WriteLine("read: " + Outcome(() => Interlocked.Read(ref Unsafe.NullRef<long>())));

        int live = 5;
        Console.WriteLine("cell: " + Interlocked.CompareExchange(ref live, 9, 5) + " " + live);
    }
}
