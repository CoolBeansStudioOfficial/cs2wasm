// A delegate's BeginInvoke and EndInvoke are the runtime's: .NET throws PlatformNotSupportedException for them, and a
// program that calls them catches it (a body-less method used to trap at run time instead).
using System;

static class Program
{
    static void Main()
    {
        Func<int, int> twice = x => x * 2;
        Func<int, AsyncCallback?, object?, IAsyncResult> begin = twice.BeginInvoke;
        try
        {
            begin(4, null, null);
            Console.WriteLine("begin returned");
        }
        catch (PlatformNotSupportedException)
        {
            Console.WriteLine("begin: PlatformNotSupportedException");
        }
        Action<int> act = _ => { };
        try
        {
            act.BeginInvoke(1, null, null);
            Console.WriteLine("action begin returned");
        }
        catch (PlatformNotSupportedException)
        {
            Console.WriteLine("action begin: PlatformNotSupportedException");
        }
        Console.WriteLine("invoke still works: " + twice(21));
    }
}
