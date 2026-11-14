using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

class Program
{
    int _bonus = 100;

    async Task<int> Instance(int[] items)
    {
        var fs = new List<Func<int>>();
        foreach (var item in items)
        {
            await Task.Yield();
            int local = item * 2;
            fs.Add(() => item + local + _bonus);
        }
        int shared = 1;
        Func<int> bump = () => ++shared;
        await Task.Delay(1);
        bump(); bump();
        return fs.Sum(f => f()) + shared;
    }

    static async Task<string> Loops()
    {
        var acts = new List<Func<string>>();
        for (int i = 0; i < 3; i++)
        {
            int copy = i;
            await Task.Yield();
            acts.Add(() => "i" + i + "c" + copy);
        }
        int n = 0;
        while (n < 2)
        {
            var label = "w" + n;
            acts.Add(() => label);
            n++;
            await Task.Yield();
        }
        int Local(int x) => x + n;
        return string.Join(",", acts.Select(a => a())) + " " + Local(10);
    }

    static IEnumerable<Func<int>> Iter()
    {
        foreach (var x in new[] { 5, 6, 7 }) yield return () => x * 10;
    }

    static async Task<int> Parallel(int[] xs)
    {
        var tasks = new List<Task<int>>();
        foreach (var x in xs) tasks.Add(Task.Run(() => x * x));
        var r = await Task.WhenAll(tasks);
        return r.Sum();
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(new Program().Instance(new[] { 1, 2, 3 }).Result);
        Console.WriteLine(Loops().Result);
        Console.WriteLine(string.Join(" ", Iter().Select(f => f())));
        Console.WriteLine(Parallel(new[] { 1, 2, 3, 4 }).Result);
        Func<int, Task<int>> lam = async k => { var gs = new List<Func<int>>(); foreach (var j in Enumerable.Range(0, k)) { await Task.Yield(); gs.Add(() => j + k); } return gs.Sum(g => g()); };
        Console.WriteLine(lam(3).Result);
    }
}
