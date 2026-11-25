// Multi-dimensional arrays of reference elements are covariant as vectors are: a store of a value the array's own element
// type does not take throws ArrayTypeMismatchException and leaves the element as it was, and an address of an element of
// an array of another type is refused the same way. An exact-type array takes any of its element type.
using System;

static class Program
{
    static ref object Address(object[,] values) => ref values[0, 0];

    static string Try(Action action)
    {
        try { action(); return "ok"; }
        catch (ArrayTypeMismatchException) { return "ATME"; }
    }

    static void Main()
    {
        var original = new string[,] { { "unchanged" } };
        object[,] view = original;
        Console.WriteLine("store object: " + Try(() => view[0, 0] = new object()) + " " + original[0, 0]);
        Console.WriteLine("store string: " + Try(() => view[0, 0] = "changed") + " " + original[0, 0]);
        Console.WriteLine("read: " + (string)view[0, 0]);

        object[,] exact = new object[1, 1];
        Console.WriteLine("exact store: " + Try(() => exact[0, 0] = new object()) + " " + Try(() => exact[0, 0] = "s"));
        Console.WriteLine("exact address: " + Try(() => Address(exact) = "x") + " " + exact[0, 0]);

        Console.WriteLine("address: " + Try(() => Address(view) = new object()) + " " + original[0, 0]);
        Console.WriteLine("address same: " + Try(() => Address(exact) = new object()));
    }
}
