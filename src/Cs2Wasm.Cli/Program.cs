// cs2wasmc: the command line (Cli.cs)
using Cs2Wasm;

// --plugin file.dll (SPEC section 7.1: on the .NET runtime a plugin is an assembly passed in process): the assembly's
// plugin passes become known to the command line, and are chosen as if named one by one
args = Cli.Expand(args);
for (int i = 0; i + 1 < args.Length; i++)
{
    if (args[i] != "--plugin" || !File.Exists(args[i + 1])) continue;
    var asm = System.Reflection.Assembly.LoadFrom(Path.GetFullPath(args[i + 1]));
    var names = new List<string>();
    foreach (var t in asm.GetTypes())
    {
        if (t.IsAbstract || !typeof(ICompilerPlugin).IsAssignableFrom(t)) continue;
        Cli.KnownPlugins.Add((ICompilerPlugin)Activator.CreateInstance(t)!);
        names.Add(t.FullName!);
    }
    if (names.Count == 0) { Console.Error.WriteLine($"cs2wasm: no plugin pass (ICompilerPlugin) in {args[i + 1]}"); return 2; }
    args = [.. args[..i], .. names.SelectMany(n => new[] { "--plugin", n }), .. args[(i + 2)..]];
    i += names.Count * 2 - 1;
}
return Cli.Run(args);
