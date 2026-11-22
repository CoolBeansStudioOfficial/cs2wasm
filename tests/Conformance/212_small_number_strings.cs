// The strings of small numbers: .NET makes the string of each number from 0 to 299 once and gives the same object
// every time after (int, uint, long, ulong and the smaller types through them; in a concatenation and an
// interpolation's hole too), and a new string for every other number, negative ones and 300 included. Which it is
// can be asked (ReferenceEquals), so it is the same here; the texts are as ever.
using System;
using System.Runtime.InteropServices;
using System.Globalization;

static class Program
{
    static string Twice(Func<string> make) => ReferenceEquals(make(), make()) ? "same" : "new";

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        int same = 0, fresh = 0;
        for (int i = -20; i < 400; i++)
        {
            int v = i;
            if (ReferenceEquals(v.ToString(), v.ToString())) same++; else fresh++;
        }
        Console.WriteLine($"int -20..399: {same} the same object, {fresh} new");
        foreach (int v in new[] { 0, 1, 9, 10, 11, 63, 99, 100, 255, 256, 299, 300, 301, 1000, -1, -9, int.MaxValue, int.MinValue })
        {
            int i = v; uint u = (uint)v; long l = v; ulong ul = (ulong)(long)v;
            Console.WriteLine($"{i}: int {Twice(() => i.ToString())}, uint {Twice(() => u.ToString())} ({u}), long {Twice(() => l.ToString())}, ulong {Twice(() => ul.ToString())} ({ul})");
        }
        byte b = 255; sbyte sb = 127; short sh = 299; ushort us = 300; char ch = (char)65; nint ni = 12; nuint nu = 299;
        Console.WriteLine($"byte {Twice(() => b.ToString())}, sbyte {Twice(() => sb.ToString())}, short {Twice(() => sh.ToString())}, ushort {Twice(() => us.ToString())}, nint {Twice(() => ni.ToString())}, nuint {Twice(() => nu.ToString())}, char {Twice(() => ch.ToString())}");
        int n = 42, big = 4242;
        Console.WriteLine($"with a culture {Twice(() => n.ToString(CultureInfo.InvariantCulture))}, format D {Twice(() => n.ToString("D"))}, format null {Twice(() => n.ToString((string?)null))}, G {Twice(() => n.ToString("G", null))}, D5 {Twice(() => n.ToString("D5"))}, X {Twice(() => n.ToString("X"))}");
        Console.WriteLine($"across types {(ReferenceEquals(n.ToString(), ((long)n).ToString()) ? "same" : "new")}, {(ReferenceEquals(n.ToString(), ((byte)n).ToString()) ? "same" : "new")}, {(ReferenceEquals(big.ToString(), ((long)big).ToString()) ? "same" : "new")}");
        Console.WriteLine($"empty and a number {(ReferenceEquals("" + n, n.ToString()) ? "same" : "new")}, {(ReferenceEquals("" + big, big.ToString()) ? "same" : "new")}, {(ReferenceEquals(string.Concat(n), n.ToString()) ? "same" : "new")}");
        object o = n;
        Console.WriteLine($"boxed {(ReferenceEquals(o.ToString(), n.ToString()) ? "same" : "new")}, Convert {(ReferenceEquals(Convert.ToString(n), n.ToString()) ? "same" : "new")}, text {n} {big} {-n} {299} {300}");
        // the texts, every one
        long sum = 0; int wrong = 0;
        for (int i = 0; i < 1200; i++) { string s = i.ToString(); sum += s.Length; if (int.Parse(s) != i || s != ((uint)i).ToString() || s != ((long)i).ToString()) wrong++; }
        Console.WriteLine($"0..1199: {sum} characters, {wrong} wrong");
    }
}
