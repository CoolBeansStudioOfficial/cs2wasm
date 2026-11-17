// Repro (proof 5): the frames of a System.Diagnostics.StackTrace. Stock GodotSharp's GD.PushError (and so its
// report of an exception that escapes a script callback, ExceptionUtils.LogException) takes
// new StackTrace(skipFrames, fNeedFileInfo: true).GetFrame(0) and reads its file name, line and method.
// Under .NET this prints: True / True / A / True.
//   cs2wasmc StackTraceFrames.cs -o s.wasm && node host/run.mjs s.wasm main
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

static class Program
{
    static void Main() => A();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void A()
    {
        var trace = new StackTrace(0, true);
        Console.WriteLine(trace.FrameCount > 0);
        StackFrame frame = trace.GetFrame(0);
        Console.WriteLine(frame != null);
        Console.WriteLine(frame?.GetMethod()?.Name);
        Console.WriteLine(frame?.GetFileLineNumber() >= 0);
    }
}
