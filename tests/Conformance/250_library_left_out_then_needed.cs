// A library that the compiler leaves out at the start and then needs. The C# compiler reports a reference as used
// also when only a candidate of a call came from it, so cs2wasm takes such a library at the start only when the text
// has a word that names it (a type of it, or a namespace only it has), and compiles again with every reported library
// when it meets a type of one it left out (UpstreamSources.Needed, Library.Lookup). Here nothing names the library
// of the queries over expression trees: its only use is a call written as an extension whose name is no type's, on
// a value whose type is declared in another library.
// CS2WASM_BREAK_LIB_RESTART=1 (nothing is compiled again): the calls throw PlatformNotSupportedException;
// CS2WASM_NO_LIB_RESTART=1 (every reported library from the start) builds the same module.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

static class Program
{
    static IEnumerable<int> Numbers()
    {
        for (int i = 1; i <= 8; i++) yield return i * 3 % 7;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // an extension call whose candidates come from several libraries; the one that binds is in a library that
        // no word here names
        var q = Numbers().AsQueryable();
        var odd = q.Where(x => x % 2 == 1).OrderBy(x => x).Select(x => x * 10);
        foreach (var x in odd) Console.Write(x + " ");
        Console.WriteLine();
        Console.WriteLine(q.Count(x => x > 2) + " " + q.Any(x => x == 6) + " " + q.First(x => x > 4));
        Console.WriteLine(odd.Expression.NodeType + " " + odd.ElementType.Name);
        // and the plain sequence operators beside it, which bind in the library the namespace names
        Console.WriteLine(string.Join(",", Numbers().Where(x => x > 3).OrderByDescending(x => x)));
    }
}
