// The smallest program, for timing a toolchain run (tests/Godot/proof5_hello2d/README.md).
using System;
using System.Runtime.InteropServices;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main() => Console.WriteLine(42);
}
