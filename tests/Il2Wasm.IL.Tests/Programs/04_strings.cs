// Strings: literals, concatenation, length, characters, equality, ToString of numbers and objects.
using System;

class Point
{
    public int X, Y;
    public override string ToString() => "(" + X + ", " + Y + ")";
}

static class Program
{
    static string Repeat(string s, int n)
    {
        string r = "";
        for (int i = 0; i < n; i++) r = r + s;
        return r;
    }

    static int CountVowels(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u') n++;
        }
        return n;
    }

    static string Reverse(string s)
    {
        string r = "";
        for (int i = s.Length - 1; i >= 0; i--) r += s[i].ToString();
        return r;
    }

    static void Main()
    {
        Console.WriteLine("Hello, world!");
        string a = "abc", b = "ab" + "c".ToString();
        Console.WriteLine(a == b);
        Console.WriteLine((object)a == (object)b);
        Console.WriteLine(Repeat("xy", 4));
        Console.WriteLine(CountVowels("the quick brown fox jumps over the lazy dog"));
        Console.WriteLine(Reverse("stressed"));
        Console.WriteLine("n=" + 42);
        Console.WriteLine(new Point { X = 3, Y = -4 });
        Console.WriteLine("" + new Point { X = 1, Y = 2 } + "!");
        Console.WriteLine(a.Length + b.Length);
        string nul = null;
        Console.WriteLine("[" + nul + "]");
        Console.WriteLine(a.Equals((object)b));
        Console.WriteLine("abc".GetHashCode() == b.GetHashCode());
    }
}
