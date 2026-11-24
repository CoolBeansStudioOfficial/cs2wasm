// string.Empty (an [Intrinsic] field no initializer sets: the runtime's empty string) and what returns it.
using System;

static unsafe class Program
{
    static void Main()
    {
        Console.WriteLine(string.Empty.Length + " " + ReferenceEquals(string.Empty, "") + " " + (string.Empty == null));
        var a = new string((char*)null);
        var b = new string(new char[0]);
        var c = "abc".Substring(3);
        Console.WriteLine(a.Length + b.Length + c.Length + " " + ReferenceEquals(a, string.Empty) + ReferenceEquals(c, ""));
        Console.WriteLine("[" + string.Concat(string.Empty, "x", string.Empty) + "]" + string.IsNullOrEmpty(string.Empty));
    }
}
