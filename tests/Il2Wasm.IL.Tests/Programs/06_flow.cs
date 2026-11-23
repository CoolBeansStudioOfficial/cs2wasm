// Control flow the structuring must handle: irreducible loops (two entries, from goto), a loop whose head is a try
// (entered by its back edge, not only by falling in), values on the stack across a join, switch with gaps, and
// finally blocks on the way out of loops and returns.
using System;

static class Program
{
    static int Irreducible(int n)
    {
        int i = 0, steps = 0;
        if (n > 5) goto B;
    A:
        i += 1; steps++;
        if (i < 10) goto B;
        goto End;
    B:
        i += 2; steps++;
        if (i < 20) goto A;
    End:
        return i * 100 + steps;
    }

    static int Retry(int failures)
    {
        int attempts = 0;
        for (;;)
        {
            try
            {
                attempts++;
                if (attempts <= failures) throw new InvalidOperationException("try again");
                break;
            }
            catch (InvalidOperationException) { }
        }
        return attempts;
    }

    static int Pick(bool a, int x, int y) => (a ? x : y) + (x > y ? x - y : y - x);

    static string Day(int d)
    {
        switch (d)
        {
            case 1: return "mon";
            case 2: return "tue";
            case 5: return "fri";
            case 6: case 7: return "weekend";
            case 100: return "hundred";
        }
        return "?";
    }

    static int FinallyReturn(int k)
    {
        int r = 1;
        try
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    if (i == k) return r * 1000 + i;
                    r += i;
                }
                finally { r += 1; Console.WriteLine("f " + i); }
            }
            return r;
        }
        finally { Console.WriteLine("outer " + r); }
    }

    static bool And(int a, int b) => a > 0 && b > 0 || a < -10;

    static void Main()
    {
        Console.WriteLine(Irreducible(1));
        Console.WriteLine(Irreducible(9));
        Console.WriteLine(Retry(0));
        Console.WriteLine(Retry(3));
        Console.WriteLine(Pick(true, 3, 8) + Pick(false, 3, 8));
        for (int d = 0; d < 8; d++) Console.WriteLine(Day(d));
        Console.WriteLine(Day(100));
        Console.WriteLine(FinallyReturn(2));
        Console.WriteLine(And(1, 2));
        Console.WriteLine(And(1, -2));
        Console.WriteLine(And(-20, 0));
    }
}
