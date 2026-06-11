using System.Collections.ObjectModel;
using System.ComponentModel;
using LTOG.Gui.Core;
using Microsoft.UI.Xaml;

namespace LTOG.Gui;

public enum SlotPhase { Idle, Mounting, Mounted, Unmounting }

/// <summary>One captured index snapshot, parsed for display.</summary>
public sealed class SchemaItem
{
    public string Title { get; set; } = "";        // volume name
    public string GenLine { get; set; } = "";      // generation + on-tape update time
    public string CaptureLine { get; set; } = "";  // capture time, file count, size
    public string Uuid { get; set; } = "";         // volume UUID (ties snapshot to cartridge)
    public string Path { get; set; } = "";         // snapshot file on disk
    public DateTime Captured { get; set; }         // snapshot file write time (for sorting)
}

/// <summary>
/// Per-tape-drive card on the Mounting page: drive identity, cartridge
/// status, mount options and the mount/unmount state machine.
/// </summary>
public sealed class DriveSlot : INotifyPropertyChanged
{
    public TapeDrive Drive { get; set; } = null!;
    public string DriveDisplay => Drive.Display;

    public Mapping? Mapping { get; set; }          // active mount, if any

    private CartridgeInfo? _lastCart;              // last MAM read
    public CartridgeInfo? LastCart
    {
        get => _lastCart;
        set
        {
            _lastCart = value;
            Raise(nameof(EjectLoadGlyph));
            Raise(nameof(EjectLoadTooltip));
        }
    }

    public bool MediaAbsent => _lastCart?.State == CartridgeState.NoMedia;

    private bool _mediaOpRunning;   // eject/load in progress
    public bool MediaOpRunning
    {
        get => _mediaOpRunning;
        set
        {
            _mediaOpRunning = value;
            Raise(nameof(MediaOpRunning));   // ProgressRing.IsActive binds this
            Raise(nameof(EjectLoadGlyphVisibility));
            Raise(nameof(EjectLoadSpinnerVisibility));
            RaisePhase();   // every enable gate depends on it
        }
    }

    // Stays enabled during its own op: a disabled WinUI button suspends its
    // child ProgressRing entirely (renders blank). Re-entry is guarded
    // in the click handler instead.
    public bool EjectLoadEnabled => _phase == SlotPhase.Idle && _globalEnabled;

    private bool _refreshRunning;   // manual card refresh in progress
    public bool RefreshRunning
    {
        get => _refreshRunning;
        set
        {
            _refreshRunning = value;
            Raise(nameof(RefreshRunning));   // ProgressRing.IsActive binds this
            Raise(nameof(RefreshGlyphVisibility));
            Raise(nameof(RefreshSpinnerVisibility));
        }
    }

    public Visibility RefreshGlyphVisibility =>
        _refreshRunning ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RefreshSpinnerVisibility =>
        _refreshRunning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EjectLoadGlyphVisibility =>
        _mediaOpRunning ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EjectLoadSpinnerVisibility =>
        _mediaOpRunning ? Visibility.Visible : Visibility.Collapsed;
    public string EjectLoadGlyph => MediaAbsent ? "" : ""; // Download : Upload (load/eject)
    public string EjectLoadTooltip => MediaAbsent ? "Load cartridge" : "Eject cartridge";

    private SlotPhase _phase = SlotPhase.Idle;
    public SlotPhase Phase
    {
        get => _phase;
        set { _phase = value; RaisePhase(); }
    }

    private string _cartridgeText = "Checking cartridge...";
    public string CartridgeText
    {
        get => _cartridgeText;
        set { _cartridgeText = value; Raise(nameof(CartridgeText)); }
    }

    public ObservableCollection<string> Letters { get; } = new();

    private int _selectedLetterIndex = -1;
    public int SelectedLetterIndex
    {
        get => _selectedLetterIndex;
        set { _selectedLetterIndex = value; Raise(nameof(SelectedLetterIndex)); }
    }

