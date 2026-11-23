// Starts one oracle process with Windows' fault dialogs off: SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX
// | SEM_NOOPENFILEERRORBOX) here, which the child inherits (it is started without CREATE_DEFAULT_ERROR_MODE). The case
// program itself stays free of P/Invoke, so the IL il2wasm compiles is the IL .NET ran. The child shares this
// process's standard streams; its exit code is this process's.
//   launch <program> [arguments...]
using System.Diagnostics;
using System.Runtime.InteropServices;

if (OperatingSystem.IsWindows()) NativeMethods.SetErrorMode(0x0001 | 0x0002 | 0x8000);
var start = new ProcessStartInfo(args[0]) { UseShellExecute = false };
foreach (var a in args.Skip(1)) start.ArgumentList.Add(a);
using var child = Process.Start(start)!;
child.WaitForExit();
return child.ExitCode;

static partial class NativeMethods
{
    [LibraryImport("kernel32.dll")]
    internal static partial uint SetErrorMode(uint mode);
}
