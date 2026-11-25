// Array.Copy between reference arrays whose element types are unrelated (neither assignable to the other, no interface)
// throws ArrayTypeMismatchException even when there is nothing to copy; a downcast between related types is checked per
// element, so an empty one is allowed.
using System;

sealed class Item { }

static class Program
{
    static string Try(Array from, Array to, int n)
    {
        try { Array.Copy(from, to, n); return "ok"; }
        catch (ArrayTypeMismatchException) { return "ATME"; }
        catch (InvalidCastException) { return "ICE"; }
    }

    static void Main()
    {
        Console.WriteLine("unrelated empty: " + Try(Array.Empty<string>(), Array.Empty<Item>(), 0));
        Console.WriteLine("unrelated one: " + Try(new string[1], new Item[1], 1));
        Console.WriteLine("downcast empty: " + Try(Array.Empty<object>(), Array.Empty<string>(), 0));
        Console.WriteLine("downcast one: " + Try(new object[] { "x" }, new string[1], 1));
        Console.WriteLine("upcast empty: " + Try(Array.Empty<string>(), Array.Empty<object>(), 0));
        Console.WriteLine("same empty: " + Try(Array.Empty<string>(), Array.Empty<string>(), 0));
        Console.WriteLine("interface empty: " + Try(Array.Empty<string>(), Array.Empty<IDisposable>(), 0));
    }
}
