using System.IO;
using System.Text.Json;

namespace DocsViewer.Infrastructure;

/// <summary>Persisted in %LocalAppData%\DocsViewer\settings.json.</summary>
public sealed class AppSettings
{
    public string? LastFolder { get; set; }
    public string? LastDocument { get; set; }

    /// <summary>Source root chosen by the user, keyed by docs folder (a docs folder normally lives inside the repo it
    /// documents, but the user can point it elsewhere).</summary>
    public Dictionary<string, string> SourceRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string DataDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocsViewer");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public string? SourceRootFor(string? docsFolder)
        => docsFolder != null && SourceRoots.TryGetValue(docsFolder, out var root) ? root : null;

    public void SetSourceRoot(string docsFolder, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) SourceRoots.Remove(docsFolder);
        else SourceRoots[docsFolder] = root;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                settings.SourceRoots = new Dictionary<string, string>(settings.SourceRoots ?? new(), StringComparer.OrdinalIgnoreCase);
                return settings;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Loading settings");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Saving settings");
        }
    }
}
