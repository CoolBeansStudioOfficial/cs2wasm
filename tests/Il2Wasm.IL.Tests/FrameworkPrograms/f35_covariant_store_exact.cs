// A reference stored into an array through an object[] (ECMA-335 stelem.ref) is checked against the array's runtime
// element type, even when the array was allocated in the same method, so the local's exact type is known: an int
// stored into a string[] reached through an object[] local throws ArrayTypeMismatchException, and so does an object.
// A string stored into a string[] does not. A sealed element type's store of its own type needs no check.
using System;
using System.Runtime.CompilerServices;

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Put(object[] arr, object v)
    {
        try { arr[0] = v; return 0; }
        catch (ArrayTypeMismatchException) { return 7; }
    }

    static void Main()
    {
        object[] viaParameter = new string[] { "a", "b" };
        Console.WriteLine("parameter: " + Put(viaParameter, 5) + " " + Put(viaParameter, new object()) + " " + Put(viaParameter, "x"));

        object[] local = new string[] { "a", "b" };
        int r = 0;
        try { local[0] = 5; } catch (ArrayTypeMismatchException) { r = 7; }
        Console.WriteLine("local int: " + r);

        object[] other = new string[] { "a", "b" };
        int s = 0;
        try { other[1] = new object(); } catch (ArrayTypeMismatchException) { s = 9; }
        Console.WriteLine("local object: " + s + " " + other[1]);

        object[] ok = new string[] { "a", "b" };
        ok[0] = "fine";
        Console.WriteLine("string: " + ok[0]);

        object[] objects = new object[] { "a" };
        objects[0] = 5;
        Console.WriteLine("object[]: " + objects[0]);
    }
}
