// await inside every kind of conditional expression: ?:, ??, ?., &&, ||, ??=, a switch expression's arms and guards,
// an interpolated string's holes, an object initializer, an argument list after other arguments, a loop's condition.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

class Box { public string Name = "box"; public Box Next; public async Task<int> SizeAsync() { await Task.Yield(); return Name.Length; } }

static class P
{
    static int s_calls;
    static async Task<int> N(int v) { s_calls++; await Task.Yield(); return v; }
    static async Task<bool> B(bool v) { s_calls++; await Task.Yield(); return v; }
    static async Task<string> S(string v) { s_calls++; await Task.Yield(); return v; }
    static async Task<Box> Maybe(bool some) { await Task.Yield(); return some ? new Box { Name = new string('m', 5) } : null; }
    static int Sum(int a, int b, int c) => a * 100 + b * 10 + c;

    static async Task Run()
    {
        // ?: with awaits in the condition and in both arms (only one arm runs)
        int t = await B(true) ? await N(1) : await N(2);
        int f = await B(false) ? await N(3) : await N(4);
        Console.WriteLine(t + " " + f + " " + s_calls);
        // ?? and ??=
        string a = await S(null) ?? await S("fallback");
        string b = await S("first") ?? await S("unused");
        string c = null; c ??= await S("assigned"); c ??= await S("never");
        Console.WriteLine(a + " " + b + " " + c + " " + s_calls);
        // ?. on an awaited receiver, and an awaited call after ?.
        Box some = await Maybe(true), none = await Maybe(false);
        Console.WriteLine((await Maybe(true))?.Name + "|" + (await Maybe(false))?.Name + "|" + (some?.Name.Length ?? -1) + "|" + (none?.Name.Length ?? -1));
        int? size = some == null ? null : await some.SizeAsync();
        Console.WriteLine(size + " " + (none != null ? await none.SizeAsync() : -2));
        // && and ||: the right side runs only when it must
        int before = s_calls;
        bool and1 = await B(false) && await B(true), and2 = await B(true) && await B(true);
        bool or1 = await B(true) || await B(false), or2 = await B(false) || await B(false);
        Console.WriteLine(and1 + " " + and2 + " " + or1 + " " + or2 + " " + (s_calls - before));
        if (await B(true) && (await N(5) > 4 || await B(false))) Console.WriteLine("mixed");
        // a switch expression
        for (int i = 0; i < 4; i++)
        {
            string r = await N(i) switch
            {
                0 => await S("zero"),
                1 when await B(true) => "one",
                var n when n > 2 && await B(n == 3) => await S("three") + n,
                _ => "other",
            };
            Console.Write(r + " ");
        }
        Console.WriteLine();
        // interpolation, arguments evaluated left to right around awaits, initializers
        Console.WriteLine($"{await N(7)}-{(await B(true) ? "y" : "n")}-{await S(null) ?? "none"}");
        Console.WriteLine(Sum(s_calls > 0 ? 1 : 0, await N(2), await B(false) ? 9 : await N(3)));
        var box = new Box { Name = await S(null) ?? "init", Next = await B(true) ? new Box { Name = await S("next") } : null };
        Console.WriteLine(box.Name + " " + box.Next?.Name);
        var list = new List<int> { await N(1), await B(true) ? await N(2) : 0, 3 };
        Console.WriteLine(string.Join(",", list));
        // conditions of loops
        int k = 0;
        while (await N(k) < 3 && await B(true)) k++;
        do { k--; } while (k > 0 && await B(k > 1));
        Console.WriteLine(k);
        // a conditional inside a try with an await in the finally
        try { Console.WriteLine(await B(false) ? "no" : await S("in try")); }
        finally { Console.WriteLine(await B(true) ? await S("finally") : "no"); }
        // assignments whose target is evaluated before the await: compound, ??=, ++, on locals, fields, elements
        int total = 1; total += await N(2); total *= await B(true) ? await N(3) : 1;
        var arr = new int[3]; arr[await N(1)] += await N(5); arr[await N(2)]++; arr[0] -= await N(4);
        var fb = new Box { Name = null }; fb.Name ??= await S("named"); fb.Name += await S("!"); fb.Next ??= await Maybe(true);
        string[] names = new string[2]; names[await N(1)] ??= await S("slot"); names[1] ??= await S("kept");
        Console.WriteLine(total + " " + string.Join(",", arr) + " " + fb.Name + " " + fb.Next.Name + " " + names[1] + (names[0] == null));
        // lambdas
        Func<bool, Task<int>> pick = async x => x ? await N(10) : await N(20) + (await B(true) ? 1 : 0);
        Console.WriteLine(await pick(true) + " " + await pick(false));
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main() => Run().GetAwaiter().GetResult();
}
