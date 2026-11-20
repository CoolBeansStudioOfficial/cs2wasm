// size-limit: 28383
// Console.WriteLine(42) as shipped stays below NetWasm's 28,383 bytes (the size goal: a module with no collector is
// smaller than one with its own). The runner fails the program when its module reaches the limit.
using System;
using System.Runtime.InteropServices;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main() => Console.WriteLine(42);
}
