using System;
using System.Text;
using System.Runtime.InteropServices;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        string s = "Hello, World";
        Console.WriteLine(s.Length);
        Console.WriteLine(s[4]);
        Console.WriteLine(s.Substring(7));
        Console.WriteLine(s.Substring(0, 5).ToUpper());
        Console.WriteLine(s.ToLower());
        Console.WriteLine(s.IndexOf('o') + " " + s.LastIndexOf('o') + " " + s.IndexOf("World") + " " + s.IndexOf("xyz"));
        Console.WriteLine(s.Contains("lo, W") + " " + s.StartsWith("Hell") + " " + s.EndsWith("d"));
        Console.WriteLine(s.Replace("World", "il2wasm").Replace('l', 'L'));
        Console.WriteLine("  trim me  ".Trim() + "|" + "xxabcxx".Trim('x') + "|" + "  a".TrimStart() + "|");
        foreach (var part in "a,b,,c".Split(',')) Console.Write("[" + part + "]");
        Console.WriteLine();
        Console.WriteLine(string.Join("-", "a b c d".Split(' ')));
        Console.WriteLine(string.Join(", ", new[] { 1, 2, 3 }));
        Console.WriteLine("abc".PadLeft(6, '.') + "abc".PadRight(6, '.') + "|");
        Console.WriteLine(string.IsNullOrEmpty("") + " " + string.IsNullOrEmpty(null) + " " + string.IsNullOrWhiteSpace("  "));
        string n = null;
        Console.WriteLine("null concat: " + n + "!");
        Console.WriteLine("a" == "a" + "" ? "eq" : "ne");
        string built = "";
        for (int i = 0; i < 5; i++) built += i;
        Console.WriteLine(built);
        var sb = new StringBuilder();
        sb.Append("x=").Append(42).Append(", y=").Append(3.5).Append(' ').Append(true);
        sb.AppendLine();
        sb.Insert(0, ">> ");
        Console.Write(sb.ToString());
        Console.WriteLine(sb.ToString().Trim().Length);
        int x = 7; double y = 2.5;
        Console.WriteLine($"x={x}, y={y}, sum={x + y}, hex={x:X2}, pad=[{x,4}] [{x,-4}] {y:F3}");
        Console.WriteLine(string.Format("{0} + {1} = {2}", 1, 2, 1 + 2));
        Console.WriteLine(string.Concat("a", "b", "c"));
        Console.WriteLine("Abc".CompareTo("abd") < 0);
        Console.WriteLine(string.CompareOrdinal("a", "B") > 0);
        Console.WriteLine("hello".GetHashCode() == ("hel" + "lo").GetHashCode() ? "same" : "hash differs");
        Console.WriteLine("hello".Equals("hel" + "lo"));
        char[] chars = "dcba".ToCharArray();
        Array.Sort(chars);
        Console.WriteLine(new string(chars));
        Console.WriteLine(new string('*', 5));
        Console.WriteLine("é ü ß".ToUpper());
        Console.WriteLine('a' + 'b');
        Console.WriteLine("" + 'a' + 'b');
        Console.WriteLine(char.IsDigit('5') + " " + char.IsLetter('x') + " " + char.IsWhiteSpace('\t') + " " + char.ToUpper('q'));
        Console.WriteLine("tab\tnew\\line \"quoted\" é");
        Console.WriteLine(@"verbatim \n " + """raw "string" here""");
        var parts = "k1=v1;k2=v2".Split(';');
        foreach (var p in parts) { var kv = p.Split('='); Console.WriteLine(kv[0] + " -> " + kv[1]); }
        Console.WriteLine("abc".Insert(1, "XY").Remove(0, 1));
        Console.WriteLine(int.TryParse("12x", out int bad) + " " + bad + " " + int.TryParse("77", out int good) + " " + good);
        Console.WriteLine(1.5.ToString() + 2.25f + 'z' + 10L + 3u);
    }
}
