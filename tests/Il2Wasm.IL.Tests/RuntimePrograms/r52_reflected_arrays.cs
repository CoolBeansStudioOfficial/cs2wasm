// Arrays made at run time of a type the code names (Array.CreateInstance, Type.MakeArrayType), of element types the
// program never makes arrays of in its own code (IL2WASM_NO_REFLECTED_ARRAYS=1 breaks it).
using System;

sealed class Foo { public int V; public override string ToString() => "Foo " + V; }
struct Pt { public int X, Y; }

static class Program
{
    static void Main()
    {
        var a = Array.CreateInstance(typeof(Foo), 3);
        a.SetValue(new Foo { V = 7 }, 1);
        Console.WriteLine(a.GetType().FullName + " " + a.Length + " " + a.GetValue(1) + " " + (a.GetValue(0) == null));
        var p = Array.CreateInstance(typeof(Pt), 2);
        Console.WriteLine(p.GetType().Name + " " + p.Length);
        var t = typeof(Guid).MakeArrayType();
        Console.WriteLine(t.FullName + " " + t.GetElementType()!.Name);
    }
}
