using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace LMDTP_Fetcher
{
    public class POSTPendingRequest
    {
        public Stream Data;
        public long Timestamp;
        public int TimeoutMs;
        /// <summary>
        /// SHA-512 of <see cref="Data"/>, computed once by <see cref="Seal"/>: hashing the stream itself would move its position.
        /// </summary>
        public byte[] Hash = [];
        public bool DoTimeout()
        {
            // TotalMilliseconds: Milliseconds is only the 0..999 part and would never exceed the timeout
            return Stopwatch.GetElapsedTime(Timestamp).TotalMilliseconds > TimeoutMs;
        }
        /// <summary>
        /// Call after the body is written: computes the hash and rewinds the stream to be sent from the beginning.
        /// </summary>
        public void Seal()
        {
            Data.Position = 0;
            Hash = SHA512.HashData(Data);
            Data.Position = 0;
        }
        public POSTPendingRequest(Stream data, int tout)
        {
            Data = data;
            TimeoutMs = tout;
            Timestamp = Stopwatch.GetTimestamp();
        }
    }
}
