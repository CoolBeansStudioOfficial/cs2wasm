// Exclusive parts, condition 3 (Compiler/ExclusiveParts.cs): the holder of a list is let go by a callback while a call
// on the list runs. NOT RIGHT with exclusive parts on (CS2WASM_EXCLUSIVE_PARTS=1): the checked build reads freed
// memory in the second callback, the shipped build prints `seen 6` for .NET's 7. Off (the default) it prints what
// .NET prints. Kept as the program to fix before exclusive parts can be on by default (docs/AUDIT.md A3.5, "The
// build, round 8").
//   CS2WASM_EXCLUSIVE_PARTS=1 cs2wasmc tools/parts-holder-goes.cs --checked -o hg.wasm   then   node host/run.mjs hg.wasm
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Tag { readonly int _k; public Tag(int k) { _k = k; P.Live[k]++; } ~Tag() { System.Threading.Interlocked.Decrement(ref P.Live[_k]); } }
sealed class E { public E Up; public List<E> Kids = new(); public Tag Tag = new Tag(5); }

static class P
{
    public static readonly int[] Live = new int[8];
    static E s_e;

    static void Report(string when)
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(when + ": " + string.Join(" ", Live));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void HolderGoes()
    {
        s_e = new E();
        for (int i = 0; i < 7; i++) s_e.Kids.Add(new E { Up = s_e });
        int seen = 0;
        // (the holder is let go by the callback while the call on its list runs)
        int removed = s_e.Kids.RemoveAll(k => { s_e = null; seen += k.Kids.Count + (k.Up != null ? 1 : 0); for (int j = 0; j < 4; j++) new E().Kids.Add(new E()); return seen % 2 == 0; });
        Console.WriteLine("3: removed " + removed + " seen " + seen);
        Report("3: gone");
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main() { HolderGoes(); }
}
