using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LTOG.Gui.Core;

public class MountOptions
{
    public bool ReadOnly;
    public bool EjectAfterUnmount;
    public bool CaptureIndex = true;
    public string WorkFolder = @"C:\tmp\ltfs";
    public bool OverrideSyncPolicy;
    public int SyncPolicyMode;             // 0 = unmount, 1 = periodic
    public int SyncPeriodMinutes = 5;

    public string SyncTypeOption =>
        !OverrideSyncPolicy ? "sync_type=close"
        : SyncPolicyMode == 0 ? "sync_type=unmount"
        : $"sync_type=time@{SyncPeriodMinutes}";

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (ReadOnly) parts.Add("Read-only");
            parts.Add(!OverrideSyncPolicy ? "Index written on file close"
                : SyncPolicyMode == 0 ? "Index written on dismount"
                : $"Index written every {SyncPeriodMinutes} min");
            if (CaptureIndex) parts.Add("index snapshots");
            if (EjectAfterUnmount) parts.Add("eject after unmount");
            return string.Join("  ·  ", parts);
        }
    }
}

public class Mapping : INotifyPropertyChanged
{
    public string Letter { get; set; } = "T:";
    public string Device { get; set; } = "TAPE0";
    public string Description { get; set; } = "";
    public MountOptions Options { get; set; } = new();
    public string OptionsSummary => Options.Summary;
    public int Pid { get; set; }
    public bool IsExternal { get; set; }   // found running from a previous session
    public string? VolumeName { get; set; } // cartridge volume name (from cartridge MAM)
    public string? FormatVersion { get; set; } // LTFS format version (from cartridge MAM)
    public string? LtoGeneration { get; set; } // e.g. "LTO-5" (from medium density code)
    public bool WriteProtected { get; set; } // physical write-protect tab engaged

    /// <summary>The live ltfs.exe process (anchored so its Exited event still fires).</summary>
    internal Process? Proc { get; set; }
    /// <summary>The activity-log entry for this mount, completed when ltfs.exe exits.</summary>
    internal ILogScope? Scope { get; set; }

