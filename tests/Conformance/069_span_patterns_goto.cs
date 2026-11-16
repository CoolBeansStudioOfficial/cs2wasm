// Constant string patterns on spans of chars (switch and is, as System.Private.Xml's XmlTextReaderImpl matches names),
// and goto to a label that labels another label (A: B: stmt, as its DtdParser loops).
using System;
using System.Runtime.InteropServices;

static class Program
{
    static string Kind(ReadOnlySpan<char> s) => s switch { "version" => "V", "encoding" => "E", _ => "?" };
    static int Classify(Span<char> s)
    {
        switch (s)
        {
            case "standalone": return 3;
            case "version": return 1;
            default: return 0;
        }
    }
    static int Parse(string s)
    {
        int i = 0, n = 0;
    Again:
    Loop:
        if (i >= s.Length) goto Done;
        if (s[i] == '(') { n++; i++; goto Again; }
        i++;
        goto Loop;
    Done:
        return n;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        char[] buf = "xxversionxx".ToCharArray();
        Console.WriteLine(Kind("version") + Kind("encoding") + Kind("other") + Kind(buf.AsSpan(2, 7)));
        Console.WriteLine(Classify(buf.AsSpan(2, 7)) + " " + Classify("standalone".ToCharArray()) + " " + Classify(buf));
        Console.WriteLine(Parse("a(b(c)d(") + " " + ("abc".AsSpan() is "abc") + " " + ("abc".AsSpan() is "ab"));
    }
}
