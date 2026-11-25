// A write through MemoryMarshal.GetArrayDataReference (its IL goes through Array.RawData's byte field) is a write to
// the array's elements: the type sets must see it in Elem(T), or a virtual call on an element read misses the type
// written that way. Here B, stored only through the reference, overrides M to clear the holder's field, so h.F's
// string, held across the call as a borrow when the call's targets are only A.M, would be freed under it
// (TypeSets.ElementAddress). Also a write through Unsafe.Add of the reference, and an element read held across both.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Holder { public string? F; }
class A { public virtual int M(Holder h) => 1; }
sealed class B : A { public override int M(Holder h) { h.F = null; return 2; } }
sealed class C : A { public override int M(Holder h) { h.F = null; return 3; } }
sealed class Node { public int X; public string Name = ""; }

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Put(A[] arr) => MemoryMarshal.GetArrayDataReference(arr) = new B();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void PutAt(A[] arr, int i) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(arr), i) = new C();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run(A[] arr, int i, Holder h)
    {
        string s = h.F!;
        int r = arr[i].M(h);
        // (allocations of the string's size: freed memory is taken first)
        int z = 0;
        for (int k = 0; k < 4; k++) { string t = new string('z', 6 + k % 2); z += t.Length; }
        return r * 1000 + s.Length * 10 + (s[0] == 'x' ? 1 : 0) + z * 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Replace(Node[] arr, int x)
    {
        Node old = arr[0];
        MemoryMarshal.GetArrayDataReference(arr) = new Node { X = x, Name = "new" + x };
        var a = new Node { X = -1, Name = "a" };
        return old.X * 1000 + old.Name.Length + a.X + arr[0].X;
    }

    static void Main()
    {
        var arr = new A[] { new A(), new A() };
        Put(arr);
        PutAt(arr, 1);
        Console.WriteLine(Run(arr, 0, new Holder { F = new string('x', 6) }));
        Console.WriteLine(Run(arr, 1, new Holder { F = new string('x', 7) }));
        var nodes = new[] { new Node { X = 7, Name = "seven" } };
        Console.WriteLine(Replace(nodes, 9));
        Console.WriteLine(nodes[0].X + " " + nodes[0].Name);
    }
}
