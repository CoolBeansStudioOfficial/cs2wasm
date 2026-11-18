// string.Empty read by the program's own code (section 3): the empty string, the same object as "".
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

static class Program
{
    static readonly string s_field = string.Empty;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string Pick(bool b) => b ? string.Empty : "x";

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(string.Empty == null);
        Console.WriteLine(string.Empty.Length + " " + Pick(true).Length + " " + Pick(false).Length + " " + s_field.Length);
        Console.WriteLine(ReferenceEquals(string.Empty, "") + " " + ReferenceEquals(String.Empty, s_field) + " " + (string.Empty == ""));
        string s = String.Empty;
        s += "a";
        Console.WriteLine(s + "[" + string.Empty + "]" + string.IsNullOrEmpty(string.Empty) + string.Concat(string.Empty, string.Empty).Length);
        object o = string.Empty;
        Console.WriteLine(o is string { Length: 0 });
    }
}
