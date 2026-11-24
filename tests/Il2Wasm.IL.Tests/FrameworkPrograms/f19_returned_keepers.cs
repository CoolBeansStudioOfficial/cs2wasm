// Returned spans and byrefs over objects only the callee held (docs/MEMORY-IR.md 7.4): the callee hands the object
// to its caller (Rt.ReturnKeepers), which lets it go at the returned value's last use. Without the keeper the object
// is never freed (the weak reference stays alive); freed too early, the read through the byref sees poison. The
// naive plan keeps such objects (a leak, its baseline), so this runs with IL2WASM_MEM_PLAN=1 only.
// plan: only
// live: 234
using System;
using System.Runtime.CompilerServices;

sealed class Box { public int F; public int[] Arr = new int[4]; }
sealed class Payload { public int Number = 3; }

static class Program
{
    static WeakReference? s_w, s_p;
    [MethodImpl(MethodImplOptions.NoInlining)] static ref int Field(int v) { var b = new Box { F = v }; s_w = new WeakReference(b); return ref b.F; }
    [MethodImpl(MethodImplOptions.NoInlining)] static Span<int> Fresh(int n) { var a = new int[n]; a[0] = n; return a; }
    [MethodImpl(MethodImplOptions.NoInlining)] static ReadOnlySpan<char> Text(int i) => (i.ToString() + "-x").AsSpan();
    [MethodImpl(MethodImplOptions.NoInlining)] static int Use() { ref int r = ref Field(5); r += 1; return r; }
    [MethodImpl(MethodImplOptions.NoInlining)] static Span<int> Again(int n) => Fresh(n);
    // (NetWasm's fixture-memory.collector-retention shape: a byref into an object made in the return expression)
    [MethodImpl(MethodImplOptions.NoInlining)] static ref int ObjectInterior() => ref new Payload().Number;
    [MethodImpl(MethodImplOptions.NoInlining)] static ref int Watched() { var p = new Payload(); s_p = new WeakReference(p); return ref p.Number; }

    static void Main()
    {
        int t = Use();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine(t + " " + s_w!.IsAlive);
        long sum = 0;
        for (int i = 1; i < 200; i++)
        {
            var s = Fresh(i);
            sum += s[0] + s.Length;
            sum += Text(i).Length;
            sum += Again(i)[0];
        }
        Console.WriteLine(sum);
        int n = 0;
        for (int i = 0; i < 100; i++) n += ObjectInterior();
        n += Watched();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine(n + " " + s_p!.IsAlive);
    }
}
