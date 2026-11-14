using System;
using System.Runtime.InteropServices;

static class Program
{
    static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);
    static long Fact(int n) { long r = 1; for (int i = 2; i <= n; i++) r *= i; return r; }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Fib(20));
        Console.WriteLine(Fact(20));
        int a = int.MaxValue; a++; Console.WriteLine(a);
        uint u = 0; u--; Console.WriteLine(u);
        Console.WriteLine(-7 / 2); Console.WriteLine(-7 % 2); Console.WriteLine(7 >> 1); Console.WriteLine(-7 >> 1); Console.WriteLine(-7 >>> 28);
        Console.WriteLine(1L << 40); Console.WriteLine(0xFFu & 0x0F); Console.WriteLine(5 ^ 3); Console.WriteLine(~5);
        byte b = 250; b += 10; Console.WriteLine(b);
        short s = -32768; s--; Console.WriteLine(s);
        char c = 'a'; c++; Console.WriteLine(c); Console.WriteLine((int)c);
        double d = 1.0 / 3; Console.WriteLine(d); Console.WriteLine(0.1 + 0.2); Console.WriteLine(2.5); Console.WriteLine(1e100); Console.WriteLine(-0.0);
        float f = 1.0f / 3; Console.WriteLine(f); Console.WriteLine(16777217f);
        Console.WriteLine(double.NaN); Console.WriteLine(1.0 / 0); Console.WriteLine(Math.Sqrt(2)); Console.WriteLine(Math.Floor(-2.5)); Console.WriteLine(Math.Round(2.5)); Console.WriteLine(Math.Round(3.5));
        double big = 3.99;
        Console.WriteLine((int)big); Console.WriteLine((int)-big); Console.WriteLine((long)(big * 1e19)); Console.WriteLine((byte)(big * 100));
        Console.WriteLine(10 % 3.5); Console.WriteLine(-10.5 % 3); Console.WriteLine(Math.Abs(-5)); Console.WriteLine(Math.Max(3, 9)); Console.WriteLine(Math.Min(-1.5, 2));
        try { checked { int x = int.MaxValue; x += 1; Console.WriteLine(x); } } catch (OverflowException e) { Console.WriteLine("overflow: " + e.Message); }
        try { int z = 0; Console.WriteLine(10 / z); } catch (DivideByZeroException e) { Console.WriteLine(e.Message); }
        try { checked { byte bb = (byte)Fib(15); Console.WriteLine(bb); } } catch (OverflowException) { Console.WriteLine("byte overflow"); }
        Console.WriteLine(123456789L * 1000);
        Console.WriteLine(ulong.MaxValue / 3);
        Console.WriteLine(long.MinValue);
        Console.WriteLine(int.Parse("-123") + 1);
        Console.WriteLine(double.Parse("3.25") * 2);
        Console.WriteLine(Math.Pow(2, 10)); Console.WriteLine(Math.Sin(0.5)); Console.WriteLine(Math.Cos(1.0)); Console.WriteLine(Math.Exp(1)); Console.WriteLine(Math.Log(10)); Console.WriteLine(Math.Atan2(1, 2));
        Console.WriteLine(Math.Tan(0.75)); Console.WriteLine(Math.Pow(1.5, 2.5)); Console.WriteLine(Math.Log10(12345)); Console.WriteLine(Math.Asin(0.3)); Console.WriteLine(Math.Acos(-0.3)); Console.WriteLine(Math.Atan(3));
        Console.WriteLine(Math.Sin(1e6)); Console.WriteLine(Math.Cos(100)); Console.WriteLine(Math.Exp(-3.7)); Console.WriteLine(Math.Pow(10, -2));
        Console.WriteLine(3.14159.ToString("F2")); Console.WriteLine(1234567.891.ToString("N2")); Console.WriteLine(255.ToString("X4")); Console.WriteLine(0.5.ToString("P1")); Console.WriteLine(42.ToString("D5"));
        Console.WriteLine(12.345.ToString("0.0")); Console.WriteLine(1e-7); Console.WriteLine(123456789012345678.0); Console.WriteLine(0.000123f);
    }
}
