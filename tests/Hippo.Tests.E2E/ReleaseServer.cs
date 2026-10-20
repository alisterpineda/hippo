using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Hippo.Tests.E2E;

/// <summary>
/// A release laid out as GitHub serves one, on a loopback port: <c>/releases/latest</c> redirects to the latest
/// version's tag, as GitHub does, and <c>/releases/download/v&lt;version&gt;/&lt;file&gt;</c> holds the files added.
/// It answers one GET or HEAD per connection, then closes it, and anything else with 404. A <c>TcpListener</c>, since
/// <c>HttpListener</c> takes no port 0, and on Windows needs administrator rights or a URL ACL for <c>127.0.0.1</c>.
/// </summary>
public sealed class ReleaseServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    private readonly Dictionary<string, byte[]> _files = [];

    private readonly string _latest;

    private readonly CancellationTokenSource _stopping = new();

    private readonly Task _serving;

    public ReleaseServer(string latestVersion)
    {
        _latest = latestVersion;
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>What to set <c>HIPPO_DOWNLOAD_BASE</c> to.</summary>
    public string DownloadBase => $"{BaseUrl}/releases/download";

    /// <summary>Serves <paramref name="bytes"/> as <paramref name="name"/> among the assets of <paramref name="version"/>.</summary>
    public void Add(string version, string name, byte[] bytes) => _files[$"/releases/download/v{version}/{name}"] = bytes;

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        _serving.Wait(TimeSpan.FromSeconds(5));
        _stopping.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => AnswerAsync(client));
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var request = await ReadHeadAsync(stream);
            var words = request.Split("\r\n")[0].Split(' ');
            var method = words.Length > 0 ? words[0] : "";
            var path = words.Length > 1 ? words[1] : "";

            string status;
            var headers = new List<string>();
            byte[] body = [];
            if (path == "/releases/latest")
            {
                status = "302 Found";
                headers.Add($"Location: {BaseUrl}/releases/tag/v{_latest}");
            }
            else if (_files.TryGetValue(path, out var file))
            {
                status = "200 OK";
                headers.Add("Content-Type: application/octet-stream");
                body = file;
            }
            else
            {
                status = "404 Not Found";
            }
            headers.Add($"Content-Length: {body.Length}");
            headers.Add("Connection: close");

            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{string.Join("\r\n", headers)}\r\n\r\n");
            await stream.WriteAsync(head);
            if (method != "HEAD")
            {
                await stream.WriteAsync(body);
            }
            await stream.FlushAsync();
            client.Client.Shutdown(SocketShutdown.Send);
        }
    }

    /// <summary>The request line and headers, up to the blank line that ends them.</summary>
    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var head = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }
            head.Write(buffer, 0, read);
            if (head.ToArray().AsSpan().IndexOf("\r\n\r\n"u8) >= 0)
            {
                break;
            }
        }
        return Encoding.ASCII.GetString(head.ToArray());
    }
}
