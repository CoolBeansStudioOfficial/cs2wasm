// cs2wasm: the boundary of .NET's browser HTTP handler (upstream BrowserHttpHandler, unchanged, calls this), over the
// host's fetch (SPEC section 8) instead of the browser runtime's JavaScript interop: each JSImport is an import of
// module cs2wasm (http_*); a promise is a host operation (Cs2Wasm.Runtime.HostOps) the host completes when its fetch
// does. The controller is a handle of the host's. String arrays go as one block: a count, then each string's length
// and its UTF-16 characters.
using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.JavaScript;
using System.Threading;
using System.Threading.Tasks;
using Cs2Wasm.Runtime;

namespace System.Runtime.InteropServices.JavaScript
{
    /// <summary>A host object (the fetch controller): its handle.</summary>
    internal sealed class JSObject : IDisposable
    {
        internal readonly int Handle;
        internal JSObject(int handle) => Handle = handle;
        public bool IsDisposed { get; private set; }
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            System.Net.Http.BrowserHttpInterop.Dispose(Handle);
        }
    }

    /// <summary>An error the host's script reported (as the browser runtime's JSException).</summary>
    internal sealed class JSException(string message) : Exception(message);

    internal static class CancelablePromise
    {
        /// <summary>(The controller is aborted with it: the host's operation then completes with an AbortError.)</summary>
        public static void CancelPromise(Task promise) { }
    }
}

namespace System.Net.Http
{
    internal static unsafe partial class BrowserHttpInterop
    {
        static class Host
        {
            [Import("cs2wasm", "http_create_controller")] public static extern int CreateController();
            [Import("cs2wasm", "http_abort")] public static extern void Abort(int controller);
            [Import("cs2wasm", "http_dispose")] public static extern void Dispose(int controller);
            /// <summary>Starts the fetch (operation op): the uri, the headers' and the options' names and values (string
            /// blocks; an option value is its text), and the body (null: none).</summary>
            [Import("cs2wasm", "http_fetch")] public static extern void Fetch(int controller, int op, char* uri, int uriLength, char* block, int blockLength, byte* body, int bodyLength);
            [Import("cs2wasm", "http_status")] public static extern int Status(int controller);
            /// <summary>The response's type (basic, cors, opaque...) into the buffer: its length in characters.</summary>
            [Import("cs2wasm", "http_type")] public static extern int Type(int controller, char* buffer, int size);
            /// <summary>The response's headers as a string block (names then values) into the buffer: its length in characters.</summary>
            [Import("cs2wasm", "http_headers")] public static extern int Headers(int controller, char* buffer, int size);
            /// <summary>Reads the whole body (operation op, whose result is its length).</summary>
            [Import("cs2wasm", "http_body_length")] public static extern void BodyLength(int controller, int op);
            /// <summary>The body read, into the buffer: the bytes copied.</summary>
            [Import("cs2wasm", "http_body")] public static extern int Body(int controller, byte* buffer, int size);
            /// <summary>Reads the next part of a streamed body into the buffer (operation op, whose result is the bytes
            /// read: 0 at the end; the buffer is pinned until it completes).</summary>
            [Import("cs2wasm", "http_stream_read")] public static extern void StreamRead(int controller, int op, byte* buffer, int size);
        }

        internal static void Dispose(int handle) => Host.Dispose(handle);

        // (a response streams; a request's body goes at once)
        public static bool SupportsStreamingRequest() => false;
        public static bool SupportsStreamingResponse() => true;

        public static JSObject CreateController() => new JSObject(Host.CreateController());

        public static void Abort(JSObject httpController) => Host.Abort(httpController.Handle);

        public static Task TransformStreamWriteUnsafe(JSObject httpController, ReadOnlyMemory<byte> buffer, MemoryHandle handle) => throw new PlatformNotSupportedException();

        public static Task TransformStreamClose(JSObject httpController) => throw new PlatformNotSupportedException();

        public static int GetResponseStatus(JSObject httpController) => Host.Status(httpController.Handle);

        public static string GetResponseType(JSObject httpController)
        {
            char* b = stackalloc char[64];
            int n = Host.Type(httpController.Handle, b, 64);
            return new string(b, 0, Math.Min(n, 64));
        }

        public static void GetResponseHeaders(JSObject httpController, HttpHeaders resposeHeaders, HttpHeaders contentHeaders)
        {
            int n = Host.Headers(httpController.Handle, null, 0);
            char[] block = new char[n];
            fixed (char* p = block) Host.Headers(httpController.Handle, p, n);
            var all = ReadBlock(block);
            int half = all.Length / 2;
            // Some of the headers may not even be valid header types in .NET thus we use TryAddWithoutValidation
            // CORS will only allow access to certain headers on browser.
            for (int i = 0; i < half; i++)
            {
                if (!resposeHeaders.TryAddWithoutValidation(all[i], all[half + i]))
                {
                    contentHeaders.TryAddWithoutValidation(all[i], all[half + i]);
                }
            }
        }

