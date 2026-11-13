// See https://aka.ms/new-console-template for more information
using LMDTP_Fetcher;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using ModemAPI;
using System.Diagnostics;
using System.IO.Pipelines;

Console.WriteLine("LMDTP Fetcher");

string[] config = File.ReadAllLines("config.txt");
string cias = config[0];
int httpPort = int.Parse(config[1]);
int lmdtpClientTimeoutMs = int.Parse(config[2]);
int maxData = int.Parse(config[3]);
// optional: line 5 - address of the UNNRP server for resolving names (freeuname.org etc.), line 6 - its timeout in ms
Address? unnrpServer = config.Length > 4 && !string.IsNullOrWhiteSpace(config[4]) ? new Address(config[4].Trim()) : null;
int unnrpTimeoutMs = config.Length > 5 && !string.IsNullOrWhiteSpace(config[5]) ? int.Parse(config[5]) : 5000;
if (unnrpServer != null && (!Address.Validate(unnrpServer) || unnrpServer.IsEmpty))
{
    Console.WriteLine($"[ERROR]: '{config[4].Trim()}' in config.txt (line 5) isn't a valid tetronet address of a UNNRP server");
    return;
}

VirtualModem mdm = new(cias, new(), rawWs:true);
mdm.Dial();
Console.Write($"[PROCESS]: Connecting to the tetronet using CIAS {cias} : ");
while (!mdm.IsModemConnected) { Console.Write('.'); await Task.Delay(50); }
Console.WriteLine($"\n[EVENT]: The modem is connected to the tetronet with local address of {mdm.LocalModemAddress}");

Console.WriteLine("[INFO]: HttpListener is going to be created");
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(httpPort);
    // POST bodies up to 200 MB (+ room for a small header); Kestrel's default is ~28.6 MB
    o.Limits.MaxRequestBodySize = 200L * 1024 * 1024 + 64 * 1024;
});
var app = builder.Build();
Console.WriteLine("[SUCCESS]: HttpListener is created");

Console.WriteLine("[INFO]: LMDTPServer is going to be created");
LMDTPServer server = new(mdm, 1000000000, 1200000000, [], false, 20000000, 100000);
server.SrtpEnablePacketLossBasedRateLimiter = true;
server.PacketMaxLength = 32768;
POSTRequestResourceProvider resprov = new();
server.ResourceProvider = resprov;
server.Start();
Console.WriteLine("[INFO]: LMDTPServer was created");
Console.WriteLine("[INFO]: NLMNPClient is going to be created");
NLMNPClient nclient = new(mdm);
Console.WriteLine("[INFO]: NLMNPClient was created");
UnameResolver resolver = new(mdm, unnrpServer, unnrpTimeoutMs);
Console.WriteLine(resolver.Enabled
    ? $"[INFO]: Names are resolved through the UNNRP server {unnrpServer}"
    : "[INFO]: No UNNRP server in config.txt (line 5): only tetronet addresses can be opened");

Console.WriteLine("[PROCESS]: Obtaing mappings \"file-extension -> MIME-type\" using ASP.NET...");
var contentTypes = new FileExtensionContentTypeProvider();
Console.WriteLine("[INFO]: Obtained successfully.");

app.MapMethods("/{address}/{resource}", ["POST", "GET"], async (string address, string resource, HttpContext ctx) =>
{
    var ip = ctx.Connection.RemoteIpAddress;
    if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
    Console.WriteLine($"[EVENT]: New Http request received from {ip}:{ctx.Connection.RemotePort}");
    Console.WriteLine($"[INFO]: Http fetched tetronet address {address} and resource name {resource}");
    UnameResolver.Result resolved = await Task.Run(() => resolver.Resolve(address));
    if (resolved.Address == null)
    {
        Console.WriteLine($"[WARNING]: {resolved.Error}");
        return Results.Text(resolved.Error, statusCode: resolved.StatusCode);
    }
    Address target = resolved.Address;
    if (ctx.Request.Method == "POST")
    {
        FileStream fsPost;
        try
        {
            fsPost = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            POSTPendingRequest body = new(fsPost, 300000); // in milliseconds timeout
            await ctx.Request.Body.CopyToAsync(body.Data);
            body.Seal(); // rewind and hash, otherwise the LMDTP server would send 0 bytes
            string tempresname = Guid.NewGuid().ToString();
            resprov.Resources.TryAdd(tempresname, body);
            Console.WriteLine("[EVENT]: POST request attached to the LMDTP server");
            try
            {
                nclient.Notify(target, mdm.LocalModemAddress, tempresname); // notify the remote side that we have a large message to obtain
            }
            catch (TimeoutException)
            {
                Console.WriteLine("[WARNING]: Large message notification has timed out, which can sometimes mean that the remote server which is the user is asking to send a POST request to may be dead.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ERROR]: POST request processing failed " + ex);
        }
    }

    Stopwatch swTotal = Stopwatch.StartNew();
    LMDTPClient client = new(mdm, target, (uint)Random.Shared.Next(1000000000, 1200000000), 20000000);
    Exception? lmdtpError = null;
    client.ErrorOccured += e =>
    {
        lmdtpError = e;
        Console.WriteLine("[ERROR]: LMDTP failed: " + e);
    };

    // temp file instead of MemoryStream: no 2 GB cap, no LOH buffers, deleted when disposed
    FileStream fs = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
    try
    {
        Stopwatch swRq = Stopwatch.StartNew();
        await Task.Run(() => client.Request(fs, resource, maxData, lmdtpClientTimeoutMs * 10000));

        if (lmdtpError != null)
        {
            fs.Dispose();
            return Results.Text($"OBTAIN in the LMDTP client has failed: {lmdtpError.Message}", statusCode: 500);
        }

        long totalLength = fs.Length;
        Console.WriteLine($"[INFO]: It took {swRq.ElapsedMilliseconds}ms to finish the LMDTP request, which received {totalLength} bytes of data");
        ctx.Response.OnCompleted(() =>
        {
            Console.WriteLine($"[SUCCESS]: Proxied the Request containing {totalLength} bytes from LMDTP to HTTP in {swTotal.ElapsedMilliseconds}ms");
            return Task.CompletedTask;
        });

        fs.Position = 0;
        if (!contentTypes.TryGetContentType(resource, out string? contentType))
            contentType = "application/octet-stream";
        return Results.Stream(fs, contentType); // sets Content-Length, disposes fs after sending
    }
    catch (TimeoutException) when (lmdtpError is null)
    {
        fs.Dispose();
        Console.WriteLine("[WARNING]: LMDTP Request has timed out.");
        return Results.Text("LMDTP Request has timed out. Try reloading the page. If this doesn't help, this may indicate, that the destination address that you specified does not exist in the tetronet.", statusCode: 400);
    }
    catch (Exception ex)
    {
        fs.Dispose();
        Console.WriteLine("[ERROR]: Failed while processing the request: " + ex);
        return Results.Text($"OBTAIN in the LMDTP client has failed: {(lmdtpError ?? ex).Message}", statusCode: 500);
    }
    finally
    {
        await Task.Delay(100); // same grace delay as before
        client.Close();
    }
});

app.MapFallback(() => Results.Text("Expected: /address/resource", statusCode: 400));

app.Run();