    private string _state = "Mounting...";
    public string State
    {
        get => _state;
        set
        {
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionGlyph)));
        }
    }

    /// <summary>Shown only for transitional/error states; steady "Mounted" is implied.</summary>
    public string StateText => _state is "Mounted" or "Unmounted" ? "" : _state;

    /// <summary>List action button: Cancel while the mount is starting, else Unmount.</summary>
    public string ActionText => _state == "Mounting..." ? "Cancel" : "Unmount";
    public string ActionGlyph => _state == "Mounting..." ? "" : "";

    /// <summary>Primary display line: the cartridge's volume name.</summary>
    public string Title =>
        string.IsNullOrEmpty(VolumeName) ? "(unlabelled volume)" : VolumeName!;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class MountManager
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroup);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    /// <summary>Start ltfs.exe for a mapping and wait for the volume to come up.</summary>
    public async Task MountAsync(Mapping m, IActivityLog log)
    {
        var o = m.Options;

        // Build the argument list once, so the logged command line is exactly
        // what we hand to the process.
        var args = new List<string> { m.Letter, "-f" };
        void Opt(string s) { args.Add("-o"); args.Add(s); }
        Opt($"config_file={LtfsEnv.LtfsConfFuse}");
        Opt($"devname={m.Device.Replace('\\', '/')}");
        Opt(o.SyncTypeOption);
        if (o.CaptureIndex)
        {
            Opt("capture_index");
            Opt($"work_directory={o.WorkFolder.Replace('\\', '/')}");
        }
        if (o.ReadOnly) Opt("ro");
        if (o.EjectAfterUnmount) Opt("eject");
        // Real Windows volume label, set by WinFsp at mount time. The cartridge
        // name was read from the MAM before mounting, so we can hand it over
        // directly — no Explorer registry override needed. (FUSE splits -o values
        // on commas, so flatten any comma in the label to a space.)
        if (!string.IsNullOrEmpty(m.VolumeName))
            Opt($"volname={m.VolumeName!.Replace(',', ' ')}");

        var psi = new ProcessStartInfo(LtfsEnv.LtfsExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,            // hidden console: needed to deliver Ctrl+C later
            RedirectStandardError = true,     // LTFS logs on stderr
            WorkingDirectory = LtfsEnv.DistPath!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        string title = $"Mount {m.Letter} → {m.Device}" +
                       (string.IsNullOrEmpty(m.Description) ? "" : $" ({m.Description})");
        var scope = log.Begin(LogKind.Mount, title,
            CommandLine.Format(LtfsEnv.LtfsExe, args), LtfsEnv.DistPath);
        m.Scope = scope;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // ltfs.exe runs in the foreground for the whole mount lifetime; stream its
        // log to the entry and close the entry when it finally exits (at unmount).
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) scope.Line(e.Data); };
        p.Exited += (_, _) => { try { scope.Complete(p.ExitCode); } catch { scope.Complete(); } };
        p.Start();
        p.BeginErrorReadLine();
        m.Pid = p.Id;
        m.Proc = p;

        // Wait for the volume (tape index reads can take minutes)
        var deadline = DateTime.Now.AddMinutes(10);
        while (!Directory.Exists($@"{m.Letter}\") && !p.HasExited && DateTime.Now < deadline)
            await Task.Delay(500);

        if (p.HasExited)
        {
            m.State = $"Failed (exit {p.ExitCode})";
            throw new IOException($"ltfs.exe exited with code {p.ExitCode} - see log");
        }
        if (!Directory.Exists($@"{m.Letter}\"))
        {
            m.State = "Timed out";
            throw new TimeoutException("Volume did not come up within 10 minutes");
        }

        m.State = "Mounted";
        scope.Line($"Volume mounted on {m.Letter} — WinFsp now serving requests.");
        if (!string.IsNullOrEmpty(m.VolumeName))
            scope.Line($"Volume label \"{m.VolumeName}\" set via WinFsp (-o volname).");
    }

    /// <summary>
    /// Read the live Windows volume label for a mounted letter — the real label
    /// WinFsp set from -o volname, read straight off the volume. Used when
    /// adopting a mount already running from a previous session.
    /// </summary>
    public static string? GetVolumeLabel(string letter)
    {
        try
        {
            var di = new DriveInfo(letter.TrimEnd(':'));
            if (di.IsReady && !string.IsNullOrWhiteSpace(di.VolumeLabel))
                return di.VolumeLabel;
        }
        catch { /* drive not ready / not a real volume */ }
        return null;
    }

    /// <summary>
    /// Graceful unmount: deliver Ctrl+C to ltfs.exe's hidden console so it writes
    /// the final index and releases the drive; hard-kill only as a last resort.
    /// </summary>
    public async Task UnmountAsync(Mapping m, IActivityLog log)
    {
        m.State = "Unmounting...";
        Process? p = m.Proc;
        if (p == null) { try { p = Process.GetProcessById(m.Pid); } catch { /* already gone */ } }

        if (p != null && !p.HasExited)
        {
            bool sent = false;
            if (AttachConsole(m.Pid))
            {
                SetConsoleCtrlHandler(IntPtr.Zero, true);    // don't kill ourselves
                sent = GenerateConsoleCtrlEvent(0 /* CTRL_C_EVENT */, 0);
                // give LTFS time to write the index and tear down (tape can be slow)
                if (sent)
                    await Task.Run(() => p.WaitForExit(120_000));
                FreeConsole();
                SetConsoleCtrlHandler(IntPtr.Zero, false);
            }
            if (!sent || !p.HasExited)
            {
                log.Note($"{m.Letter} graceful unmount failed — terminating process.", isError: true);
                try { p.Kill(); await p.WaitForExitAsync(); } catch { }
            }
        }

        // The process Exited handler normally closes the mount entry; complete it
        // here too as a fallback (no-op if already done) so it never stays "RUNNING".
        m.Scope?.Complete(p?.HasExited == true ? p.ExitCode : null);
        m.State = "Unmounted";
        log.Note($"{m.Letter} unmounted.");
    }

    /// <summary>Find ltfs.exe processes from a previous session serving a drive letter.</summary>
    public static int? FindExternalMount(string letter)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'ltfs.exe'");
            foreach (var obj in searcher.Get())
            {
                var cmd = obj["CommandLine"] as string ?? "";
                if (cmd.Contains($" {letter}", StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt32(obj["ProcessId"]);
            }
        }
        catch { }
        return null;
    }
}
