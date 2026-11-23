// Threads (section 4.5): Array.Copy across element types and Array.SetValue store through the runtime's array code,
// which is given any array: what such a store can leave in an array is what the arrays of the program hold and what
// SetValue is given. Here coins made on the main thread go from a Coin[] into an object[] a static holds
// (Array.Copy from a derived element type), and gems go in by SetValue; workers then read them, and the main thread
// counts the same objects meanwhile.
// IL2WASM_EXP_NO_PTR=1 takes those stores to leave nothing: with IL2WASM_SHARE_CHECK=1 the first coin a worker
// counts stops the program (tools/thread-escape-breaks.sh).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Coin
{
    public static int Live;
    public int V;
    public Coin(int v) { V = v; Interlocked.Increment(ref Live); }
    ~Coin() { Interlocked.Decrement(ref Live); }
}

sealed class Gem
{
    public static int Live;
    public int V;
    public Gem(int v) { V = v; Interlocked.Increment(ref Live); }
    ~Gem() { Interlocked.Decrement(ref Live); }
}

static class Program
{
    static readonly object[] s_vault = new object[8];

    static long Look(int rounds)
    {
        long sum = 0;
        for (int i = 0; i < rounds; i++)
        {
            object o = s_vault[i & 7];
            if (o is Coin c) sum += c.V;
            else if (o is Gem g) sum += g.V * 10;
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        var coins = new Coin[4];
        for (int i = 0; i < coins.Length; i++) coins[i] = new Coin(i + 1);
        Array.Copy(coins, 0, s_vault, 0, 4);
        Array vault = s_vault;
        for (int i = 4; i < 8; i++) vault.SetValue(new Gem(i), i);
        var results = new long[4];
        var threads = new Thread[4];
        for (int t = 0; t < threads.Length; t++)
        {
            int k = t;
            threads[t] = new Thread(() => results[k] = Look(40000));
            threads[t].Start();
        }
        long mine = 0;
        for (int i = 0; i < 40000; i++) { var c = coins[i & 3]; mine += c.V; }
        mine += Look(40000);
        foreach (var th in threads) th.Join();
        Console.WriteLine(mine + " " + results[0] + " " + results[1] + " " + results[2] + " " + results[3]);
        for (int i = 0; i < coins.Length; i++) coins[i] = null!;
        Array.Clear(s_vault, 0, s_vault.Length);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Run();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine(Coin.Live + " " + Gem.Live);
    }
}
