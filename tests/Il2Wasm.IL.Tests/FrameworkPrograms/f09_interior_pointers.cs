// Addresses into objects used after the object's own last use: a box's data, an element of an array nothing else
// holds, a field of a new object, a span over a new array. The object must live while the address is used.
using System;
using System.Runtime.InteropServices;

struct P { public int X; public string S; }
class Holder { public int V = 3; public long W = 9; }

static class Program
{
    static ref int First(int[] a) => ref a[0];
    static ref int FieldOf(Holder h) => ref h.V;

    static void Main()
    {
        long total = 0;
        for (int i = 0; i < 3000; i++)
        {
            object boxed = new P { X = i, S = "s" + i };
            total += ((P)boxed).X;
            ref int e = ref new int[] { i, 2, 3 }[1];
            string junk = new string('j', 40);
            total += e + junk.Length;
            ref int f = ref FieldOf(new Holder());
            var junk2 = new Holder { V = 100 };
            total += f + junk2.V;
            ref int g = ref First(new int[] { 7, 8 });
            var junk3 = new int[] { 50, 50 };
            total += g + junk3[0];
            Span<int> sp = new int[] { 1, 2, 3, i };
            var junk4 = new int[] { 0, 0, 0, 0 };
            total += sp[3] + sp.Length + junk4.Length;
            var md = new int[2, 2];
            ref int m = ref md[1, 1];
            m = 5;
            var junk5 = new int[2, 2];
            total += m + junk5[1, 1];
        }
        Console.WriteLine(total);
        var arr = new P[] { new P { X = 1, S = "a" } };
        ref P rp = ref MemoryMarshal.GetArrayDataReference(arr);
        arr = null;
        var junk6 = new P[1];
        Console.WriteLine(rp.X + rp.S + junk6.Length);
    }
}
