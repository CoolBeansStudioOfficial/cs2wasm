// Array.Copy (and Array.GetValue, which the runtime boxes the same way) boxes an element of a value type that the program
// never boxes itself: the box's type must still be a constructed type, so that an unbox or a cast to the element type
// finds it (PriorityQueue's ICollection.CopyTo does this, and its tuples were refused: InvalidCastException).
using System;
using System.Collections;
using System.Collections.Generic;

static class Program
{
    static void Main()
    {
        var src = new (int, int)[2];
        src[0] = (3, 4);
        object[] d = new object[2];
        Array.Copy(src, 0, d, 0, 1);
        var a = (ValueTuple<int, int>)d[0];
        Console.WriteLine("copied: " + a.Item1 + " " + a.Item2);

        var named = new (int Element, int Priority)[2];
        named[0] = (5, 6);
        object[] e = new object[2];
        Array.Copy(named, 0, e, 0, 1);
        var c = ((int Element, int Priority))e[0];
        Console.WriteLine("named: " + c.Element + " " + c.Priority);

        var queue = new PriorityQueue<int, int>();
        queue.Enqueue(10, 1);
        ICollection items = queue.UnorderedItems;
        var copy = new object[2];
        items.CopyTo(copy, 0);
        var q = ((int Element, int Priority))copy[0];
        Console.WriteLine("queue: " + q.Element + " " + q.Priority);

        var ints = new int[] { 7 };
        object[] boxed = new object[1];
        Array.Copy(ints, boxed, 1);
        Console.WriteLine("int: " + (int)boxed[0]);
    }
}
