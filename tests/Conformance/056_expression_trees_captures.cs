using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
public struct Money { public int Cents; public Money(int c) { Cents = c; }
  public static Money operator +(Money a, Money b) => new Money(a.Cents + b.Cents);
  public static Money operator -(Money a) => new Money(-a.Cents);
  public static bool operator >(Money a, Money b) => a.Cents > b.Cents;
  public static bool operator <(Money a, Money b) => a.Cents < b.Cents;
  public static implicit operator Money(int c) => new Money(c);
  public static explicit operator int(Money m) => m.Cents;
  public override string ToString() => Cents + "c"; }
public class Shop {
  public int Base = 10;
  public Expression<Func<int, int>> AddBase() => x => x + Base;
}
static class Util { public static T Pick<T>(T a, T b, bool first) => first ? a : b; public static int Twice(int x) => 2 * x; }
static class Program {
  static Expression<Func<int, bool>> Above(int limit) => x => x > limit;
  [UnmanagedCallersOnly(EntryPoint = "main")]
  static void Main() {
    int threshold = 3;
    Expression<Func<int, bool>> big = x => x > threshold;
    var f = big.Compile();
    Console.WriteLine(f(5) + " " + f(2));
    threshold = 1;
    Console.WriteLine(f(2) + " " + big.Compile()(2));
    Console.WriteLine(Above(7).Compile()(8) + " " + Above(7).Compile()(6));
    var shop = new Shop();
    var ab = shop.AddBase().Compile();
    Console.WriteLine(ab(5));
    shop.Base = 100;
    Console.WriteLine(ab(5));
    Expression<Func<string, string, string>> pick = (a, b) => Util.Pick(a, b, true) + Util.Pick(b, a, false);
    Console.WriteLine(pick.Compile()("x", "y"));
    Expression<Func<int, int>> pi = n => Util.Pick(n, Util.Twice(n), false);
    Console.WriteLine(pi.Compile()(21));
    Expression<Func<Money, Money, Money>> addm = (a, b) => -(a + b);
    Console.WriteLine(addm.Compile()(new Money(3), new Money(4)));
    Expression<Func<Money, bool>> rich = m => m > 50;
    Console.WriteLine(rich.Compile()(new Money(60)) + " " + rich.Compile()(new Money(6)));
    Expression<Func<Money, int>> cents = m => (int)m + 1;
    Console.WriteLine(cents.Compile()(new Money(9)));
    var words = new List<string> { "apple", "kiwi", "banana", "fig" };
    int minLen = 4;
    var q = words.AsQueryable().Where(w => w.Length >= minLen).Select(w => w.ToUpper());
    Console.WriteLine(string.Join(",", q));
    minLen = 5;
    Console.WriteLine(string.Join(",", q));
    Console.WriteLine(addm.ToString() + " | " + rich.Body.NodeType + " | " + ((BinaryExpression)rich.Body).Method!.Name);
  }
}
