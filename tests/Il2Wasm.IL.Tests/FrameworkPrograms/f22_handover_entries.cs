// The call rule's owned variants (docs/MEMORY-IR.md 4.3, 4.4) at the two places a count cannot be handed over:
// - a constructor's this at a NewObj: the new object is the caller's, passed by no operand. A medium constructor has
//   one owned variant, for every HandOver parameter; called with its argument handed over, that variant also owned
//   this and released the caller's object at the constructor's end (the result read freed memory);
// - a struct parameter: it arrives as an address into the caller's copy, which the callee's plan does not count. An
//   owned variant took the struct's references from the caller and never released them (the weak reference stayed
//   alive).
// The naive plan has no variants, so this runs with IL2WASM_MEM_PLAN=1 only.
// plan: only
// live: 53
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

sealed class Holder
{
    public int[] Data;
    public int Sum;
    public string Name;
    static int s_spin;

    // (medium: above the small cap, so its one owned variant hands over this and src together; this and src are
    // dead before the last loop, so both are HandOver, keeps running)
    [MethodImpl(MethodImplOptions.NoInlining)]
    public Holder(List<int> src, int n)
    {
        Data = new int[src.Count];
        for (int i = 0; i < Data.Length; i++) Data[i] = src[i] * 2 + n;
        int s = 0;
        for (int i = 0; i < Data.Length; i++) s += Data[i] ^ i;
        Name = "h" + src.Count;
        Sum = s;
        for (int i = 0; i < n; i++) s_spin += i * s;
    }
}

struct Pair { public object A; public string B; }

sealed class One
{
    public Pair P;
    [MethodImpl(MethodImplOptions.NoInlining)] public One(Pair p) { P = p; }
}

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static List<int> MakeList(int k) { var l = new List<int>(); for (int i = 0; i < k; i++) l.Add(i * k); return l; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int UseHolders()
    {
        int total = 0;
        for (int k = 1; k < 20; k++)
        {
            var h = new Holder(MakeList(k), 30);
            // (made after the constructor returns: freed memory of the holder's size is taken first)
            var other = new Holder(MakeList(1), 0);
            total += h.Sum + h.Data[k - 1] + h.Name.Length + other.Data[0];
        }
        return total;
    }

    static WeakReference? s_weak;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int UseOne()
    {
        var target = new object();
        s_weak = new WeakReference(target);
        var one = new One(new Pair { A = target, B = "b" });
        return one.P.B.Length + (one.P.A == target ? 1 : 0);
    }

    static void Main()
    {
        Console.WriteLine(UseHolders());
        Console.WriteLine(UseOne());
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Console.WriteLine(s_weak!.IsAlive);
        s_weak = null;
    }
}
