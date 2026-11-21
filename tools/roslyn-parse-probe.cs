using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis.CSharp;
static class P
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var t = CSharpSyntaxTree.ParseText("class C { int F(int x) => x + 1; void G() { var f = static async () => 1; } }");
        Console.WriteLine(t.GetRoot().DescendantNodes().Count());
    }
}
