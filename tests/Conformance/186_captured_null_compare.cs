// A local initialized to null and compared with null inside a local function or lambda that captures it, while the
// enclosing method or another local function assigns it. The comparison is folded only for a local nothing assigns
// (Folding.cs, KnownNull): that was looked for in the body being compiled alone, so a local function saw the
// enclosing method's local as never written and `filter == null` as always true. The self-hosted compiler's own
// Sharing analysis is such code (its node keys built in a local function), and found 399 types another thread can
// reach in a GodotSharp program where the .NET-hosted compiler found 1,861.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

static class Program
{
    static void Direct()
    {
        object? filter = null;
        string FK(string k) => filter == null ? k : k + " |" + filter;
        bool IsNull() => filter == null;
        bool NotNull() => filter != null;
        bool IsPattern() => filter is null;
        bool NotPattern() => filter is not null;
        void Set(object? v) => filter = v;
        Console.WriteLine($"{FK("k")} {IsNull()} {NotNull()} {IsPattern()} {NotPattern()}");
        filter = "direct";
        Console.WriteLine($"{FK("k")} {IsNull()} {NotNull()} {IsPattern()} {NotPattern()}");
        Set("viaSet");
        Console.WriteLine($"{FK("k")} {IsNull()} {NotNull()} {IsPattern()} {NotPattern()}");
        Set(null);
        Console.WriteLine($"{FK("k")} {IsNull()} {NotNull()} {IsPattern()} {NotPattern()}");
    }

    static void Lambdas()
    {
        string? s = null;
        Func<bool> isNull = () => s == null;
        Action<string?> set = v => s = v;
        Console.WriteLine(isNull());
        set("x");
        Console.WriteLine(isNull());
        // nested: a lambda in a local function, the write in another lambda
        string? t = null;
        Func<string> Outer() => () => t == null ? "null" : t;
        var write = () => { t = "nested"; };
        Console.WriteLine(Outer()());
        write();
        Console.WriteLine(Outer()());
    }

    // the shape of the self-hosted compiler's Sharing.Analyze: a key built by a local function from a captured filter
    // that a stack walk and a try/finally restore change
    static string Walk()
    {
        var seen = new HashSet<string>();
        var log = new List<string>();
        object? filter = null;
        string FK(string k) => filter == null ? k : k + " |" + filter;
        bool Visit(string key) => filter == null || seen.Add(FK(key));
        void WithFilter(object t, Action a)
        {
            var saved = filter;
            filter = t;
            try { a(); } finally { filter = saved; }
        }
        foreach (var f in new object?[] { "A", "B", null, "A" })
        {
            filter = f;
            log.Add("n1:" + Visit("n1"));
            WithFilter("X" + f, () => log.Add("n2:" + Visit("n2")));
        }
        return string.Join(" ", log) + " seen " + seen.Count;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Direct();
        Lambdas();
        Console.WriteLine(Walk());
    }
}
