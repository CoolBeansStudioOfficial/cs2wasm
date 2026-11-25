// Array.ConstrainedCopy throws ArrayTypeMismatchException, before anything is copied, when the element types are not
// provably compatible (Array.Copy would fail part-way with InvalidCastException); a covariant copy still works.
using System;

static class Program
{
    static void Main()
    {
        Array source = new object[] { "valid", new object() };
        Array destination = new[] { "left", "right" };
        try
        {
            Array.ConstrainedCopy(source, 0, destination, 0, 2);
            Console.WriteLine("no exception");
        }
        catch (ArrayTypeMismatchException)
        {
            Console.WriteLine("mismatch");
        }
        catch (Exception e)
        {
            Console.WriteLine("other: " + e.GetType().Name);
        }
        var values = (string[])destination;
        Console.WriteLine("unchanged: " + values[0] + " " + values[1]);

        Array covariantSource = new[] { "one", "two" };
        Array covariantDestination = new object[] { "x", "y" };
        Array.ConstrainedCopy(covariantSource, 0, covariantDestination, 0, 2);
        var copied = (object[])covariantDestination;
        Console.WriteLine("covariant: " + copied[0] + " " + copied[1]);
    }
}
