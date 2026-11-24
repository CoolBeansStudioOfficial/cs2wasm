// Main(string[]) gets a real array (empty with no command line), never null, and Environment.ExitCode is the exit code
// of a void Main; the IL front end passed null and dropped the code.
using System;

static class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine(args == null ? "null" : "args " + args.Length);
        Console.WriteLine(Environment.GetCommandLineArgs().Length >= 1);
        Environment.ExitCode = 0;
    }
}
