using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Game
{
    class Enemy { }
    struct Hit { public int Damage; }
    enum Kind { A, B }

    static class Program
    {
        static string Describe<T>(T value) => typeof(T).Name + ":" + (value?.GetType().Name ?? "null");

        [UnmanagedCallersOnly(EntryPoint = "main")]
        static void Main()
        {
            Console.WriteLine(typeof(Enemy).Name + " " + typeof(Enemy).FullName + " " + typeof(Enemy).Namespace);
            Console.WriteLine(new Enemy().GetType() == typeof(Enemy));
            Console.WriteLine(typeof(int).Name + " " + typeof(string).FullName + " " + typeof(int[]).Name + " " + typeof(List<int>).Name);
            Console.WriteLine(typeof(Hit).IsValueType + " " + typeof(Enemy).IsValueType + " " + typeof(Kind).IsEnum + " " + typeof(int[]).IsArray);
            object o = 3.5;
            Console.WriteLine(o.GetType().Name + " " + o.GetType());
            Console.WriteLine(Describe(5) + " " + Describe("s") + " " + Describe<object>(new Enemy()));
            Console.WriteLine(Kind.B.GetType().FullName);
            Console.WriteLine(typeof(List<string>));
            Console.WriteLine(BitConverter.DoubleToInt64Bits(1.5) + " " + BitConverter.Int64BitsToDouble(4609434218613702656));
            Console.WriteLine(BitConverter.ToString(BitConverter.GetBytes(0x01020304)) + " " + BitConverter.ToInt32(new byte[] { 1, 0, 0, 0 }, 0));
            Console.WriteLine(BitConverter.ToDouble(BitConverter.GetBytes(2.25), 0) + " " + BitConverter.SingleToInt32Bits(1f));
            Console.WriteLine(BitConverter.DoubleToInt64Bits(double.NaN).ToString("X"));
            var r = new Random(42);
            var nums = new List<int>();
            for (int i = 0; i < 8; i++) nums.Add(r.Next(100));
            Console.WriteLine(string.Join(" ", nums));
            Console.WriteLine(r.Next() + " " + r.Next(5, 10) + " " + r.NextDouble());
            var r2 = new Random(-7);
            Console.WriteLine(r2.Next(int.MinValue, int.MaxValue) + " " + r2.Next(1000));
            var sw = Stopwatch.StartNew();
            sw.Stop();
            Console.WriteLine(sw.ElapsedMilliseconds >= 0);
        }
    }
}
