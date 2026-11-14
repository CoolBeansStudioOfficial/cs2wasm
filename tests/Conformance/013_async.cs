using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

// a custom awaiter, like Godot's ToSignal
sealed class Signal
{
    readonly List<Action> _waiters = new();
    public string Name;
    public Signal(string name) { Name = name; }
    public SignalAwaiter GetAwaiter() => new SignalAwaiter(this);
    public void Emit() { var w = new List<Action>(_waiters); _waiters.Clear(); foreach (var a in w) a(); }
    internal void Add(Action a) => _waiters.Add(a);
}

readonly struct SignalAwaiter : INotifyCompletion
{
    readonly Signal _s;
    public SignalAwaiter(Signal s) { _s = s; }
    public bool IsCompleted => false;
    public void OnCompleted(Action continuation) => _s.Add(continuation);
    public string GetResult() => _s.Name + " fired";
}

static class Program
{
    static async Task<int> Add(int a, int b)
    {
        await Task.Yield();
        return a + b;
    }

    static async Task<string> Chain()
    {
        int x = await Add(1, 2);
        int y = await Add(x, 10);
        return "chain=" + y;
    }

    static async Task Fails()
    {
        await Task.Yield();
        throw new InvalidOperationException("async boom");
    }

    static async Task<int> TryCatchFinally()
    {
        int n = 0;
        try
        {
            await Fails();
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine("caught: " + e.Message);
            n += 1;
        }
        finally
        {
            n += 10;
            Console.WriteLine("finally");
        }
        return n;
    }

    static async Task Waiter(Signal s, string who)
    {
        Console.WriteLine(who + " waiting");
        string r = await s;
        Console.WriteLine(who + " got: " + r);
    }

    static async Task<int> Loop()
    {
        int total = 0;
        var items = new List<int> { 1, 2, 3 };
        foreach (var i in items)
        {
            total += await Add(i, i);
        }
        return total;
    }

    static async ValueTask<int> Value(int v) { await Task.Delay(1); return v * 3; }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Add(2, 3).Result);
        Console.WriteLine(Chain().GetAwaiter().GetResult());
        Console.WriteLine(TryCatchFinally().Result);
        var t = Fails();
        try { t.Wait(); } catch (AggregateException e) { Console.WriteLine("aggregate: " + e.InnerException.Message); }
        Console.WriteLine(t.IsFaulted);
        var sig = new Signal("ready");
        var w1 = Waiter(sig, "A");
        var w2 = Waiter(sig, "B");
        Console.WriteLine("emitting " + w1.IsCompleted);
        sig.Emit();
        Console.WriteLine(w1.IsCompleted + " " + w2.IsCompleted);
        Console.WriteLine(Loop().Result);
        Console.WriteLine(Value(7).Result);
        var all = Task.WhenAll(Add(1, 1), Add(2, 2), Add(3, 3));
        Console.WriteLine(string.Join(",", all.Result));
        var tcs = new TaskCompletionSource<string>();
        var pending = Echo(tcs.Task);
        Console.WriteLine("pending " + pending.IsCompleted);
        tcs.SetResult("hello");
        Console.WriteLine(pending.Result);
    }

    static async Task<string> Echo(Task<string> source) => "echo " + await source;
}
