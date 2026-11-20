// Files over the host's file system (SPEC section 8: every module gets files, as .NET's browser target does): writing,
// appending, reading back, a FileStream's seeks and partial reads, directories and their listing, existence, moves,
// copies and deletes, and the exceptions for what is not there. Relative paths, so .NET and the module work in the same
// directory; nothing printed depends on the machine.
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // (a directory of its own: the suite runs the program checked and as shipped at once, and .NET beside them)
        string dir = "cs2wasm-070-files-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        var a = Path.Combine(dir, "a.txt");

        File.WriteAllText(a, "hello\n");
        File.AppendAllText(a, "world\n");
        Console.WriteLine(File.ReadAllText(a).Replace("\n", "|"));
        Console.WriteLine(string.Join(",", File.ReadAllLines(a)) + " " + new FileInfo(a).Length);

        using (var fs = new FileStream(Path.Combine(dir, "b.bin"), FileMode.Create, FileAccess.ReadWrite))
        {
            fs.Write(Encoding.ASCII.GetBytes("0123456789"));
            fs.Seek(3, SeekOrigin.Begin);
            var buf = new byte[4];
            int n = fs.Read(buf, 0, 4);
            Console.WriteLine(n + " " + Encoding.ASCII.GetString(buf, 0, n) + " " + fs.Position + " " + fs.Length);
            fs.Seek(-2, SeekOrigin.End);
            fs.WriteByte((byte)'X');
            fs.Position = 0;
            Console.WriteLine(new StreamReader(fs).ReadToEnd());
        }

        File.WriteAllBytes(Path.Combine(dir, "sub", "c.dat"), [1, 2, 3]);
        Console.WriteLine(string.Join(",", Directory.GetFiles(dir).Select(Path.GetFileName).Order()));
        Console.WriteLine(string.Join(",", Directory.GetDirectories(dir).Select(Path.GetFileName)));
        Console.WriteLine(string.Join(",", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/')).Order()));
        Console.WriteLine(File.Exists(a) + " " + File.Exists(Path.Combine(dir, "nope")) + " " + Directory.Exists(Path.Combine(dir, "sub")));

        File.Move(a, Path.Combine(dir, "moved.txt"));
        File.Copy(Path.Combine(dir, "moved.txt"), Path.Combine(dir, "copy.txt"));
        Console.WriteLine(File.Exists(a) + " " + File.ReadAllText(Path.Combine(dir, "copy.txt")).Length);
        File.Delete(Path.Combine(dir, "copy.txt"));
        Console.WriteLine(string.Join(",", Directory.GetFiles(dir).Select(Path.GetFileName).Order()));

        try { File.ReadAllText(Path.Combine(dir, "missing.txt")); }
        catch (FileNotFoundException e) { Console.WriteLine("FileNotFound " + Path.GetFileName(e.FileName)); }
        try { Directory.GetFiles(Path.Combine(dir, "nodir")); }
        catch (DirectoryNotFoundException) { Console.WriteLine("DirectoryNotFound"); }

        Directory.Delete(dir, recursive: true);
        Console.WriteLine(Directory.Exists(dir));
    }
}
