// See https://aka.ms/new-console-template for more information
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using ModemAPI;
using System.Diagnostics;

Console.WriteLine("LMDTP Fetcher");

string[] config = File.ReadAllLines("config.txt");
string cias = config[0];
int httpPort = int.Parse(config[1]);
int lmdtpClientTimeoutMs = int.Parse(config[2]);
int maxData = int.Parse(config[3]);

VirtualModem mdm = new(cias, new(), rawWs:true);
mdm.Dial();
Console.Write($"[PROCESS]: Connecting to the tetronet using CIAS {cias} : ");
while (!mdm.IsModemConnected) { Console.Write('.'); await Task.Delay(50); }
Console.WriteLine($"\n[EVENT]: The modem is connected to the tetronet with local address of {mdm.LocalModemAddress}");

Console.WriteLine("[INFO]: HttpListener is going to be created");
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(httpPort));
var app = builder.Build();
Console.WriteLine("[SUCCESS]: HttpListener is created");

Console.WriteLine("[PROCESS]: Obtaing mappings \"file-extension -> MIME-type\" using ASP.NET...");
var contentTypes = new FileExtensionContentTypeProvider();
Console.WriteLine("[INFO]: Obtained successfully.");

app.MapGet("/{address}/{resource}", async (string address, string resource, HttpContext ctx) =>
{
    var ip = ctx.Connection.RemoteIpAddress;
    if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
    Console.WriteLine($"[EVENT]: New Http request received from {ip}:{ctx.Connection.RemotePort}");
    Console.WriteLine($"[INFO]: Http fetched tetronet address {address} and resource name {resource}");

    Stopwatch swTotal = Stopwatch.StartNew();
    LMDTPClient client = new(mdm, new(address), (uint)Random.Shared.Next(1000000000, 1200000000), 20000000);
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
