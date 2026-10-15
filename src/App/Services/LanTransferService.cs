using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IPhoneMirror.App.Services;

// Explicit, temporary LAN sharing. Only file snapshots chosen in the window
// are downloadable. Received files persist; session credentials do not.
internal sealed class LanTransferService : IAsyncDisposable
{
    internal sealed record SharedFile(string Id, string Name, long Size, string Path);
    internal const long DefaultFileLimit = 512L * 1024 * 1024;
    private readonly long _fileLimit;
    private readonly long _sessionLimit;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, SharedFile> _offered = new();
    private readonly Queue<DateTimeOffset> _pairAttempts = new();
    private readonly SemaphoreSlim _uploads = new(2);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _outbox;
    private WebApplication? _app;
    private long _reserved;
    private int _count;
    private int _disposed;
    private string _publishedText = "";
    internal string PairCode { get; } = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
    internal string Address { get; private set; } = "";
    internal string ReceivedDirectory { get; }
    internal DateTimeOffset ExpiresAt { get; } = DateTimeOffset.UtcNow.AddMinutes(30);
    internal event Action<string>? TextReceived;
    internal event Action<SharedFile>? FileReceived;
    internal IReadOnlyCollection<SharedFile> OfferedFiles => _offered.Values.ToArray();

    internal LanTransferService(string? storageRoot = null, long fileLimit = DefaultFileLimit)
    {
        _fileLimit = fileLimit;
        _sessionLimit = checked(fileLimit * 4);
        var root = Path.Combine(storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iPhoneMirror", "Transfers"),
            Guid.NewGuid().ToString("N"));
        _outbox = Path.Combine(root, "Sent");
        ReceivedDirectory = Path.Combine(root, "Received");
        Directory.CreateDirectory(_outbox);
        Directory.CreateDirectory(ReceivedDirectory);
    }

