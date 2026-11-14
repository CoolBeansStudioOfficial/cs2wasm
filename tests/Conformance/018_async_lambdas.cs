using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

class Button
{
    public event Action Pressed;
    public void Press() => Pressed?.Invoke();
}

class Game
{
    int _score;
    readonly List<Task> _pending = new();

    public void Wire(Button b, TaskCompletionSource<int> gate)
    {
        // a Godot-style signal handler: an async lambda capturing `this` and a local
        string label = "press";
        b.Pressed += async () =>
        {
            Console.WriteLine(label + " start " + _score);
            int bonus = await gate.Task;
            _score += bonus;
            Console.WriteLine(label + " done " + _score);
        };
    }

    public async Task<int> Total(IEnumerable<int> xs)
    {
        async Task<int> Twice(int v) { await Task.Yield(); return v * 2; }
        int sum = 0;
        foreach (var x in xs) sum += await Twice(x);
        return sum;
    }

    public IEnumerable<string> Labels(int n)
    {
        IEnumerable<string> Inner(int k) { for (int i = 0; i < k; i++) yield return "L" + i; }
        return Inner(n).Select(s => s.ToLower());
    }
}

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var g = new Game();
        var b = new Button();
        var gate = new TaskCompletionSource<int>();
        g.Wire(b, gate);
        b.Press();
        Console.WriteLine("before gate");
        gate.SetResult(10);
        b.Press(); // the gate is open: this one runs to the end at once (one continuation per task keeps .NET's order fixed)
        Console.WriteLine(g.Total(new[] { 1, 2, 3 }).Result);
        Console.WriteLine(string.Join(",", g.Labels(3)));
        Func<int, Task<string>> f = async n => { await Task.Delay(1); return "n=" + n; };
        Console.WriteLine(f(5).Result);
        Func<Task> thrower = async () => { await Task.Yield(); throw new InvalidOperationException("lambda boom"); };
        try { thrower().Wait(); } catch (AggregateException e) { Console.WriteLine(e.InnerException.Message); }
    }
}
