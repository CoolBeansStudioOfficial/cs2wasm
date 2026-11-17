// `c ? value : throw e` where the value is a struct: the throw runs.
using System;
using System.Runtime.InteropServices;
readonly struct Money
{
    public readonly decimal Amount; public readonly string Cur;
    public Money(decimal a, string c) { Amount = a; Cur = c; }
    public static implicit operator Money(int v) => new(v, "EUR");
    public static Money operator +(Money a, Money b) => a.Cur == b.Cur ? new(a.Amount + b.Amount, a.Cur) : throw new InvalidOperationException("currency");
    public static Money Add(Money a, Money b) => a.Cur == b.Cur ? new(a.Amount + b.Amount, a.Cur) : throw new InvalidOperationException("currency");
    public static Money Add2(Money a, Money b) { if (a.Cur != b.Cur) throw new InvalidOperationException("currency"); return new(a.Amount + b.Amount, a.Cur); }
}
struct I2 { public int X; public static I2 operator +(I2 a, I2 b) => a.X == b.X ? throw new InvalidOperationException("same") : new I2 { X = a.X + b.X }; }
static class Program
{
    static int Thrower(int x) => x > 0 ? throw new InvalidOperationException("pos") : x;
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Money a = 5;
        try { _ = a + new Money(1, "USD"); Console.WriteLine("no1"); } catch (InvalidOperationException e) { Console.WriteLine(e.Message + "1"); }
        try { var x = a + new Money(1, "USD"); Console.WriteLine("no2"); } catch (InvalidOperationException e) { Console.WriteLine(e.Message + "2"); }
        try { _ = Money.Add(a, new Money(1, "USD")); Console.WriteLine("no3"); } catch (InvalidOperationException e) { Console.WriteLine(e.Message + "3"); }
        try { Money.Add2(a, new Money(1, "USD")); Console.WriteLine("no4"); } catch (InvalidOperationException e) { Console.WriteLine(e.Message + "4"); }
        try { _ = Thrower(1); Console.WriteLine("no5"); } catch (InvalidOperationException e) { Console.WriteLine(e.Message + "5"); }
        try { var i = new I2 { X = 1 }; _ = i + i; Console.WriteLine("no6"); } catch (InvalidOperationException e) { Console.WriteLine(e.Message + "6"); }
        try { var i = new I2 { X = 1 }; var j = i + i; Console.WriteLine("no7"); } catch (InvalidOperationException e) { Console.WriteLine(e.Message + "7"); }
        try { var x = a + new Money(1, "USD"); Console.WriteLine("no8" + x.Cur); } catch (InvalidOperationException e) { Console.WriteLine(e.Message + "8"); }
    }
}
