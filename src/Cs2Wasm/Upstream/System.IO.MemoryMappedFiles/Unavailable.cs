// cs2wasm: memory-mapped files need files and the operating system's virtual memory, which the sandbox does not have
// (section 8). These are the members the libraries above name; a program that reaches one fails to build.
using System;
using System.IO;
using System.Runtime.InteropServices;
using Cs2Wasm.Runtime;

namespace System.IO.MemoryMappedFiles
{
    public enum MemoryMappedFileAccess { ReadWrite = 0, Read, Write, CopyOnWrite, ReadExecute, ReadWriteExecute }

    public sealed class MemoryMappedFile : IDisposable
    {
        MemoryMappedFile() { }
        [MissingCapability("memory-mapped files")]
        public static MemoryMappedFile CreateFromFile(FileStream fileStream, string? mapName, long capacity, MemoryMappedFileAccess access, HandleInheritability inheritability, bool leaveOpen) => throw null!;
        [MissingCapability("memory-mapped files")]
        public static MemoryMappedFile CreateFromFile(string path) => throw null!;
        [MissingCapability("memory-mapped files")]
        public MemoryMappedViewAccessor CreateViewAccessor(long offset, long size, MemoryMappedFileAccess access) => throw null!;
        [MissingCapability("memory-mapped files")]
        public MemoryMappedViewStream CreateViewStream(long offset, long size, MemoryMappedFileAccess access) => throw null!;
        public void Dispose() { }
    }

    public sealed class MemoryMappedViewAccessor : UnmanagedMemoryAccessor
    {
        MemoryMappedViewAccessor() { }
        public Microsoft.Win32.SafeHandles.SafeMemoryMappedViewHandle SafeMemoryMappedViewHandle { [MissingCapability("memory-mapped files")] get => throw null!; }
        public long PointerOffset { [MissingCapability("memory-mapped files")] get => throw null!; }
    }

    public sealed class MemoryMappedViewStream : UnmanagedMemoryStream
    {
        MemoryMappedViewStream() { }
        public Microsoft.Win32.SafeHandles.SafeMemoryMappedViewHandle SafeMemoryMappedViewHandle { [MissingCapability("memory-mapped files")] get => throw null!; }
        public long PointerOffset { [MissingCapability("memory-mapped files")] get => throw null!; }
    }
}

namespace Microsoft.Win32.SafeHandles
{
    public sealed class SafeMemoryMappedViewHandle : SafeBuffer
    {
        public SafeMemoryMappedViewHandle() : base(true) { }
        protected override bool ReleaseHandle() => true;
    }
}
