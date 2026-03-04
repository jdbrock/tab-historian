using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace TabHistorian.Viewer.Services;

public static class FaviconService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly ConcurrentDictionary<string, BitmapImage?> MemoryCache = new();
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TabHistorian", "favicons");

    public static async Task<BitmapImage?> GetFaviconAsync(string domain)
    {
        if (string.IsNullOrEmpty(domain))
            return null;

        if (MemoryCache.TryGetValue(domain, out var cached))
            return cached;

        // Check disk cache
        Directory.CreateDirectory(CacheDir);
        var diskPath = Path.Combine(CacheDir, $"{domain}.png");
        if (File.Exists(diskPath))
        {
            var img = LoadFromFile(diskPath);
            MemoryCache[domain] = img;
            return img;
        }

        // Fetch from Google's favicon service
        try
        {
            var url = $"https://www.google.com/s2/favicons?sz=16&domain={Uri.EscapeDataString(domain)}";
            var bytes = await Http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(diskPath, bytes);
            var img = LoadFromBytes(bytes);
            MemoryCache[domain] = img;
            return img;
        }
        catch
        {
            MemoryCache[domain] = null;
            return null;
        }
    }

    private static BitmapImage? LoadFromFile(string path)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    private static BitmapImage? LoadFromBytes(byte[] bytes)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public static string? ExtractDomain(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            var uri = new Uri(url);
            if (uri.Scheme is "http" or "https")
                return uri.Host;
        }
        catch { }
        return null;
    }
}
