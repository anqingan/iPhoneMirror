using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunLanTransferTests()
    {
        TestLanTransferAsync().GetAwaiter().GetResult();
        Console.WriteLine("PASS LAN transfer: pairing, origins, Unicode text/files, exact image bytes, quotas, snapshot isolation and shutdown.");
        return 0;
    }

    private static async Task TestLanTransferAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "iPhoneMirror-transfer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var host = new LanTransferService(root, fileLimit: 1024);
        try
        {
            await host.StartAsync(IPAddress.Loopback);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false, CookieContainer = new CookieContainer() })
                { BaseAddress = new Uri(host.Address) };
            using var anonymous = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = client.BaseAddress };
            void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            using (var page = await client.GetAsync("/"))
            {
                Check(page.IsSuccessStatusCode, "Pairing page failed to load.");
                var html = await page.Content.ReadAsStringAsync();
                Check(!html.Contains(host.PairCode), "The public page exposed its pairing code.");
                Check(page.Headers.Contains("Content-Security-Policy"), "Transfer page lost its CSP.");
            }
            Check((await client.GetAsync("/files")).StatusCode == HttpStatusCode.Unauthorized, "Files were exposed before pairing.");
            Check((await client.PostAsync("/session", new StringContent("000000"))).StatusCode == HttpStatusCode.Unauthorized, "Incorrect pairing code was accepted.");
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/session") { Content = new StringContent(host.PairCode) })
            {
                request.Headers.Add("Origin", "https://other.example");
                Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "Cross-origin pairing was accepted.");
            }
            Check((await client.PostAsync("/session", new StringContent(host.PairCode))).StatusCode == HttpStatusCode.NoContent, "Pairing failed.");
            Check((await anonymous.GetAsync("/files")).StatusCode == HttpStatusCode.Unauthorized, "Pairing leaked to another client.");
            using (var request = new HttpRequestMessage(HttpMethod.Get, "/files"))
            {
                request.Headers.Host = "other.example";
                Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "A foreign Host header was accepted.");
            }
            const string text = "iPad 中文\n第二行 / café 😀";
            string? received = null;
            host.TextReceived += value => received = value;
            host.PublishText(text);
            Check(await client.GetStringAsync("/text") == text, "Computer-to-iPad text changed.");
            Check((await client.PostAsync("/text", new StringContent(text, Encoding.UTF8))).StatusCode == HttpStatusCode.NoContent && received == text,
                "iPad-to-computer text changed.");
            var localFile = Path.Combine(root, "测试图片.png");
            // A small actual PNG fixture, preserved byte-for-byte in both directions.
            using var pngBytes = new MemoryStream();
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4)));
            png.Save(pngBytes);
            var bytes = pngBytes.ToArray();
            await File.WriteAllBytesAsync(localFile, bytes);
            var offered = await host.OfferFileAsync(localFile);
            await File.WriteAllTextAsync(localFile, "changed after sharing");
            Check((await client.GetByteArrayAsync("/download/" + offered.Id)).SequenceEqual(bytes), "Download was not an isolated file snapshot.");
            using (var response = await client.GetAsync("/files"))
            using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            {
                var item = json.RootElement.EnumerateArray().Single();
                Check(item.GetProperty("name").GetString() == "测试图片.png" && !item.TryGetProperty("path", out _), "File listing exposed a local path or corrupted Unicode.");
            }
            LanTransferService.SharedFile? uploaded = null;
            host.FileReceived += value => uploaded = value;
            async Task<HttpResponseMessage> Upload(byte[] payload, string name)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/upload") { Content = new ByteArrayContent(payload) };
                request.Headers.Add("X-File-Name", Uri.EscapeDataString(name));
                return await client.SendAsync(request);
            }
            Check((await Upload(bytes, "../子目录\\测试图片.png")).StatusCode == HttpStatusCode.Created, "Image upload failed.");
            Check(uploaded is not null && uploaded.Name == "测试图片.png" &&
                Path.GetDirectoryName(uploaded.Path) == host.ReceivedDirectory &&
                (await File.ReadAllBytesAsync(uploaded.Path)).SequenceEqual(bytes), "Upload escaped its directory or changed image bytes.");
            var firstPath = uploaded!.Path;
            var clipboardImage = TransferWindow.LoadImageForClipboard(firstPath, bytes.Length);
            Check(clipboardImage.IsFrozen && clipboardImage.PixelWidth == 1 && clipboardImage.PixelHeight == 1,
                "Received image could not be decoded or was unnecessarily upscaled for the clipboard.");
            Check((await Upload([0, 1, 2, 255], "测试图片.png")).StatusCode == HttpStatusCode.Created && uploaded!.Path != firstPath,
                "An upload overwrote a previously received file.");
            Check((await Upload(new byte[1025], "oversized.bin")).StatusCode == HttpStatusCode.RequestEntityTooLarge, "Oversized file was accepted.");
            Check((await client.GetAsync("/download/not-a-file")).StatusCode == HttpStatusCode.NotFound, "An unshared path was downloadable.");
            for (var i = 0; i < 3; i++) Check((await Upload(new byte[1024], "quota.bin")).IsSuccessStatusCode, "Valid session quota rejected.");
            Check((await Upload(new byte[1024], "over-quota.bin")).StatusCode == HttpStatusCode.RequestEntityTooLarge, "Session quota was exceeded.");
            Check(!Directory.EnumerateFiles(host.ReceivedDirectory, "*.part").Any(), "Failed upload left a partial file.");
            await host.DisposeAsync();
            Check(File.Exists(firstPath), "Stopping sharing deleted a received file.");
            Check(!File.Exists(offered.Path), "Stopping sharing retained an outgoing snapshot.");
            try { await client.GetAsync("/files"); throw new InvalidOperationException("Transfer listener survived disposal."); }
            catch (HttpRequestException) { }

            await using var limited = new LanTransferService(root, 1024);
            await limited.StartAsync(IPAddress.Loopback);
            using var attempts = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(limited.Address) };
            for (var i = 0; i < 10; i++) Check((await attempts.PostAsync("/session", new StringContent("000000"))).StatusCode == HttpStatusCode.Unauthorized, "Pairing failed unexpectedly.");
            Check((await attempts.PostAsync("/session", new StringContent(limited.PairCode))).StatusCode == HttpStatusCode.TooManyRequests, "Pairing guesses were not limited.");
        }
        finally
        {
            await host.DisposeAsync();
            // This root is created exclusively by this fixture under the OS temp directory.
            Directory.Delete(root, recursive: true);
        }
    }
}
