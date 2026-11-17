// Array.Copy between arrays of different primitive element types: into a wider type, or the type of the same size
// and the other sign, as the CLR allows; anything else is an ArrayTypeMismatchException. Enum arrays go by their
// underlying type (Enum.GetValues(Type) copies its values that way).
using System;
using System.Runtime.InteropServices;

enum Small : byte { A = 1, B = 200 }
enum Wide : long { A = -5, B = 1L << 40 }

static class Program
{
    static void T(string name, Array src, Array dst)
    {
        try
        {
            Array.Copy(src, dst, src.Length);
            var s = name + ":";
            foreach (var x in dst) s += " " + Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture);
            Console.WriteLine(s);
        }
        catch (Exception e) { Console.WriteLine(name + " threw " + e.GetType().Name); }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        T("int->long", new[] { -1, 2, int.MaxValue }, new long[3]);
        T("int->uint", new[] { -1, 2 }, new uint[2]);
        T("uint->int", new[] { uint.MaxValue, 2u }, new int[2]);
        T("int->double", new[] { -1, 2 }, new double[2]);
        T("int->float", new[] { 16777217, 2 }, new float[2]);
        T("int->short", new[] { 1, 2 }, new short[2]);
        T("long->int", new[] { 1L, 2L }, new int[2]);
        T("byte->char", new byte[] { 65, 66 }, new char[2]);
        T("char->int", new[] { 'a', 'b' }, new int[2]);
        T("char->short", new[] { 'a', 'b' }, new short[2]);
        T("ushort->char", new ushort[] { 97, 98 }, new char[2]);
        T("sbyte->short", new sbyte[] { -1, 2 }, new short[2]);
        T("sbyte->ushort", new sbyte[] { -1, 2 }, new ushort[2]);
        T("sbyte->byte", new sbyte[] { -1, 2 }, new byte[2]);
        T("short->ushort", new short[] { -1, 2 }, new ushort[2]);
        T("ulong->double", new[] { ulong.MaxValue, 2ul }, new double[2]);
        T("ulong->long", new[] { ulong.MaxValue, 2ul }, new long[2]);
        T("float->double", new[] { 1.5f, -0.25f }, new double[2]);
        T("double->float", new[] { 1.5, 2.5 }, new float[2]);
        T("bool->byte", new[] { true, false }, new byte[2]);
        T("byte->bool", new byte[] { 1, 0 }, new bool[2]);
        T("int->decimal", new[] { 1, 2 }, new decimal[2]);
        T("byte->Small", new byte[] { 1, 200 }, new Small[2]);
        T("Small->byte", new[] { Small.A, Small.B }, new byte[2]);
        T("Small->int", new[] { Small.A, Small.B }, new int[2]);
        T("int->Wide", new[] { -5, 7 }, new Wide[2]);
        T("Wide->long", new[] { Wide.A, Wide.B }, new long[2]);
        T("int->object", new[] { 1, 2 }, new object[2]);
        T("object->int", new object[] { 1, 2 }, new int[2]);
        T("object->long", new object[] { 1, 2 }, new long[2]);
        var days = Enum.GetValues(typeof(DayOfWeek));
        Console.WriteLine(days.Length + " " + days.GetType().Name + " " + days.GetValue(6));
        Console.WriteLine(string.Join(",", typeof(Small).GetEnumValues().GetValue(1), Enum.GetValuesAsUnderlyingType(typeof(Wide)).GetValue(0)));
    }
}