        public static Task Fetch(JSObject httpController, string uri, string[] headerNames, string[] headerValues, string[] optionNames, object?[] optionValues)
            => Start(httpController, uri, headerNames, headerValues, optionNames, optionValues, null, -1);

        public static Task FetchStream(JSObject httpController, string uri, string[] headerNames, string[] headerValues, string[] optionNames, object?[] optionValues)
            => throw new PlatformNotSupportedException();

        public static Task FetchBytes(JSObject httpController, string uri, string[] headerNames, string[] headerValues, string[] optionNames, object?[] optionValues, MemoryHandle pinBuffer, int bodyLength)
            => Start(httpController, uri, headerNames, headerValues, optionNames, optionValues, (byte*)pinBuffer.Pointer, bodyLength);

        static Task Start(JSObject c, string uri, string[] headerNames, string[] headerValues, string[] optionNames, object?[] optionValues, byte* body, int bodyLength)
        {
            var strings = new string[headerNames.Length * 2 + optionNames.Length * 2 + 2];
            int k = 0;
            strings[k++] = headerNames.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var h in headerNames) strings[k++] = h;
            foreach (var h in headerValues) strings[k++] = h;
            strings[k++] = optionNames.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var o in optionNames) strings[k++] = o;
            foreach (var o in optionValues) strings[k++] = o switch { null => "", bool b => b ? "true" : "false", _ => o.ToString() ?? "" };
            char[] block = WriteBlock(strings);
            int op = HostOps.Begin(out var task);
            fixed (char* u = uri) fixed (char* bl = block) Host.Fetch(c.Handle, op, u, uri.Length, bl, block.Length, body, bodyLength);
            return Translate(task);
        }

        /// <summary>A host operation's task as the browser runtime's promise: a host error is a JSException.</summary>
        static async Task<int> Translate(Task<int> task)
        {
            try { return await task.ConfigureAwait(false); }
            catch (HostOperationException e) { throw new JSException(e.Message); }
        }

        public static Task<int> GetStreamedResponseBytesUnsafe(JSObject jsController, Memory<byte> buffer, MemoryHandle handle)
        {
            int op = HostOps.Begin(out var task);
            Host.StreamRead(jsController.Handle, op, (byte*)handle.Pointer, buffer.Length);
            return Translate(task);
        }

        public static Task<int> GetResponseLength(JSObject fetchResponse)
        {
            int op = HostOps.Begin(out var task);
            Host.BodyLength(fetchResponse.Handle, op);
            return Translate(task);
        }

        public static int GetResponseBytes(JSObject fetchResponse, Span<byte> buffer)
        {
            fixed (byte* p = buffer) return Host.Body(fetchResponse.Handle, p, buffer.Length);
        }

        static char[] WriteBlock(string[] strings)
        {
            int n = 1;
            foreach (var s in strings) n += 1 + s.Length;
            var b = new char[n];
            int k = 0;
            b[k++] = (char)strings.Length;
            foreach (var s in strings) { b[k++] = (char)s.Length; s.CopyTo(0, b, k, s.Length); k += s.Length; }
            return b;
        }

        static string[] ReadBlock(char[] b)
        {
            if (b.Length == 0) return [];
            var r = new string[b[0]];
            int k = 1;
            for (int i = 0; i < r.Length; i++) { int len = b[k++]; r[i] = new string(b, k, len); k += len; }
            return r;
        }

        public static async Task CancellationHelper(Task promise, CancellationToken cancellationToken, JSObject jsController)
        {
            Http.CancellationHelper.ThrowIfCancellationRequested(cancellationToken);

            if (promise.IsCompletedSuccessfully)
            {
                return;
            }
            try
            {
                using (var operationRegistration = cancellationToken.Register(static s =>
                {
                    (Task _promise, JSObject _jsController) = ((Task, JSObject))s!;
                    CancelablePromise.CancelPromise(_promise);
                    if (!_jsController.IsDisposed)
                    {
                        Abort(_jsController);
                    }
                }, (promise, jsController)))
                {
                    await promise.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException oce) when (cancellationToken.IsCancellationRequested)
            {
                Http.CancellationHelper.ThrowIfCancellationRequested(oce, cancellationToken);
            }
            catch (JSException jse)
            {
                if (jse.Message.StartsWith("AbortError", StringComparison.Ordinal))
                {
                    throw Http.CancellationHelper.CreateOperationCanceledException(jse, CancellationToken.None);
                }
                if (jse.Message.Contains("BrowserHttpWriteStream.Rejected", StringComparison.Ordinal))
                {
                    throw; // do not translate
                }
                Http.CancellationHelper.ThrowIfCancellationRequested(jse, cancellationToken);
                throw new HttpRequestException(jse.Message, jse);
            }
        }

        public static async Task<T> CancellationHelper<T>(Task<T> promise, CancellationToken cancellationToken, JSObject jsController)
        {
            Http.CancellationHelper.ThrowIfCancellationRequested(cancellationToken);
            if (promise.IsCompletedSuccessfully)
            {
                return promise.Result;
            }
            await CancellationHelper((Task)promise, cancellationToken, jsController).ConfigureAwait(false);
            return promise.Result;
        }
    }
}
