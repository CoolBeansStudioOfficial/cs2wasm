// Array.SetValue stores a primitive into an array of another primitive type when the CLR widens it (int into long[],
// char into double[], byte into char[]), converting it exactly; a primitive that does not widen into one is an
// ArgumentException, and a non-primitive into a reference array is an InvalidCastException.
using System;

static class Program
{
    static string Try(Action action)
    {
        try { action(); return "ok"; }
        catch (Exception e) { return e.GetType().Name; }
    }

    static void Main()
    {
        Array a = new long[1];
        a.SetValue(5, 0);
        Console.WriteLine("int into long: " + ((long[])a)[0]);

        Array c = new double[1];
        c.SetValue('A', 0);
        Console.WriteLine("char into double: " + ((double[])c)[0]);

        Array f = new float[1];
        f.SetValue(ulong.MaxValue, 0);
        Console.WriteLine("ulong into float: " + BitConverter.SingleToInt32Bits(((float[])f)[0]));

        Array nums = new ulong[1];
        nums.SetValue((byte)200, 0);
        Console.WriteLine("byte into ulong: " + ((ulong[])nums)[0]);

        Array sh = new short[1];
        sh.SetValue((sbyte)-3, 0);
        Console.WriteLine("sbyte into short: " + ((short[])sh)[0]);

        Array ch = new char[1];
        ch.SetValue((byte)65, 0);
        Console.WriteLine("byte into char: " + ((char[])ch)[0]);

        Array u = new uint[1];
        Console.WriteLine("short into uint: " + Try(() => u.SetValue((short)-1, 0)));
        Array i = new int[1];
        Console.WriteLine("long into int: " + Try(() => i.SetValue(5L, 0)));
        Array b = new bool[1];
        Console.WriteLine("int into bool: " + Try(() => b.SetValue(1, 0)));
        Array s = new string[1];
        Console.WriteLine("int into string: " + Try(() => s.SetValue(1, 0)));
        Array o = new object[1];
        o.SetValue(5, 0);
        Console.WriteLine("int into object: " + o.GetValue(0));
    }
}
