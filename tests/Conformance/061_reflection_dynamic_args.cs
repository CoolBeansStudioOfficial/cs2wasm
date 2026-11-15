using System;
using System.Reflection;
using System.Runtime.InteropServices;

public struct Cell { public int V; public override string ToString() => "Cell" + V; }
public class Node { public string Name = "n"; }

static class Program
{
    public static string Show<T>(T v) => typeof(T).Name + ":" + v;
    public static int Count<T>(T[] items) => items.Length;

    static Type Pick(int i) => i switch { 0 => typeof(int), 1 => typeof(string), _ => typeof(Cell) };

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // instances the program makes anyway
        Console.WriteLine(Show(1) + " " + Show("a") + " " + Show(new Cell { V = 2 }));
        var show = typeof(Program).GetMethod("Show")!;
        for (int i = 0; i < 3; i++)
        {
            var t = Pick(i);
            object arg = i == 0 ? 7 : i == 1 ? "seven" : new Cell { V = 7 };
            Console.WriteLine(show.MakeGenericMethod(t).Invoke(null, [arg]));
        }
        var cells = Array.CreateInstance(typeof(Cell), 3);
        cells.SetValue(new Cell { V = 5 }, 1);
        Console.WriteLine(cells.GetType() + " " + cells.Length + " " + cells.GetValue(1) + " " + cells.GetValue(0));
        var nodes = (Node[])Array.CreateInstance(typeof(Node), 2);
        nodes[0] = new Node { Name = "first" };
        Console.WriteLine(nodes[0].Name + " " + (nodes[1] == null));
        var at = typeof(Node).MakeArrayType();
        Console.WriteLine(at + " " + at.IsArray + " " + (at.GetElementType() == typeof(Node)));
        var made = (Node[])Activator.CreateInstance(at, 4)!;
        Console.WriteLine(made.Length);
    }
}
