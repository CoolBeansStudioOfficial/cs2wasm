using System;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
public struct Money { public int C; public Money(int c) { C = c; } public static Money operator +(Money a, Money b) => new Money(a.C + b.C); public static bool operator <(Money a, Money b) => a.C < b.C; public static bool operator >(Money a, Money b) => a.C > b.C; public static Money operator -(Money a) => new Money(-a.C); public override string ToString() => "$" + C; }
static class Program {
  [UnmanagedCallersOnly(EntryPoint = "main")]
  static void Main() {
    Expression<Func<Money?, Money?, Money?>> add = (a, b) => a + b;
    Expression<Func<Money?, Money?, bool>> lt = (a, b) => a < b;
    Expression<Func<Money?, Money?>> neg = a => -a;
    Console.WriteLine(add + " | " + lt + " | " + neg);
    Console.WriteLine(add.Compile()(new Money(2), new Money(3)) + " " + (add.Compile()(null, new Money(3)) == null) + " " + lt.Compile()(new Money(1), new Money(2)) + " " + lt.Compile()(null, new Money(2)) + " " + neg.Compile()(new Money(4)));
  }
}
