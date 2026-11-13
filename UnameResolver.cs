using ModemAPI;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LMDTP_Fetcher
{
    /// <summary>
    /// Turns the address part of the URL into a tetronet address: a tetronet address is taken as is, a UNAME (for example
    /// <c>freeuname.org</c>) or a videonet address (<c>vnet123</c>) is resolved through the UNNRP server from the config.
    /// Successful answers are cached for <see cref="CacheTtl"/>, errors are not.
    /// </summary>
    public partial class UnameResolver
    {
        public sealed record Result(Address? Address, int StatusCode, string? Error);

        private readonly UNNRPClient Client;
        private readonly Address? Server;
        private readonly int TimeoutMs;
        private readonly ConcurrentDictionary<string, (Address Address, long Timestamp)> Cache = new(StringComparer.OrdinalIgnoreCase);
        public TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

        [GeneratedRegex("^vnet[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex VideonetRegex();

        /// <param name="modem">The modem of the fetcher</param>
        /// <param name="server">Address of the UNNRP server, or null - names aren't resolved, only tetronet addresses work</param>
        /// <param name="timeoutMs">How long to wait for every UNNRP response</param>
        public UnameResolver(IModem modem, Address? server, int timeoutMs)
        {
            Client = new UNNRPClient(modem);
            Server = server;
            TimeoutMs = timeoutMs;
        }

        public bool Enabled => Server != null;

        public Result Resolve(string host)
        {
            Address direct = new(host);
            if (Address.Validate(direct) && !direct.IsEmpty)
            {
                return new(direct, 200, null);
            }
            if (Server == null)
            {
                return new(null, 400, $"'{host}' isn't a tetronet address, and this fetcher has no UNNRP server configured to resolve names");
            }
            if (Cache.TryGetValue(host, out var cached) && Stopwatch.GetElapsedTime(cached.Timestamp) < CacheTtl)
            {
                return new(cached.Address, 200, null);
            }
            try
            {
                Address resolved;
                if (VideonetRegex().IsMatch(host))
                {
                    resolved = Client.ResolveVideonet(Server, host, TimeoutMs);
                }
                else
                {
                    Address[] addresses = Client.ResolveName(Server, host, TimeoutMs);
                    if (addresses.Length == 0)
                    {
                        return new(null, 404, $"The name '{host}' is taken, but doesn't lead to any tetronet address");
                    }
                    // several addresses: the first one, the order is set by the owner of the name
                    resolved = addresses[0];
                }
                Cache[host] = (resolved, Stopwatch.GetTimestamp());
                Console.WriteLine($"[INFO]: UNNRP resolved {host} -> {resolved}");
                return new(resolved, 200, null);
            }
            catch (ArgumentException)
            {
                return new(null, 400, $"'{host}' is neither a tetronet address nor a valid UNAME");
            }
            catch (UNNRPErrorException)
            {
                return new(null, 404, $"The name '{host}' doesn't exist");
            }
            catch (TimeoutException)
            {
                return new(null, 504, $"The UNNRP server {Server} didn't answer in {TimeoutMs} ms while resolving '{host}'");
            }
            catch (UNNRPException ex)
            {
                return new(null, 502, $"Resolving '{host}' failed: {ex.Message}");
            }
            catch (ModemNotConnectedException)
            {
                return new(null, 503, "The modem of this fetcher isn't connected to the tetronet right now");
            }
        }
    }
}
