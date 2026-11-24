// A stock library's exception messages come from its resources (SR.GetResourceString through ResourceManager, with the
// assembly's NeutralResourcesLanguageAttribute read by reflection), as .NET reads them when UseSystemResourceKeys is
// off; and an assembly's name and attributes as .NET gives them.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Resources;

static class Program
{
    static void Main()
    {
        ICollection c = new LinkedList<int>(new[] { 1, 2, 3 });
        try { c.CopyTo(new string[3], 0); }
        catch (ArgumentException e) { Console.WriteLine(e.GetType().FullName + ": " + e.Message); }
        try { new LinkedList<int>().AddAfter(null!, 1); }
        catch (ArgumentNullException e) { Console.WriteLine(e.GetType().FullName + ": " + e.Message); }

        var asm = typeof(LinkedList<int>).Assembly;
        Console.WriteLine(asm.FullName);
        var lang = asm.GetCustomAttribute<NeutralResourcesLanguageAttribute>();
        Console.WriteLine("neutral language: " + lang?.CultureName);
    }
}
