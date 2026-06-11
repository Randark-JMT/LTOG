namespace LTOG.Gui.Core;

/// <summary>Locates the LTOG dist directory (ltfs.exe + ltfs.conf + plugins).</summary>
public static class LtfsEnv
{
    public static string? DistPath { get; private set; }

    public static string LtfsExe => Path.Combine(DistPath!, "ltfs.exe");
    public static string MkltfsExe => Path.Combine(DistPath!, "mkltfs.exe");
    public static string UnltfsExe => Path.Combine(DistPath!, "unltfs.exe");
    public static string LtfsckExe => Path.Combine(DistPath!, "ltfsck.exe");
    public static string LtfsConf => Path.Combine(DistPath!, "ltfs.conf");
    /// <summary>Forward-slash form for FUSE -o values (backslash is an escape char there).</summary>
    public static string LtfsConfFuse => LtfsConf.Replace('\\', '/');

    public static bool Resolve(string? settingsOverride)
    {
        var exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        var candidates = new[]
        {
            settingsOverride,
            exeDir,
            Path.GetDirectoryName(exeDir),                       // gui exe inside dist\gui\
            Path.Combine(Path.GetDirectoryName(exeDir) ?? "", "dist"),
        };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c)
                && File.Exists(Path.Combine(c, "ltfs.exe"))
                && File.Exists(Path.Combine(c, "ltfs.conf")))
            {
                DistPath = c;
                return true;
            }
        }
        return false;
    }
}
