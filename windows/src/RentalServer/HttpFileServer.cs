using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace RentalServer;

/// <summary>
/// Minimal embedded HTTP file server that serves binary artifacts from a
/// single directory on a LAN-only port. Used by the self-update system so
/// clients can download new EXEs/APKs without an external web server.
/// Binds to the LAN adapter IP only — never 0.0.0.0.
/// </summary>
public sealed class HttpFileServer : IDisposable
{
    private readonly int _port;
    private readonly string _rootDirectory;
    private readonly string _bindAddress;
    private readonly HttpListener _listener = new();
    private Task? _loop;
    private readonly CancellationTokenSource _cts = new();

    public HttpFileServer(int port, string rootDirectory)
    {
        _port = port;
        _rootDirectory = rootDirectory;
        _bindAddress = GetLanIp() ?? "127.0.0.1";
        Directory.CreateDirectory(_rootDirectory);
    }

    public string BaseUrl => $"http://{_bindAddress}:{_port}";

    public void Start()
    {
        if (_loop is not null) return;

        // Bind to the LAN adapter IP so LAN clients can reach the server
        // without requiring admin rights (the "+" wildcard needs netsh urlacl).
        _listener.Prefixes.Add($"http://{_bindAddress}:{_port}/");
        _listener.Start();
        _loop = Task.Run(RunLoopAsync);
    }

    private async Task RunLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(ctx));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var method = ctx.Request.HttpMethod;
            if (method != "GET" && method != "HEAD")
            {
                ctx.Response.StatusCode = 405;
                ctx.Response.Close();
                return;
            }

            var fileName = ctx.Request.Url?.AbsolutePath.TrimStart('/') ?? string.Empty;
            // Security: reject path traversal (../ etc.)
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.Contains("..") ||
                fileName.Contains('\\'))
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            var filePath = Path.Combine(_rootDirectory, fileName);
            if (!File.Exists(filePath))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength64 = new FileInfo(filePath).Length;
            ctx.Response.StatusCode = 200;

            if (method == "HEAD")
            {
                ctx.Response.Close();
                return;
            }

            await using var fileStream = File.OpenRead(filePath);
            await fileStream.CopyToAsync(ctx.Response.OutputStream);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[http] {ctx.Request.HttpMethod} {ctx.Request.Url}: {ex}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); }
            catch { /* best-effort */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _listener.Close();
        _cts.Dispose();
    }

    private static string? GetLanIp()
    {
        foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or
                System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
            foreach (var ip in adapter.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ip.Address))
                {
                    return ip.Address.ToString();
                }
            }
        }
        return null;
    }
}
