using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Lumen;

public sealed record UpdateInfo(Version Latest, string Tag, string PageUrl, Dictionary<string, string> Assets);

/// Looks for a newer GitHub release and downloads its installer for this platform.
public static class Updater
{
    private const string Repo = "cheapearth9/Lumen";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0";
            int plus = v.IndexOf('+'); if (plus >= 0) v = v[..plus];
            return Version.TryParse(v, out var r) ? Norm(r) : new Version(0, 0, 0);
        }
    }

    private static Version Norm(Version v) => new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LumenHost-Updater");
        return c;
    }

    /// Returns the newest release when it is newer than this build (and not skipped by the user); null otherwise or on any error.
    public static async Task<UpdateInfo?> CheckAsync(bool ignoreSkip = false)
    {
        try
        {
            using var http = NewClient();
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return null;
            var latest = Norm(parsed);
            if (latest <= Current) return null;
            if (!ignoreSkip && State.Load().Skipped == tag) return null;
            if (!ignoreSkip && State.Load().RemindAfter > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            var assets = new Dictionary<string, string>();
            foreach (var a in root.GetProperty("assets").EnumerateArray())
                assets[a.GetProperty("name").GetString() ?? ""] = a.GetProperty("browser_download_url").GetString() ?? "";
            return new UpdateInfo(latest, tag, root.GetProperty("html_url").GetString() ?? "", assets);
        }
        catch (Exception ex) { HostLog.Write("update check failed: " + ex.Message); return null; }
    }

    public static void Skip(UpdateInfo u) { var s = State.Load(); s.Skipped = u.Tag; s.Save(); }
    /// "Remind me later": ask again after a day.
    public static void Later() { var s = State.Load(); s.RemindAfter = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(); s.Save(); }

    public static async Task<string> DownloadAsync(UpdateInfo u, string assetName, IProgress<double>? progress = null)
    {
        if (!u.Assets.TryGetValue(assetName, out var url)) throw new InvalidOperationException($"{assetName} is not part of release {u.Tag}");
        string dir = Path.Combine(Path.GetTempPath(), "lumen-update");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, assetName);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LumenHost-Updater");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using (var dst = File.Create(path))
        {
            var buf = new byte[81920]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        return path;
    }

    /// Finds the asset matching a pattern like "lumenhost_*_amd64.deb".
    public static string? FindAsset(UpdateInfo u, string prefix, string suffix) =>
        u.Assets.Keys.FirstOrDefault(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private sealed class State
    {
        public string Skipped { get; set; } = "";
        public long RemindAfter { get; set; }

        private static string FilePath => Path.Combine(Path.GetDirectoryName(HostLog.Path)!, "update.json");
        public static State Load()
        {
            try { return JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)) ?? new State(); } catch { return new State(); }
        }
        public void Save() { try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); } catch { } }
    }
}
