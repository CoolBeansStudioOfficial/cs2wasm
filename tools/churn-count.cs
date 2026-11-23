// What the two counts of a moved reference are made of (docs/AUDIT.md A3.5, "The churn loop, round 5"): the loop of
// tools/churn-raw.cs (an array of 19,392 particles in order, every survivor moved 400 slots down) with the counted
// store written by hand in its parts, the count read and written through a pointer at the object's first word. For
// cs2wasm only: the count's place is its own (every particle is also held by s_master, so no count reaches zero and
// the hand's counts are the program's). Median ns a frame.
//   cs2wasmc tools/churn-count.cs -o cc.wasm   then   node host/run.mjs cc.wasm
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Particle { public float X, Y, Vx, Vy; public int Life; public string Tag = ""; }

static unsafe class ChurnCount
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "ore wood stone iron gold gem fish meat herb cloth rope nail plank brick glass coal".Split(' ');
    static Particle Make() => new Particle { X = Next() % 640, Y = Next() % 480, Vx = (int)(Next() % 7) - 3, Vy = (int)(Next() % 7) - 3, Life = 20 + (int)(Next() % 60), Tag = s_words[Next() & 15] };
    const int N = 19392 + 400;
    static Particle[] s_master = new Particle[N];
    static Particle[] s_a = new Particle[N];
    static int s_w, s_dead;

    static long LoopNoStore(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { w++; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    static long LoopCounted(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { a[w++] = p; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    // (the reference written and nothing else)
    static long LoopRawOnly(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0) { raw[w] = raw[i]; w++; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    // (the counts as the compiler writes them: one added when the count is above zero, one taken when it is above one)
    static long LoopByHand(Particle[] a)
    {
        long sum = 0;
        int w = 0, dead = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0)
            {
                nint v = raw[i];
                int c = *(int*)v;
                if (c > 0) *(int*)v = c + 1; else dead++;
                nint old = raw[w];
                raw[w] = v;
                if (old != 0) { int oc = *(int*)old; if (oc > 1) *(int*)old = oc - 1; else dead++; }
                w++; sum += p.Tag.Length;
            }
        }
        s_w = w; s_dead = dead;
        return sum;
    }
    // (the same with a call on the paths not taken, as the compiler's have: rt.retain, the destroy)
    [MethodImpl(MethodImplOptions.NoInlining)] static void Cold(nint v) { s_dead += (int)(v & 1) + 1; }
    static long LoopByHandCalls(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0)
            {
                nint v = raw[i];
                int c = *(int*)v;
                if (c > 0) *(int*)v = c + 1; else Cold(v);
                nint old = raw[w];
                raw[w] = v;
                if (old != 0) { int oc = *(int*)old; if (oc > 1) *(int*)old = oc - 1; else Cold(old); }
                w++; sum += p.Tag.Length;
            }
        }
        s_w = w;
        return sum;
    }
    // (the paths not taken outside the loop: the loop is left for them, they end the turn they were in and the loop
    // is entered again; what the turn had read is read again there, from the index and the array alone)
    static long LoopExit(Particle[] a)
    {
        long sum = 0;
        int w = 0, i = 0, n = a.Length;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (; ; )
        {
            for (; i < n; i++)
            {
                var p = a[i];
                p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
                if (p.Life > 0)
                {
                    nint v = raw[i];
                    int c = *(int*)v;
                    if (c <= 0) goto ColdRetain;
                    *(int*)v = c + 1;
                    nint old = raw[w];
                    if (old != 0) { int oc = *(int*)old; if (oc <= 1) goto ColdRelease; *(int*)old = oc - 1; }
                    raw[w] = v;
                    w++; sum += p.Tag.Length;
                }
            }
            break;
        ColdRetain:
            Cold(raw[i]);
        ColdRelease:
            {
                nint old2 = raw[w];
                raw[w] = raw[i];
                Cold(old2);
                w++; sum += *(int*)(*(nint*)(raw[i] + 32) + 12);
                i++;
            }
        }
        s_w = w;
        return sum;
    }
    // (the same, but what the turn had read is used after the call: the engine keeps it in memory from where it is read)
    static long LoopExitLive(Particle[] a)
    {
        long sum = 0;
        int w = 0, i = 0, n = a.Length;
        nint[] raw = Unsafe.As<nint[]>(a);
        nint v = 0, old = 0;
        for (; ; )
        {
            for (; i < n; i++)
            {
                var p = a[i];
                p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
                if (p.Life > 0)
                {
                    v = raw[i];
                    int c = *(int*)v;
                    if (c <= 0) goto ColdRetain;
                    *(int*)v = c + 1;
                    old = raw[w];
                    if (old != 0) { int oc = *(int*)old; if (oc <= 1) goto ColdRelease; *(int*)old = oc - 1; }
                    raw[w] = v;
                    w++; sum += p.Tag.Length;
                }
            }
            break;
        ColdRetain:
            Cold(v);
            old = raw[w];
        ColdRelease:
            raw[w] = v;
            Cold(old);
            w++; sum += *(int*)(*(nint*)(v + 32) + 12);
            i++;
        }
        s_w = w;
        return sum;
    }
    // (the paths not taken at the top of an outer loop, reached by its branch back with a number in a local: what
    // they use of the turn comes to them as the outer loop's own values)
    static long LoopPending(Particle[] a)
    {
        long sum = 0;
        int w = 0, i = 0, n = a.Length, pending = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        nint v = 0, old = 0;
        for (; ; )
        {
            if (pending != 0)
            {
                if (pending == 1)
                {
                    Cold(v);
                    old = raw[w];
                    if (old != 0) { int oc = *(int*)old; if (oc > 1) *(int*)old = oc - 1; else Cold(old); }
                }
                else Cold(old);
                raw[w] = v;
                w++; sum += *(int*)(*(nint*)(v + 32) + 12);
                i++;
                pending = 0;
            }
            for (; i < n; i++)
            {
                var p = a[i];
                p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
                if (p.Life > 0)
                {
                    v = raw[i];
                    int c = *(int*)v;
                    if (c <= 0) { pending = 1; goto Next; }
                    *(int*)v = c + 1;
                    old = raw[w];
                    if (old != 0) { int oc = *(int*)old; if (oc <= 1) { pending = 2; goto Next; } *(int*)old = oc - 1; }
                    raw[w] = v;
                    w++; sum += p.Tag.Length;
                }
            }
            break;
        Next:;
        }
        s_w = w;
        return sum;
    }
    // (the same with the paths not taken after the loop in the code's order: the outer loop's top only branches to them)
    static long LoopPendingAfter(Particle[] a)
    {
        long sum = 0;
        int w = 0, i = 0, n = a.Length, pending = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        nint v = 0, old = 0;
        for (; ; )
        {
            if (pending == 0)
            {
                for (; i < n; i++)
                {
                    var p = a[i];
                    p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
                    if (p.Life > 0)
                    {
                        v = raw[i];
                        int c = *(int*)v;
                        if (c <= 0) { pending = 1; goto Next; }
                        *(int*)v = c + 1;
                        old = raw[w];
                        raw[w] = v;
                        if (old != 0) { int oc = *(int*)old; if (oc <= 1) { pending = 2; goto Next; } *(int*)old = oc - 1; }
                        w++; sum += p.Tag.Length;
                    }
                }
                break;
            }
            else
            {
                if (pending == 1)
                {
                    Cold(v);
                    old = raw[w];
                    raw[w] = v;
                    if (old != 0) { int oc = *(int*)old; if (oc > 1) *(int*)old = oc - 1; else Cold(old); }
                }
                else Cold(old);
                w++; sum += *(int*)(*(nint*)(v + 32) + 12);
                i++;
                pending = 0;
            }
        Next:;
        }
        s_w = w;
        return sum;
    }
    // (the read loop with each float stored as its bits, the NaN's bits chosen by a select on integers: no branch)
    static long LoopNoStoreBits(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            float x = p.X + p.Vx, vy = p.Vy, y = p.Y + vy, v2 = vy + 0.1f;
            int* q = (int*)raw[i];
            q[3] = x == x ? BitConverter.SingleToInt32Bits(x) : unchecked((int)0xFFC00000);
            q[4] = y == y ? BitConverter.SingleToInt32Bits(y) : unchecked((int)0xFFC00000);
            q[6] = v2 == v2 ? BitConverter.SingleToInt32Bits(v2) : unchecked((int)0xFFC00000);
            if (p.Life > 0) { w++; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    // (and with no NaN test at all: the bits as they are)
    static long LoopNoStoreRaw(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            float x = p.X + p.Vx, vy = p.Vy, y = p.Y + vy, v2 = vy + 0.1f;
            int* q = (int*)raw[i];
            q[3] = BitConverter.SingleToInt32Bits(x);
            q[4] = BitConverter.SingleToInt32Bits(y);
            q[6] = BitConverter.SingleToInt32Bits(v2);
            if (p.Life > 0) { w++; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    static long LoopNoStoreOnce(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            float x = p.X + p.Vx, vy = p.Vy, y = p.Y + vy, v2 = vy + 0.1f;
            p.X = x; p.Y = y; p.Vy = v2;
            if (p.Life > 0) { w++; sum += p.Tag.Length; }
        }
        s_w = w;
        return sum;
    }
    // (without the count's tests: one added, one taken; the null test of the old value stays)
    static long LoopNoTests(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0)
            {
                nint v = raw[i];
                (*(int*)v)++;
                nint old = raw[w];
                raw[w] = v;
                if (old != 0) (*(int*)old)--;
                w++; sum += p.Tag.Length;
            }
        }
        s_w = w;
        return sum;
    }
    // (the zero test alone kept on the count taken: what a class known to be plain would leave)
    static long LoopPlain(Particle[] a)
    {
        long sum = 0;
        int w = 0, dead = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0)
            {
                nint v = raw[i];
                (*(int*)v)++;
                nint old = raw[w];
                raw[w] = v;
                if (old != 0) { int oc = *(int*)old - 1; *(int*)old = oc; if (oc == 0) dead++; }
                w++; sum += p.Tag.Length;
            }
        }
        s_w = w; s_dead = dead;
        return sum;
    }
    // (one count only: the one added; the one taken)
    static long LoopAddOnly(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0)
            {
                nint v = raw[i];
                (*(int*)v)++;
                raw[w] = v;
                w++; sum += p.Tag.Length;
            }
        }
        s_w = w;
        return sum;
    }
    static long LoopTakeOnly(Particle[] a)
    {
        long sum = 0;
        int w = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0)
            {
                nint old = raw[w];
                raw[w] = raw[i];
                if (old != 0) (*(int*)old)--;
                w++; sum += p.Tag.Length;
            }
        }
        s_w = w;
        return sum;
    }
    // (the old value read and tested for null, no count)
    static long LoopOldRead(Particle[] a)
    {
        long sum = 0;
        int w = 0, dead = 0;
        nint[] raw = Unsafe.As<nint[]>(a);
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
            if (p.Life > 0)
            {
                nint old = raw[w];
                raw[w] = raw[i];
                if (old == 0) dead++;
                w++; sum += p.Tag.Length;
            }
        }
        s_w = w; s_dead = dead;
        return sum;
    }
    // (the read loop with a pointer stepped along the array and a count taken down, the test at the bottom: the shape
    // of NativeAOT-LLVM's loop)
    static long LoopPointer(Particle[] a)
    {
        long sum = 0;
        int w = 0, n = a.Length;
        if (n > 0)
            fixed (nint* start = Unsafe.As<nint[]>(a))
            {
                nint* q = start;
                do
                {
                    var p = Unsafe.As<nint, Particle>(ref *q);
                    p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.1f;
                    if (p.Life > 0) { w++; sum += p.Tag.Length; }
                    q++;
                } while (--n != 0);
            }
        s_w = w;
        return sum;
    }

    static long[] s_t = new long[4000];
    static int s_n;
    static long Med() { Array.Sort(s_t, 0, s_n); long m = s_t[s_n / 2] * 1000000000 / Stopwatch.Frequency; s_n = 0; return m; }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main() => Run();
    public static void Run()
    {
        s_seed = 12345;
        for (int i = 0; i < N; i++) { s_master[i] = Make(); s_master[i].Life = i < 400 ? 0 : 50; }
        long sum = 0;
        string[] names = { "no store", "counted store", "the reference written, nothing else", "the counts by hand, as compiled", "the counts without their tests", "the counts, the zero test alone", "one added only", "the old value read, one taken only", "the old value read and tested, no count", "no store, a pointer stepped, a count down", "the counts by hand, calls on the paths not taken", "the counts by hand, the calls outside the loop", "the same, the turn's values used after the call", "the calls at the top of an outer loop", "the calls after the loop, reached from the outer loop's top", "no store, the sums made first", "no store, the floats stored as bits chosen by a select", "no store, the floats stored as their bits, no NaN test" };
        for (int kind = 0; kind < names.Length; kind++)
        {
            for (int r = 0; r < 3000; r++)
            {
                Array.Copy(s_master, s_a, N);
                long t0 = Stopwatch.GetTimestamp();
                sum += kind switch { 0 => LoopNoStore(s_a), 1 => LoopCounted(s_a), 2 => LoopRawOnly(s_a), 3 => LoopByHand(s_a), 4 => LoopNoTests(s_a), 5 => LoopPlain(s_a), 6 => LoopAddOnly(s_a), 7 => LoopTakeOnly(s_a), 8 => LoopOldRead(s_a), 9 => LoopPointer(s_a), 10 => LoopByHandCalls(s_a), 11 => LoopExit(s_a), 12 => LoopExitLive(s_a), 13 => LoopPending(s_a), 14 => LoopPendingAfter(s_a), 15 => LoopNoStoreOnce(s_a), 16 => LoopNoStoreBits(s_a), _ => LoopNoStoreRaw(s_a) };
                long t1 = Stopwatch.GetTimestamp();
                if (r >= 1000) s_t[s_n++] = t1 - t0;
                nint[] raw = Unsafe.As<nint[]>(s_a), from = Unsafe.As<nint[]>(s_master);
                if (kind == 2 || kind == 8)
                {
                    // (no count was changed: every slot is given its own particle again, as written)
                    for (int i = 0; i < N; i++) raw[i] = from[i];
                }
                else if (kind == 6)
                {
                    // (a count added for each of the 19,392 written, none taken for what they wrote over: taken here)
                    for (int i = 0; i < N - 400; i++) (*(int*)from[i])--;
                }
                else if (kind == 7)
                {
                    // (a count taken for each written over, none added for the written: added here)
                    for (int i = 400; i < N; i++) (*(int*)from[i])++;
                }
            }
            Console.WriteLine(names[kind] + ": " + Med() + " " + sum + " " + s_w + " " + s_dead);
        }
    }
}
