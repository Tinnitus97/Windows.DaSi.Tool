using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DaSiPruefung;

/// <summary>
/// A tiny web server on 127.0.0.1 for the tests: serves whatever is stored
/// under a path (otherwise 404). This way the update check, the download and
/// installing winget really run over HTTP - without internet and without
/// third-party servers.
/// </summary>
internal sealed class MiniHttp : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, (int Status, byte[] Inhalt)> _seiten = new(StringComparer.Ordinal);

    /// <summary>How often each path was requested.</summary>
    public ConcurrentDictionary<string, int> Abrufe { get; } = new(StringComparer.Ordinal);

    public string Basis { get; }

    public MiniHttp()
    {
        _listener.Start();
        Basis = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _ = Task.Run(Accept);
    }

    public void Set(string pfad, byte[] inhalt, int status = 200) => _seiten[pfad.TrimStart('/')] = (status, inhalt);
    public void Set(string pfad, string text, int status = 200) => Set(pfad, Encoding.UTF8.GetBytes(text), status);

    private async Task Accept()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var strom = client.GetStream();
                var leser = new StreamReader(strom, Encoding.ASCII, false, 1024, leaveOpen: true);
                var anfrage = await leser.ReadLineAsync() ?? "";
                string? zeile;
                while (!string.IsNullOrEmpty(zeile = await leser.ReadLineAsync())) { /* skip the header lines */ }
                var teile = anfrage.Split(' ');
                var pfad = teile.Length > 1 ? Uri.UnescapeDataString(teile[1].TrimStart('/')) : "";
                Abrufe.AddOrUpdate(pfad, 1, (_, n) => n + 1);
                var (status, inhalt) = _seiten.TryGetValue(pfad, out var s) ? s : (404, Encoding.UTF8.GetBytes("nicht da"));
                var kopf = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Fehler")}\r\nContent-Length: {inhalt.Length}\r\n"
                           + "Content-Type: application/octet-stream\r\nConnection: close\r\n\r\n";
                var k = Encoding.ASCII.GetBytes(kopf);
                await strom.WriteAsync(k);
                await strom.WriteAsync(inhalt);
                await strom.FlushAsync();
            }
            catch { /* connection dropped - does not matter */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
