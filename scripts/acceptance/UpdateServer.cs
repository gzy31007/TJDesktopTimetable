using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Tjt.Acceptance
{
    // In-process loopback fixture, no URL ACL, shell jobs, external requests or installers.
    public sealed class UpdateServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Thread thread;
        private readonly string[] bodies;
        private readonly int[] statuses;
        private volatile bool stopping;
        private int requestCount;
        public int Port { get; private set; }
        public int RequestCount { get { return Volatile.Read(ref requestCount); } }
        public string Error { get; private set; }

        public UpdateServer(string[] bodies, int[] statuses)
        {
            if (bodies == null || statuses == null || bodies.Length != statuses.Length)
                throw new ArgumentException("Response bodies and statuses must match.");
            this.bodies = bodies;
            this.statuses = statuses;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            thread = new Thread(Serve) { IsBackground = true };
            thread.Start();
        }

        private void Serve()
        {
            try
            {
                while (!stopping)
                {
                    using (var client = listener.AcceptTcpClient())
                    {
                        client.ReceiveTimeout = client.SendTimeout = 2000;
                        using (var stream = client.GetStream())
                        {
                            // Deliberately tiny HTTP/1.1 surface for the application's GET only.
                            var header = new StringBuilder();
                            while (header.Length < 8192 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                            {
                                var b = stream.ReadByte();
                                if (b < 0) throw new IOException("Incomplete loopback request.");
                                header.Append((char)b);
                            }
                            if (!header.ToString().StartsWith("GET /releases/latest HTTP/1.", StringComparison.Ordinal)
                                || !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                                throw new IOException("Unexpected loopback request.");
                            var index = Interlocked.Increment(ref requestCount) - 1;
                            if (index >= bodies.Length) throw new IOException("Unexpected extra update request.");
                            var body = Encoding.UTF8.GetBytes(bodies[index]);
                            var prefix = Encoding.ASCII.GetBytes("HTTP/1.1 " + statuses[index] + " Fixture\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                            stream.Write(prefix, 0, prefix.Length);
                            stream.Write(body, 0, body.Length);
                        }
                    }
                }
            }
            catch (Exception ex) { if (!stopping) Error = ex.GetType().Name; }
        }

        public void Dispose()
        {
            stopping = true;
            listener.Stop();
            if (!thread.Join(3000)) throw new TimeoutException("Loopback fixture did not stop.");
        }
    }
}