    public string? SelectedLetter =>
        _selectedLetterIndex >= 0 && _selectedLetterIndex < Letters.Count
            ? Letters[_selectedLetterIndex] : null;

    private bool _readOnly;
    public bool ReadOnlyChecked
    {
        get => _readOnly;
        set { _readOnly = value; Raise(nameof(ReadOnlyChecked)); }
    }

    private bool _eject;
    public bool EjectChecked
    {
        get => _eject;
        set { _eject = value; Raise(nameof(EjectChecked)); }
    }

    private bool _remount;
    public bool RemountChecked
    {
        get => _remount;
        set { _remount = value; Raise(nameof(RemountChecked)); }
    }

    private bool _canMount = true;     // false when no cartridge is inserted
    public bool CanMount
    {
        get => _canMount;
        set { _canMount = value; Raise(nameof(ButtonEnabled)); }
    }

    private bool _globalEnabled = true; // env resolved, no utility running
    public bool GlobalEnabled
    {
        get => _globalEnabled;
        set { _globalEnabled = value; RaisePhase(); }
    }

    // ---- derived UI state --------------------------------------------------

    public bool OptionsEnabled => _phase == SlotPhase.Idle && _globalEnabled && !_mediaOpRunning;
    public bool RefreshEnabled => !IsBusy && _globalEnabled && !_mediaOpRunning;
    public bool RemountEnabled => _globalEnabled && !_mediaOpRunning;

    public bool ButtonEnabled => _globalEnabled && !_mediaOpRunning && _phase switch
    {
        SlotPhase.Idle => _canMount,
        SlotPhase.Mounting => true,    // acts as Cancel
        SlotPhase.Mounted => true,
        _ => false,
    };

    public string ButtonText => _phase switch
    {
        SlotPhase.Idle => "Mount",
        SlotPhase.Mounting => "Mounting",
        SlotPhase.Mounted => "Unmount",
        _ => "Unmounting...",
    };

    /// <summary>
    /// Cancel (mounting) is a neutral/grey button; Mount and Unmount use the
    /// accent (blue) style. A null Style falls back to the implicit default.
    /// </summary>
    public Style? ButtonStyle => _phase == SlotPhase.Mounting
        ? null
        : (Style)Application.Current.Resources["AccentButtonStyle"];

    public bool IsBusy => _phase is SlotPhase.Mounting or SlotPhase.Unmounting;
    public Visibility SpinnerVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Utilities-card notice, shown while the drive is not idle.</summary>
    public Visibility MountedNoticeVisibility =>
        _phase == SlotPhase.Idle ? Visibility.Collapsed : Visibility.Visible;

    // ---- helpers -----------------------------------------------------------

    /// <summary>Replace the free-letter list, preserving the selection when possible.</summary>
    public void SetLetters(IReadOnlyList<string> letters, string? prefer = null)
    {
        string? keep = prefer ?? SelectedLetter;
        Letters.Clear();
        foreach (var l in letters) Letters.Add(l);
        int idx = keep != null ? letters.ToList().IndexOf(keep) : -1;
        if (idx < 0) idx = letters.ToList().IndexOf("T:");
        if (idx < 0 && letters.Count > 0) idx = letters.Count - 1;
        SelectedLetterIndex = idx;
    }

    /// <summary>Lock the letter list to the mounted letter.</summary>
    public void SetMountedLetter(string letter)
    {
        Letters.Clear();
        Letters.Add(letter);
        SelectedLetterIndex = 0;
    }

    private void RaisePhase()
    {
        Raise(nameof(OptionsEnabled));
        Raise(nameof(RefreshEnabled));
        Raise(nameof(RemountEnabled));
        Raise(nameof(EjectLoadEnabled));
        Raise(nameof(ButtonEnabled));
        Raise(nameof(ButtonText));
        Raise(nameof(ButtonStyle));
        Raise(nameof(IsBusy));
        Raise(nameof(SpinnerVisibility));
        Raise(nameof(MountedNoticeVisibility));
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
