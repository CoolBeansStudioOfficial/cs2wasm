// Static methods with integer arithmetic, loops and calls.
using System;

static class Program
{
    static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

    static int Gcd(int a, int b)
    {
        while (b != 0) { int t = a % b; a = b; b = t; }
        return a;
    }

    static long Factorial(int n)
    {
        long r = 1;
        for (int i = 2; i <= n; i++) r *= i;
        return r;
    }

    static int Collatz(int n)
    {
        int steps = 0;
        while (n != 1)
        {
            n = (n & 1) == 0 ? n / 2 : 3 * n + 1;
            steps++;
        }
        return steps;
    }

    static int Classify(int x)
    {
        switch (x)
        {
            case 0: return 10;
            case 1: return 11;
            case 2: case 3: return 23;
            case 5: return 15;
            default: return -1;
        }
    }

    static uint Mix(uint a, uint b) => (a << 5) ^ (b >> 3) ^ (a * 2654435761u);

    static void Main()
    {
        Console.WriteLine(42);
        Console.WriteLine(Fib(20));
        Console.WriteLine(Gcd(1071, 462));
        Console.WriteLine(Factorial(20));
        Console.WriteLine(Collatz(27));
        for (int i = -1; i < 7; i++) Console.WriteLine(Classify(i));
        Console.WriteLine((int)Mix(123456789u, 987654321u));
        int sum = 0;
        for (int i = 0; i < 100; i++) { if (i % 3 == 0) continue; if (i > 90) break; sum += i; }
        Console.WriteLine(sum);
        Console.WriteLine(int.MaxValue + Fib(1));
        Console.WriteLine(-7 / 2);
        Console.WriteLine(-7 % 2);
        Console.WriteLine((long)int.MinValue * 3);
        Console.WriteLine(7 > 3 && 2 > 1);
        int two = Fib(3) * 100; Console.WriteLine((sbyte)two);
        Console.WriteLine((ushort)(two - 201));
        Console.WriteLine(1L << 40);
        Console.WriteLine(-100 >> 3);
        Console.WriteLine((int)(-100u >> 3));
    }
}
