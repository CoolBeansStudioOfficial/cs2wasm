// checked
// A box of a Nullable<T> without a value is null, not a new object: the constant propagation must not take a box for
// non-null (Reach/Sccp.cs; fixture:rtti.boxed-representation). Needs il-codegen's Nullable box lowering.
using System;

static class Program
{
    static bool IsNull<T>(T? v) where T : struct { object? boxed = v; return boxed is null; }

    static void Main()
    {
        int? none = null;
        object? boxedNone = none;
        Console.WriteLine("boxed empty nullable is null: " + (boxedNone == null) + ", boxed 5: " + (object)(int?)5);
        Console.WriteLine("generic: " + IsNull<long>(null) + " " + IsNull<long>(3));
    }
}