    internal static IReadOnlyList<IPAddress> GetAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Where(IsPrivateAddress).Distinct().ToArray();

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31;
    }

    internal async Task StartAsync(IPAddress address)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_app is not null) throw new InvalidOperationException("Transfer session already started.");
        if (!IsPrivateAddress(address) && !IPAddress.IsLoopback(address)) throw new ArgumentException("Use a private IPv4 address.");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(address, 0);
            o.Limits.MaxRequestBodySize = _fileLimit;
            o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            o.Limits.MaxConcurrentConnections = 16;
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (context.Request.Host.Host != address.ToString()) { context.Response.StatusCode = 403; return; }
            if (DateTimeOffset.UtcNow >= ExpiresAt) { context.Response.StatusCode = 410; return; }
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length != 0 && origin != $"http://{context.Request.Host}") { context.Response.StatusCode = 403; return; }
            var path = context.Request.Path.Value;
            if (path is not ("/" or "/app.js" or "/app.css" or "/session"))
            {
                if (!context.Request.Cookies.TryGetValue("transfer", out var token) ||
                    token.Length != _credential.Length || !CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(_credential)))
                { context.Response.StatusCode = 401; return; }
            }
            try { await next(context); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || context.RequestAborted.IsCancellationRequested) { }
            catch (BadHttpRequestException) { if (!context.Response.HasStarted) context.Response.StatusCode = 413; }
            catch (IOException) { if (!context.Response.HasStarted) context.Response.StatusCode = 500; }
        });
        app.MapGet("/", () => Asset("page.html", "text/html; charset=utf-8"));
        app.MapGet("/app.js", () => Asset("app.js", "application/javascript; charset=utf-8"));
        app.MapGet("/app.css", () => Asset("app.css", "text/css; charset=utf-8"));
        app.MapPost("/session", async (HttpContext c) =>
        {
            lock (_gate)
            {
                while (_pairAttempts.TryPeek(out var at) && at < DateTimeOffset.UtcNow.AddMinutes(-1)) _pairAttempts.Dequeue();
                if (_pairAttempts.Count >= 10) { c.Response.StatusCode = 429; return; }
                _pairAttempts.Enqueue(DateTimeOffset.UtcNow);
            }
            if (c.Request.ContentLength is not (>= 0 and <= 64)) { c.Response.StatusCode = 413; return; }
            using var reader = new StreamReader(c.Request.Body, Encoding.UTF8);
            var code = await reader.ReadToEndAsync(c.RequestAborted);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(PairCode)))
            { c.Response.StatusCode = 401; return; }
            c.Response.Cookies.Append("transfer", _credential, new CookieOptions {
                HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = ExpiresAt - DateTimeOffset.UtcNow, Path = "/" });
            c.Response.StatusCode = 204;
        });
        app.MapGet("/files", () => Results.Json(_offered.Values.Select(f => new { f.Id, f.Name, f.Size })));
        app.MapGet("/download/{id}", (string id) => _offered.TryGetValue(id, out var file)
            ? Results.File(file.Path, "application/octet-stream", file.Name, enableRangeProcessing: true)
            : Results.NotFound());
        app.MapGet("/text", () => Results.Text(Volatile.Read(ref _publishedText), "text/plain; charset=utf-8"));
        app.MapPost("/text", async (HttpContext c) =>
        {
            if (c.Request.ContentLength is not (>= 0 and <= 1048576)) { c.Response.StatusCode = 413; return; }
            using var reader = new StreamReader(c.Request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(c.RequestAborted);
            if (text.Length != 0) TextReceived?.Invoke(text);
            c.Response.StatusCode = 204;
        });
        app.MapPost("/upload", ReceiveFileAsync);
        try
        {
            await app.StartAsync(_shutdown.Token);
            Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            _app = app;
        }
        catch { await app.DisposeAsync(); throw; }
    }

    private static IResult Asset(string name, string contentType)
    {
        using var stream = typeof(LanTransferService).Assembly.GetManifestResourceStream("IPhoneMirror.Transfer." + name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Results.Text(reader.ReadToEnd(), contentType);
    }

    internal void PublishText(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 1048576) throw new ArgumentException("Text exceeds 1 MiB.");
        Volatile.Write(ref _publishedText, text);
    }

    private void Reserve(long length)
    {
        if (length < 0 || length > _fileLimit) throw new InvalidOperationException("File exceeds the session file limit.");
        lock (_gate)
        {
            if (_count >= 100 || _reserved + length > _sessionLimit) throw new InvalidOperationException("Transfer session is full.");
            _reserved += length; ++_count;
        }
    }

    private void Release(long length) { lock (_gate) { _reserved -= length; --_count; } }

    internal async Task<SharedFile> OfferFileAsync(string path, string? displayName = null)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var size = source.Length;
        Reserve(size);
        var id = Guid.NewGuid().ToString("N");
        var destination = Path.Combine(_outbox, id);
        try
        {
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await CopyBoundedAsync(source, output, size, _shutdown.Token);
            var file = new SharedFile(id, SafeName(displayName ?? Path.GetFileName(path)), size, destination);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                _offered[id] = file;
            }
            return file;
        }
        catch { Release(size); File.Delete(destination); throw; }
    }

    private async Task ReceiveFileAsync(HttpContext c)
    {
        if (c.Request.ContentLength is not { } size || size < 0 || size > _fileLimit)
        { c.Response.StatusCode = 413; return; }
        if (!await _uploads.WaitAsync(0, c.RequestAborted)) { c.Response.StatusCode = 429; return; }
        var reserved = false;
        string? path = null;
        try
        {
            try { Reserve(size); reserved = true; }
            catch (InvalidOperationException) { c.Response.StatusCode = 413; return; }
            var name = SafeName(Uri.UnescapeDataString(c.Request.Headers["X-File-Name"].ToString()));
            var id = Guid.NewGuid().ToString("N");
            path = Path.Combine(ReceivedDirectory, id + "-" + name);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, c.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using (var output = new FileStream(path + ".part", FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await CopyBoundedAsync(c.Request.Body, output, size, timeout.Token);
            File.Move(path + ".part", path);
            FileReceived?.Invoke(new(id, name, size, path));
            reserved = false; // Successful received bytes retain their session quota.
            c.Response.StatusCode = 201;
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested && !c.RequestAborted.IsCancellationRequested)
        { if (!c.Response.HasStarted) c.Response.StatusCode = 408; }
        finally
        {
            if (reserved) Release(size);
            if (path is not null) File.Delete(path + ".part");
            _uploads.Release();
        }
    }

    internal static string SafeName(string name)
    {
        name = name.Replace('\\', '/').Split('/').LastOrDefault() ?? "file";
        name = new string(name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return name.Length == 0 ? "file" : name[..Math.Min(name.Length, 120)];
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long expected, CancellationToken token)
    {
        var buffer = new byte[65536];
        long written = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
        {
            written += count;
            if (written > expected) throw new IOException("File size changed during transfer.");
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (written != expected) throw new IOException("Incomplete file transfer.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        if (_app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(timeout.Token); }
            finally { await _app.DisposeAsync(); }
        }
        // Received files remain available after the window is closed.
        lock (_gate)
        {
            foreach (var file in _offered.Values) File.Delete(file.Path);
            _offered.Clear();
        }
    }
}
