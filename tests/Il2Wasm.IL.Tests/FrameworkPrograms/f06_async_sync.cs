// Async methods that complete synchronously, awaited through GetAwaiter().GetResult(); a delegate of an extension
// method (a static method closed over its first argument).
using System;
using System.Threading.Tasks;

static class Ext { public static int Twice(this string s) => s.Length * 2; }

static class Program
{
    static int log;
    static async Task<int> Await(int value)
    {
        if (value > 0) log += value;
        var completed = await Task.FromResult(value);
        if (completed > 1) log++;
        return completed + 1;
    }
    static async Task<string> Text(int n) { var s = await Task.FromResult(new string('x', n)); return s + n; }
    static async Task Nothing() { await Task.CompletedTask; log += 100; }

    static void Main()
    {
        Console.WriteLine(Await(5).GetAwaiter().GetResult() + Await(0).GetAwaiter().GetResult());
        for (int i = 0; i < 3; i++) Console.WriteLine(Text(i).Result);
        Nothing().Wait();
        Console.WriteLine(log);
        Func<int> twice = "abc".Twice;
        Console.WriteLine(twice() + " " + (twice.Target as string) + " " + twice.Method.Name);
    }
}
