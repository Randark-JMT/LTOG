using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTOG.Gui.Core;

public class PersistedMapping
{
    public string Letter { get; set; } = "T:";
    public string Device { get; set; } = "TAPE0";
    public bool ReadOnly { get; set; }
    public bool EjectAfterUnmount { get; set; }
    public bool RemountAtStartup { get; set; }   // per-drive, default unchecked
}

public class Settings
{
    public bool CaptureIndex { get; set; } = true;
    public string WorkFolder { get; set; } = @"C:\tmp\ltfs";
    public bool OverrideSyncPolicy { get; set; } = false;
    /// <summary>0 = when volume is dismounted, 1 = periodically every N minutes</summary>
    public int SyncPolicyMode { get; set; } = 1;
    public int SyncPeriodMinutes { get; set; } = 5;
    public string? DistPath { get; set; }                  // optional override of auto-detection

    public string LastTab { get; set; } = "mount";
    public int IndexSort { get; set; } = 0;   // 0 name A-Z, 1 name Z-A, 2 newest, 3 oldest

    // last window geometry (null until first close)
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public List<PersistedMapping> Mappings { get; set; } = new();

    [JsonIgnore]
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LTOG");
    [JsonIgnore]
    public static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupted settings -> defaults */ }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }
}
