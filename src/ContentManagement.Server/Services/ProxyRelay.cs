using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ContentManagement.Server.Services;

public static class ProxyRelay
{
    public static HttpMessageHandler CreateHandler(Uri proxyUri)
    {
        if (proxyUri.Scheme is "socks4" or "socks5")
        {
            return new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectCallback = (context, token) => ConnectThroughSocksAsync(proxyUri, context.DnsEndPoint, token)
            };
        }

        return new HttpClientHandler
        {
            Proxy = new WebProxy(new UriBuilder(Uri.UriSchemeHttp, proxyUri.Host, proxyUri.Port).Uri),
            UseProxy = true,
            UseCookies = false,
            AllowAutoRedirect = false
        };
    }

    private static async ValueTask<Stream> ConnectThroughSocksAsync(
        Uri proxyUri, DnsEndPoint target, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(proxyUri.Host, proxyUri.Port, cancellationToken);
            var stream = client.GetStream();
            if (proxyUri.Scheme == "socks5")
            {
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken);
                var greeting = new byte[2];
                await ReadExactlyAsync(stream, greeting, cancellationToken);
                if (greeting[0] != 5 || greeting[1] != 0)
                    throw new HttpRequestException("The SOCKS5 proxy does not support unauthenticated connections.");

                var hostBytes = Encoding.ASCII.GetBytes(target.Host);
                if (hostBytes.Length > 255) throw new HttpRequestException("Target host name is too long for SOCKS5.");
                var request = new byte[7 + hostBytes.Length];
                request[0] = 5; request[1] = 1; request[2] = 0; request[3] = 3;
                request[4] = (byte)hostBytes.Length;
                hostBytes.CopyTo(request, 5);
                request[^2] = (byte)(target.Port >> 8);
                request[^1] = (byte)target.Port;
                await stream.WriteAsync(request, cancellationToken);

                var reply = new byte[4];
                await ReadExactlyAsync(stream, reply, cancellationToken);
                if (reply[0] != 5 || reply[1] != 0)
                    throw new HttpRequestException($"SOCKS5 connection failed with status {reply[1]}.");
                var remaining = reply[3] switch
                {
                    1 => 4,
                    4 => 16,
                    3 => await ReadOneLengthAsync(stream, cancellationToken),
                    _ => throw new HttpRequestException("SOCKS5 returned an invalid address type.")
                };
                var discard = new byte[remaining + 2];
                await ReadExactlyAsync(stream, discard, cancellationToken);
            }
            else
            {
                // SOCKS4a delegates DNS resolution to the proxy while retaining the same outbound guard.
                var hostBytes = Encoding.ASCII.GetBytes(target.Host);
                var request = new byte[9 + hostBytes.Length];
                request[0] = 4; request[1] = 1;
                request[2] = (byte)(target.Port >> 8); request[3] = (byte)target.Port;
                request[4] = 0; request[5] = 0; request[6] = 0; request[7] = 1;
                request[8] = 0;
                hostBytes.CopyTo(request, 9);
                await stream.WriteAsync(request, cancellationToken);
                await stream.WriteAsync(new byte[] { 0 }, cancellationToken);
                var reply = new byte[8];
                await ReadExactlyAsync(stream, reply, cancellationToken);
                if (reply[1] != 90)
                    throw new HttpRequestException($"SOCKS4 connection failed with status {reply[1]}.");
            }

            return new OwnedNetworkStream(stream, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<int> ReadOneLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[1];
        await ReadExactlyAsync(stream, length, cancellationToken);
        return length[0];
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new IOException("Proxy closed the connection during protocol negotiation.");
            offset += read;
        }
    }

    private sealed class OwnedNetworkStream(Stream inner, TcpClient owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); owner.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); owner.Dispose(); GC.SuppressFinalize(this); }
    }
}
