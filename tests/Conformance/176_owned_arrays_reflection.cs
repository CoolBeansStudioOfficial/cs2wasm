// Owned arrays (test 175) and reflection: a constructor reflection can call is given whatever array the caller has,
// here one written after it is handed over, closing a cycle; so the field stays an edge that closes cycles.
// cycles: RHolder, RItem
using System;
using System.Runtime.InteropServices;

class RItem { public RHolder? Back; }
class RHolder
{
    readonly RItem[] items;
    public RHolder(RItem[] items) { this.items = items; }
    public int Count => items.Length;
    // the shape that alone would be sealed
    public static RHolder Make(RItem a)
    {
        var t = new RItem[1];
        t[0] = a;
        return new RHolder(t);
    }
}

static class P
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var plain = RHolder.Make(new RItem());
        var arr = new RItem[1];
        var made = (RHolder)Activator.CreateInstance(typeof(RHolder), new object[] { arr })!;
        arr[0] = new RItem { Back = made };
        Console.WriteLine(plain.Count + " " + made.Count + " " + (arr[0].Back == made));
        long total = 0;
        for (int i = 0; i < 300; i++)
        {
            var a = new RItem[1];
            var h = (RHolder)Activator.CreateInstance(typeof(RHolder), new object[] { a })!;
            a[0] = new RItem { Back = h };
            total += h.Count;
        }
        Console.WriteLine(total);
    }
}
