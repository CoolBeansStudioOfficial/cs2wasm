// TypedReference.ToObject: the referenced value as an object, as an array element is boxed (Array.GetValue): a reference
// as it is, a value type boxed with its own type. __makeref of a field, an array element and a local, read back.
using System;

enum Color : short { Red = 1 }

class Box
{
    public string Text = "box";
    public int Number = 5;
    public int[] Items = new int[3];
}

static class Program
{
    static void Main()
    {
        var box = new Box();
        TypedReference text = __makeref(box.Text);
        object t = TypedReference.ToObject(text)!;
        Console.WriteLine("string: " + (t is string s ? s : "?") + " " + (__reftype(text) == typeof(string)));

        TypedReference number = __makeref(box.Number);
        __refvalue(number, int) = 9;
        object n = TypedReference.ToObject(number)!;
        Console.WriteLine("int: " + n + " " + n.GetType().Name + " " + box.Number);

        TypedReference element = __makeref(box.Items[2]);
        __refvalue(element, int) = 7;
        Console.WriteLine("element: " + TypedReference.ToObject(element) + " " + box.Items[2]);

        int local = 3;
        TypedReference color = __makeref(local);
        __refvalue(color, int) = 42;
        Console.WriteLine("local: " + TypedReference.ToObject(color) + " " + local);

        Color c = Color.Red;
        TypedReference cref = __makeref(c);
        object boxed = TypedReference.ToObject(cref)!;
        Console.WriteLine("enum: " + boxed + " " + boxed.GetType().Name);
        Console.WriteLine("equal: " + boxed.Equals(Color.Red));
    }
}
