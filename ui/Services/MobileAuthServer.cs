using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Web;

namespace CloudRedirect.Services;

/// <summary>
/// A lightweight local LAN HTTP server that serves a mobile-friendly web page
/// allowing users to scan a QR code on their phone, complete Google OAuth,
/// and automatically submit the authorization code back to CloudRedirect on their PC.
/// </summary>
public sealed class MobileAuthServer : IDisposable
{
    private readonly OAuthService _oauthService;
    private readonly string _authUrl;
    private readonly Action<string> _log;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public string? LanIp { get; private set; }
    public int Port { get; private set; }
    public string? ServerUrl { get; private set; }

    public MobileAuthServer(OAuthService oauthService, string authUrl, Action<string> log)
    {
        _oauthService = oauthService;
        _authUrl = authUrl;
        _log = log;
    }

    public bool Start()
    {
        LanIp = GetLocalLanIp();
        if (string.IsNullOrEmpty(LanIp))
        {
            _log("Mobile LAN Server: Could not determine local IPv4 address.");
            return false;
        }

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            ServerUrl = $"http://{LanIp}:{Port}/";

            _listenTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _log($"Mobile Assist: Listening for phone on {ServerUrl}");
            return true;
        }
        catch (Exception ex)
        {
            _log($"Mobile Assist failed to start: {ex.Message}");
            Stop();
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancel);
                _ = Task.Run(() => HandleClientAsync(client, cancel), cancel);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                if (cancel.IsCancellationRequested) break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancel)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);

                // Read request line (e.g. GET / HTTP/1.1 or POST /api/submit HTTP/1.1)
                string? requestLine = await reader.ReadLineAsync(cancel);
                if (string.IsNullOrEmpty(requestLine)) return;

                var lineParts = requestLine.Split(' ');
                if (lineParts.Length < 2) return;
                string method = lineParts[0].ToUpperInvariant();
                string path = lineParts[1];

                // Read headers
                int contentLength = 0;
                string? header;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(cancel)))
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(header.Substring(15).Trim(), out contentLength);
                    }
                }

                // Handle routing
                if (method == "GET" && (path == "/" || path.StartsWith("/?") || path.StartsWith("/mobile")))
                {
                    string html = GetMobileHtml();
                    byte[] body = Encoding.UTF8.GetBytes(html);
                    string respHeader = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(respHeader);
                    await stream.WriteAsync(headBytes, cancel);
                    await stream.WriteAsync(body, cancel);
                    await stream.FlushAsync(cancel);
                }
                else if (method == "POST" && path.StartsWith("/api/submit"))
                {
                    char[] bodyChars = new char[contentLength];
                    int readTotal = 0;
                    while (readTotal < contentLength)
                    {
                        int read = await reader.ReadAsync(bodyChars, readTotal, contentLength - readTotal);
                        if (read <= 0) break;
                        readTotal += read;
                    }
                    string postBody = new string(bodyChars, 0, readTotal);

                    // Parse input=...
                    string codeInput = string.Empty;
                    var parsed = ParseFormBody(postBody);
                    if (parsed.TryGetValue("input", out var val))
                    {
                        codeInput = val;
                    }
                    else
                    {
                        codeInput = postBody.Trim();
                    }

                    bool success = _oauthService.TrySubmitManualCodeOrUrl(codeInput, out string error);
                    var resObj = new { success, error = success ? "" : error };
                    string json = JsonSerializer.Serialize(resObj);
                    byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

                    string respHeader = $"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {jsonBytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(respHeader);
                    await stream.WriteAsync(headBytes, cancel);
                    await stream.WriteAsync(jsonBytes, cancel);
                    await stream.FlushAsync(cancel);

                    if (success)
                    {
                        _log("Mobile Assist: Successfully received authorization code from phone!");
                    }
                }
                else
                {
                    byte[] notFound = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(notFound, cancel);
                    await stream.FlushAsync(cancel);
                }
            }
            catch { }
        }
    }

    private static Dictionary<string, string> ParseFormBody(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(body)) return dict;
        var pairs = body.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=');
            if (eq >= 0)
            {
                var k = Uri.UnescapeDataString(pair.Substring(0, eq).Replace('+', ' '));
                var v = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                dict[k] = v;
            }
        }
        return dict;
    }

    private string GetMobileHtml()
    {
        string encodedAuthUrl = System.Net.WebUtility.HtmlEncode(_authUrl);
        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">
          <title>CloudRedirect Mobile Auth</title>
          <style>
            * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
            body {
              font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
              background-color: #171a21;
              color: #f3f3f3;
              margin: 0;
              padding: 16px;
              display: flex;
              justify-content: center;
              min-height: 100vh;
            }
            .container {
              width: 100%;
              max-width: 440px;
              background: #1b2838;
              border: 1px solid #2a475e;
              border-radius: 10px;
              padding: 20px;
              box-shadow: 0 8px 24px rgba(0,0,0,0.5);
              display: flex;
              flex-direction: column;
            }
            .header {
              text-align: center;
              margin-bottom: 20px;
              border-bottom: 1px solid #2a3d54;
              padding-bottom: 14px;
            }
            .title {
              font-size: 20px;
              font-weight: 700;
              color: #66c0f4;
              margin: 0 0 6px 0;
            }
            .subtitle {
              font-size: 13px;
              color: #8f98a0;
              margin: 0;
              line-height: 1.4;
            }
            .step-card {
              background: #1a2432;
              border: 1px solid #2a3d54;
              border-radius: 8px;
              padding: 14px;
              margin-bottom: 16px;
            }
            .step-header {
              display: flex;
              align-items: center;
              margin-bottom: 8px;
            }
            .step-badge {
              background: #1a9fff;
              color: #fff;
              font-weight: bold;
              font-size: 12px;
              border-radius: 12px;
              padding: 2px 8px;
              margin-right: 8px;
            }
            .step-title {
              font-size: 14px;
              font-weight: 600;
              color: #f3f3f3;
            }
            .step-desc {
              font-size: 12px;
              color: #8f98a0;
              margin: 4px 0 10px 0;
              line-height: 1.4;
            }
            .btn {
              display: block;
              width: 100%;
              padding: 14px;
              border: none;
              border-radius: 6px;
              font-size: 15px;
              font-weight: 700;
              text-align: center;
              text-decoration: none;
              cursor: pointer;
              transition: filter 0.2s;
            }
            .btn:active { filter: brightness(0.85); }
            .btn-green {
              background: linear-gradient(to bottom, #5c7e10, #4b6b0d);
              color: #fff;
              border: 1px solid #6ca015;
            }
            .btn-blue {
              background: linear-gradient(to bottom, #214b6b, #1b3d57);
              color: #66c0f4;
              border: 1px solid #3877a5;
              margin-top: 10px;
            }
            .input-box {
              width: 100%;
              padding: 12px;
              border: 1px solid #2a3d54;
              border-radius: 6px;
              background: #121922;
              color: #fff;
              font-size: 13px;
              font-family: inherit;
            }
            .input-box:focus {
              outline: none;
              border-color: #1a9fff;
            }
            .status-box {
              margin-top: 12px;
              padding: 10px;
              border-radius: 6px;
              font-size: 13px;
              display: none;
              text-align: center;
              font-weight: 600;
              line-height: 1.4;
            }
            .status-success {
              display: block;
              background: rgba(164, 208, 7, 0.15);
              border: 1px solid #a4d007;
              color: #a4d007;
            }
            .status-error {
              display: block;
              background: rgba(224, 108, 117, 0.15);
              border: 1px solid #e06c75;
              color: #e06c75;
            }
            .status-busy {
              display: block;
              background: rgba(26, 159, 255, 0.15);
              border: 1px solid #1a9fff;
              color: #1a9fff;
            }
          </style>
        </head>
        <body>
          <div class="container">
            <div class="header">
              <h1 class="title">CloudRedirect Mobile Sign-In</h1>
              <p class="subtitle">Complete authentication on your phone to link Google Drive to CloudRedirect on your PC.</p>
            </div>

            <div class="step-card">
              <div class="step-header">
                <span class="step-badge">1</span>
                <span class="step-title">Sign In with Google</span>
              </div>
              <p class="step-desc">Tap below to sign in and grant CloudRedirect permissions. After allowing access, the browser will navigate to a page that says <em>"This site can't be reached"</em>. <strong>Copy the entire link from your browser's address bar.</strong></p>
              <a class="btn btn-green" href="{{encodedAuthUrl}}" target="_blank">Open Google Sign-In</a>
            </div>

            <div class="step-card">
              <div class="step-header">
                <span class="step-badge">2</span>
                <span class="step-title">Send Code to PC</span>
              </div>
              <p class="step-desc">Paste the redirected link (or code) below and tap Send. CloudRedirect on your PC will automatically complete sign-in.</p>
              <input type="text" id="codeBox" class="input-box" placeholder="Paste http://localhost:... or code here" autocomplete="off" autocorrect="off" autocapitalize="off" spellcheck="false">
              <button class="btn btn-blue" id="sendBtn" onclick="submitToPc()">Send to CloudRedirect on PC</button>
              <div id="statusBox" class="status-box"></div>
            </div>
          </div>

          <script>
            async function submitToPc() {
              const box = document.getElementById('codeBox');
              const status = document.getElementById('statusBox');
              const btn = document.getElementById('sendBtn');
              const val = box.value.trim();

              if (!val) {
                status.className = 'status-box status-error';
                status.textContent = 'Please paste the redirected link or authorization code first.';
                return;
              }

              status.className = 'status-box status-busy';
              status.textContent = 'Sending code to your PC...';
              btn.disabled = true;

              try {
                const resp = await fetch('/api/submit', {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                  body: 'input=' + encodeURIComponent(val)
                });
                const data = await resp.json();
                if (data.success) {
                  status.className = 'status-box status-success';
                  status.textContent = 'Success! CloudRedirect on your PC is now authenticated. You can close this browser tab.';
                  box.value = '';
                } else {
                  status.className = 'status-box status-error';
                  status.textContent = data.error || 'Failed to authenticate.';
                  btn.disabled = false;
                }
              } catch (err) {
                status.className = 'status-box status-error';
                status.textContent = 'Could not reach your PC. Ensure your phone and PC are on the same Wi-Fi network.';
                btn.disabled = false;
              }
            }
          </script>
        </body>
        </html>
        """;
    }

    public static string? GetLocalLanIp()
    {
        // 1. Query OS routing table via dummy UDP connect
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint ep)
            {
                string ip = ep.Address.ToString();
                if (!ip.StartsWith("127.") && !ip.StartsWith("169.254."))
                    return ip;
            }
        }
        catch { }

        // 2. Scan network interfaces
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ip = ua.Address.ToString();
                        if (!ip.StartsWith("127.") && !ip.StartsWith("169.254."))
                            return ip;
                    }
                }
            }
        }
        catch { }

        return null;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
