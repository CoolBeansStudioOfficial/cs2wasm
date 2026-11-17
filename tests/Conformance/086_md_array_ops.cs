// Arrays of more dimensions as a whole: Array.Copy, Array.Clear, Clone, GetValue/SetValue, Buffer.BlockCopy (their
// elements start after the dimensions' lengths).
using System;
using System.Runtime.InteropServices;
sealed class P { public int V; public P(int v) { V = v; } }
static class Program
{
    static void T(string name, Action a)
    {
        try { a(); } catch (Exception e) { Console.WriteLine(name + " threw " + e.GetType().Name + ": " + e.Message); }
    }
    static string Dump(P[,] m) { var s = ""; foreach (var p in m) s += p == null ? "- " : p.V + " "; return s + "[" + m.GetLength(0) + "," + m.GetLength(1) + "]"; }
    static string DumpI(int[,] m) { var s = ""; foreach (var p in m) s += p + " "; return s + "[" + m.GetLength(0) + "," + m.GetLength(1) + "]"; }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var m = new P[3, 3]; for (int i = 0; i < 3; i++) for (int k = 0; k < 3; k++) m[i, k] = new P(i * 3 + k);
        var n = new int[2, 4]; for (int i = 0; i < 2; i++) for (int k = 0; k < 4; k++) n[i, k] = i * 4 + k;
        T("clone", () => { var c = (P[,])m.Clone(); Console.WriteLine(Dump(c)); var d = (int[,])n.Clone(); Console.WriteLine(DumpI(d)); });
        T("copy", () => { var c = new P[3, 3]; Array.Copy(m, c, 9); Console.WriteLine(Dump(c)); Array.Copy(m, 0, c, 4, 3); Console.WriteLine(Dump(c)); var d = new int[2, 4]; Array.Copy(n, 2, d, 1, 5); Console.WriteLine(DumpI(d)); });
        T("copy flat", () => { var f = new int[8]; Array.Copy(n, f, 8); Console.WriteLine(string.Join(" ", f)); var d = new int[2, 4]; Array.Copy(f, 1, d, 0, 6); Console.WriteLine(DumpI(d)); var pf = new P[9]; Array.Copy(m, pf, 9); Console.WriteLine(pf[8].V); });
        T("clear", () => { var c = (P[,])m.Clone(); Array.Clear(c, 2, 4); Console.WriteLine(Dump(c)); var d = (int[,])n.Clone(); Array.Clear(d, 1, 3); Console.WriteLine(DumpI(d)); Array.Clear(d); Console.WriteLine(DumpI(d)); });
        T("misc", () => { Console.WriteLine(m.Rank + " " + m.Length + " " + m.GetUpperBound(1) + " " + ((P)m.GetValue(1, 2)!).V); m.SetValue(new P(99), 2, 0); Console.WriteLine(Dump(m)); var t = new double[2, 2, 2]; t[1, 1, 1] = 2.5; Console.WriteLine(t[1, 1, 1] + " " + t.Length); Buffer.BlockCopy(n, 0, n, 4, 8); Console.WriteLine(DumpI(n)); });
    }
}
