// A store of a non-string into an array the program made as string[] but holds as object[]: ArrayTypeMismatchException,
// even when the compiler knows the array's exact type (the element check was skipped for the refined node type).
using System;
using System.Runtime.CompilerServices;

public sealed class Box { public int Value; }

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Store(object[] values, int round) { values[0] = new Box { Value = round }; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Local(int round)
    {
        object[] values = new string[1];
        values[0] = new Box { Value = round };
        return 1;
    }

    static string Describe(Action a)
    {
        try { a(); return "no throw"; }
        catch (ArrayTypeMismatchException) { return "ArrayTypeMismatch"; }
    }

    static void Main()
    {
        Console.WriteLine("local: " + Describe(() => Local(1)));
        Console.WriteLine("param: " + Describe(() => Store(new string[1], 2)));
        object[] v = new string[1];
        Console.WriteLine("direct: " + Describe(() => v[0] = new Box()));
        object[] ok = new object[1];
        Console.WriteLine("object array: " + Describe(() => ok[0] = new Box()));
        object[] strings = new string[1];
        strings[0] = "fine";
        Console.WriteLine("string value: " + strings[0]);
    }
}
