using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // strings built at run time compare by value (the predefined string == is value equality)
        var x = " Read".Trim();
        string y = string.Concat("Re", "ad"), n = null;
        Console.WriteLine((x == "Read") + " " + (y == x) + " " + (x != "read") + " " + (n == null) + " " + (null == n) + " " + (x == n));
        var parts = "a,b,a".Split(',');
        Console.WriteLine((parts[0] == parts[2]) + " " + ((object)parts[0] == (object)parts[2]));
        var set = new List<string> { new string('z', 2), "q" };
        Console.WriteLine(set.Contains("zz") + " " + (set[0] == "zz"));
        switch (y) { case "Read": Console.WriteLine("switch ok"); break; default: Console.WriteLine("switch bad"); break; }
    }
}
