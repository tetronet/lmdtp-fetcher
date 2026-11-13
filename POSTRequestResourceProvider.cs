using ModemAPI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace LMDTP_Fetcher
{
    public class POSTRequestResourceProvider : IResourceProvider
    {
        public ConcurrentDictionary<string, POSTPendingRequest> Resources = [];
        public POSTRequestResourceProvider()
        {
            Task.Run(async delegate ()
            {
                while (true)
                {
                    foreach (KeyValuePair<string, POSTPendingRequest> kvRes in Resources)
                    {
                        if (kvRes.Value.DoTimeout())
                        {
                            if (Resources.TryRemove(kvRes.Key, out POSTPendingRequest? req)) // can remove since it's a concurrent dict =}
                            {
                                await req.Data.DisposeAsync(); // prevent shit on the hdd
                            }
                        }
                    }
                    Thread.Sleep(1000);
                }
            });
        }
        public Stream? GetResource(string resourceName)
        {
            if (Resources.TryGetValue(resourceName, out POSTPendingRequest? value))
            {
                value.Data.Position = 0;
                return value.Data;
            }
            return null;
        }

        public LMDTPResponseFlags GetResourceResponseFlags(string resourceName)
        {
            if (Resources.ContainsKey(resourceName))
            {
                return LMDTPResponseFlags.Success;
            }
            else
            {
                return LMDTPResponseFlags.LargeMessageNotFound;
            }
        }

        public byte[] GetResourceSha512Hashsum(string resourceName)
        {
            // computed in POSTPendingRequest.Seal(): hashing here would move the stream to its end and nothing would be sent
            return Resources[resourceName].Hash;
        }

        public long GetResourceSize(string resourceName)
        {
            return Resources[resourceName].Data.Length;
        }
    }
}
