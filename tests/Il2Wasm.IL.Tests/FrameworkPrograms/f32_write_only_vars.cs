// A pinned local nothing reads, given only a borrow: string.GetNonRandomizedHashCode's `fixed` over the string it
// hashes (every Dictionary<string, _> lookup) and the program's own `fixed` over a parameter. Plan.DeadVars: lowering
// neither stores into it nor releases it (IL2WASM_NO_DEAD_VARS=1: it does). A pinned new string the method owns keeps
// its count and is let go at the end; a string pinned, then dropped by its caller, is freed after the pin.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe int Pin(string s)
    {
        int t = 0;
        fixed (char* p = s) for (int i = 0; i < s.Length; i++) t += p[i];
        return t;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe int PinNew(int k)
    {
        fixed (char* p = new string('b', k)) return p[0] * k;
    }

    static WeakReference? s_weak;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int PinAndDrop()
    {
        string s = new string('c', 3);
        s_weak = new WeakReference(s);
        return Pin(s);
    }

    static void Main()
    {
        var counts = new Dictionary<string, int>();
        string[] words = "the cat and the dog and the bird".Split(' ');
        for (int round = 0; round < 3; round++)
            foreach (var w in words) counts[w] = counts.TryGetValue(w, out var c) ? c + 1 : 1;
        foreach (var w in new[] { "the", "and", "cat", "fish" }) Console.WriteLine($"{w} {counts.GetValueOrDefault(w)}");
        Console.WriteLine(Pin("hello") + PinNew(4));
        Console.WriteLine(PinAndDrop());
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Console.WriteLine(s_weak!.IsAlive);
        s_weak = null;
    }
}
