// Results the plan borrows instead of counting (docs/MEMORY-IR.md 2.1, 2.3, 2.5, 7.4; Memory/Plan.cs):
// - a persistent static's object, read or returned by its getter, is borrowed: stored elsewhere it is counted, so
//   dropping those copies never frees it (a missing count would free it: its field then reads poison);
// - Unsafe.As (MemoryMarshal.GetArrayDataReference's) is its operand, borrowed: the array lives while the byref is used;
// - a returned byref's keeper moved into the hold (HoldFrom): the callee's array is freed at the caller's last use;
// - an object used only through its fields lives in the frame: its children are released at its last use.
// plan: only
// live: 40
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class T { public int V = 7; }
sealed class Config { public int Level = 41; public static readonly Config Shared = new Config(); public static Config Get => Shared; }
sealed class Pt { public T? A; public int B; }

static class Program
{
    static WeakReference? s_arr, s_child;

    [MethodImpl(MethodImplOptions.NoInlining)] static int Keep()
    {
        int sum = 0;
        for (int i = 0; i < 20; i++)
        {
            var l = new List<Config> { Config.Get, Config.Shared };
            var h = new Holder { C = Config.Get };
            sum += l[0].Level + l[1].Level + h.C!.Level;
        }
        return sum;
    }
    sealed class Holder { public Config? C; }

    [MethodImpl(MethodImplOptions.NoInlining)] static ref int First(int n)
    {
        var a = new int[n];
        s_arr = new WeakReference(a);
        a[0] = n;
        return ref MemoryMarshal.GetArrayDataReference(a);
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static int UseFirst() { ref int r = ref First(5); r += 2; return r; }

    [MethodImpl(MethodImplOptions.NoInlining)] static int Frame()
    {
        int s = 0;
        for (int i = 0; i < 10; i++)
        {
            var p = new Pt { A = new T(), B = i };
            if (i == 9) s_child = new WeakReference(p.A);
            s += p.A.V + p.B;
        }
        return s;
    }

    static void Main()
    {
        Console.WriteLine(Keep());
        Console.WriteLine(Config.Shared.Level);
        Console.WriteLine(UseFirst());
        Console.WriteLine(Frame());
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine(s_arr!.IsAlive + " " + s_child!.IsAlive);
    }
}
