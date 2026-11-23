// HTTP through the host's fetch (SPEC section 8: every module gets HTTP as .NET's browser target does): HttpClient's
// GET, POST and PUT with request headers and bodies, response status, headers, content type, a streamed body of
// 300 KB, a 404, and a refused connection as HttpRequestException. The runner serves http-server.mjs on a free
// port and puts it in place of 18765 below.
using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

static class Program
{
    static async Task Run(string server)
    {
        using var c = new HttpClient();
        c.DefaultRequestHeaders.Add("x-mine", "abc");
        var r = await c.GetAsync(server + "/hello?x=1");
        Console.WriteLine((int)r.StatusCode + " " + r.IsSuccessStatusCode + " " + string.Join(",", r.Headers.GetValues("x-test")) + " " + r.Content.Headers.ContentType);
        Console.WriteLine(await r.Content.ReadAsStringAsync());
        var p = await c.PostAsync(server + "/post", new StringContent("payload"));
        Console.WriteLine(await p.Content.ReadAsStringAsync());
        var u = await c.PutAsync(server + "/put", new ByteArrayContent([65, 66, 67]));
        Console.WriteLine(await u.Content.ReadAsStringAsync());
        var big = await c.GetByteArrayAsync(server + "/big");
        Console.WriteLine(big.Length + " " + big.All(b => b == 7));
        var m = await c.GetAsync(server + "/missing");
        Console.WriteLine((int)m.StatusCode + " " + m.IsSuccessStatusCode + " " + await m.Content.ReadAsStringAsync());
        try { m.EnsureSuccessStatusCode(); } catch (HttpRequestException e) { Console.WriteLine("ensure: " + e.StatusCode); }
        try { await c.GetStringAsync("http://127.0.0.1:1/none"); Console.WriteLine("connected?"); }
        catch (HttpRequestException e) { Console.WriteLine("refused: " + e.GetType().Name + " " + (e.StatusCode == null)); }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main() => Run("http://127.0.0.1:18765").Wait();
}
