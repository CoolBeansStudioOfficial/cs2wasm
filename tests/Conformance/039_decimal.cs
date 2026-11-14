using System;
using System.Linq;
using System.Runtime.InteropServices;

static class Program
{
    static decimal Total(decimal[] prices, decimal taxRate) => prices.Sum() * (1 + taxRate);

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        decimal a = 1.10m, b = 2.205m;
        Console.WriteLine(a + b);
        Console.WriteLine(a - b);
        Console.WriteLine(a * b);
        Console.WriteLine(b / a);
        Console.WriteLine(1m / 3m);
        Console.WriteLine(10m / 4m + " " + 1.00m / 2m + " " + 7m % 3m + " " + -7.5m % 2m);
        Console.WriteLine(0.1m + 0.2m == 0.3m);
        Console.WriteLine(decimal.MaxValue + " " + decimal.MinValue);
        Console.WriteLine((decimal)0.1 + " " + (decimal)1.0 / 3 + " " + (decimal)123.456f + " " + (double)1.25m + " " + (int)-3.99m + " " + (long)12345678901.9m);
        Console.WriteLine(Math.Round(2.345m, 2) + " " + Math.Round(2.355m, 2) + " " + Math.Round(2.5m) + " " + Math.Round(3.5m) + " " + Math.Round(2.345m, 2, MidpointRounding.AwayFromZero));
        Console.WriteLine(decimal.Floor(-1.5m) + " " + decimal.Ceiling(-1.5m) + " " + decimal.Truncate(9.99m) + " " + Math.Abs(-4.2m) + " " + Math.Max(1.5m, 1.25m));
        Console.WriteLine($"{1234.5678m:F2} {1234.5678m:N1} {0.125m:P1} {12.5m:0.000} {-0.001m:F2}");
        Console.WriteLine(Total(new[] { 19.99m, 5.01m, 0.5m }, 0.0825m));
        Console.WriteLine(decimal.Parse("123.4500") + " " + decimal.Parse("-0.5") + " " + (decimal.TryParse("1e3", out var e) ? e : -1m));
        var list = new[] { 3.3m, 1.1m, 2.2m };
        Console.WriteLine(string.Join(",", list.OrderBy(x => x)) + " " + list.Max() + " " + list.Average());
        decimal acc = 0;
        for (int i = 0; i < 10; i++) acc += 0.1m;
        acc++;
        Console.WriteLine(acc + " " + (acc > 1.9m) + " " + decimal.Compare(acc, 2m) + " " + acc.Equals(2.0m) + " " + (-acc));
        decimal two = 2, zero = 0;
        try { var big = decimal.MaxValue * two; Console.WriteLine(big); } catch (OverflowException ex) { Console.WriteLine(ex.Message); }
        try { Console.WriteLine(1m / zero); } catch (DivideByZeroException ex) { Console.WriteLine(ex.Message); }
        Console.WriteLine(string.Join(" ", decimal.GetBits(-1.5m)));
    }
}
