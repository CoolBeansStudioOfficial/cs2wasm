using System;
using System.Runtime.InteropServices;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine("Hello from cs2wasm");
        int x = 6 * 7;
        Console.WriteLine(x);
        Console.WriteLine("sum: " + (x + 8));
        for (int i = 0; i < 3; i++) Console.Write(i);
        Console.WriteLine();
    }
}
