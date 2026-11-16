// cs2wasm: asynchronous operations of the host (its fetch: SPEC section 8). An operation is started through an import
// with the id Begin gives; the host reports its end as a completion, which the pump (a background thread, started
// with the first operation) waits for through the import host_next_completion and turns into the result of the
// operation's task. (The host's own work never waits for a thread of the module: host/http.mjs.)
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cs2Wasm.Runtime
{
    public static unsafe class HostOps
    {
        static class Host
        {
            /// <summary>Waits for the next completion: its operation, with its result and its message's length (-1:
            /// none; else read it with CompletionMessage).</summary>
            [Import("cs2wasm", "host_next_completion")] public static extern int NextCompletion(int* result, int* messageLength);
            /// <summary>A completion's message (UTF-16, the length NextCompletion gave) into the buffer.</summary>
            [Import("cs2wasm", "host_completion_message")] public static extern void CompletionMessage(int op, char* buffer, int length);
        }

        static readonly Dictionary<int, TaskCompletionSource<int>> s_pending = [];
        static int s_next;
        static Thread? s_pump;

        /// <summary>A new operation: its id (for the import that starts it) and the task its completion completes.</summary>
        public static int Begin(out Task<int> task)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            int id;
            lock (s_pending)
            {
                id = ++s_next;
                s_pending[id] = tcs;
                if (s_pump == null)
                {
                    s_pump = new Thread(Pump) { IsBackground = true, Name = ".NET Host Operations" };
                    s_pump.Start();
                }
            }
            task = tcs.Task;
            return id;
        }

        static void Pump()
        {
            while (true)
            {
                int result, length;
                int op = Host.NextCompletion(&result, &length);
                string? message = null;
                if (length >= 0)
                {
                    message = new string('\0', length);
                    fixed (char* p = message) Host.CompletionMessage(op, p, length);
                }
                TaskCompletionSource<int>? tcs;
                lock (s_pending) { s_pending.Remove(op, out tcs); }
                if (tcs == null) continue;
                if (message != null) tcs.SetException(new HostOperationException(message));
                else tcs.SetResult(result);
            }
        }
    }

    /// <summary>A host operation that failed, with the host's message (a fetch's TypeError, AbortError...).</summary>
    public sealed class HostOperationException(string message) : System.Exception(message);
}
