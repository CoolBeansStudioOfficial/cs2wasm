// Roslyn's parser over 400 generated classes (doc comments, async, switch expressions), parsed and walked four times,
// best time printed: a baseline-tier proxy for the self-hosted compiler's library parse that builds in a minute.
//   cs2wasmc tools/roslyn-parse-bench.cs -r <bin>/Microsoft.CodeAnalysis.dll -r <bin>/Microsoft.CodeAnalysis.CSharp.dll -o pb.wasm
//   node --liftoff-only host/run.mjs pb.wasm main     (CS2WASM_NO_THREADS=1 when compiling: no threads, as NativeAOT-LLVM)
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
static class P
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 400; i++)
            sb.Append("/// <summary>doc ").Append(i).Append("</summary>\nclass C").Append(i)
              .Append(" : System.IDisposable { int _f; public int F(int x) => x + _f * 2; public void Dispose() { if (_f > 0) { _f--; } else { throw new System.Exception(\"x\" + _f); } }\n")
              .Append(" static string G(string s, int n) { var b = new System.Text.StringBuilder(); for (int k = 0; k < n; k++) { b.Append(s[k % s.Length]); } return b.ToString(); }\n")
              .Append(" public async System.Threading.Tasks.Task<int> H(int[] a) { int t = 0; foreach (var v in a) t += v switch { 0 => 1, < 10 => v, _ => -v }; await System.Threading.Tasks.Task.Yield(); return t; } }\n");
        var text = sb.ToString();
        int best = int.MaxValue, nodes = 0;
        for (int r = 0; r < 4; r++)
        {
            var sw = Stopwatch.StartNew();
            var t = CSharpSyntaxTree.ParseText(text);
            nodes = t.GetRoot().DescendantNodesAndTokens().Count();
            best = Math.Min(best, (int)sw.ElapsedMilliseconds);
        }
        Console.WriteLine("parse " + best + " ms, " + nodes + " nodes and tokens");
    }
}
