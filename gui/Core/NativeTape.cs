using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LTOG.Gui.Core;

public record TapeDrive(string Device, string Vendor, string Product, string Serial)
{
    /// <summary>"TAPE0 — HP Ultrium 5-SCSI (HUJ5153GNF)"</summary>
    public string Display =>
        string.IsNullOrEmpty(Serial)
            ? $"{Device} — {Vendor} {Product}"
            : $"{Device} — {Vendor} {Product} ({Serial})";
    public override string ToString() => Display;
}

public enum CartridgeState { DriveInUse, NotReady, NoMedia, NotLtfs, Ltfs }

/// <param name="LtoGeneration">e.g. "LTO-5", from the medium's density code; null if unknown.</param>
/// <param name="WriteProtected">true when the cartridge's physical write-protect tab is engaged.</param>
public record CartridgeInfo(CartridgeState State, string? VolumeName, string? FormatVersion,
    string? LtoGeneration = null, bool WriteProtected = false);

/// <summary>
/// Native tape device access: enumeration via IOCTL_STORAGE_QUERY_PROPERTY and
/// physical load/eject via the Win32 PrepareTape API (no SCSI pass-through needed).
/// </summary>
public static class NativeTape
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint TAPE_LOAD = 0;
    private const uint TAPE_UNLOAD = 1;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint PrepareTape(SafeFileHandle handle, uint operation, bool immediate);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetTapeStatus(SafeFileHandle handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        ref SptdWithSense inBuf, int inLen, ref SptdWithSense outBuf, int outLen,
        out int returned, IntPtr overlapped);

    private static SafeFileHandle Open(string device) =>
        CreateFile($@"\\.\{device}", GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>
    /// Zero-access open: enough for IOCTL_STORAGE_QUERY_PROPERTY and succeeds
    /// even while a mount holds the device locked (FSCTL_LOCK_VOLUME).
    /// </summary>
    private static SafeFileHandle OpenQuery(string device) =>
        CreateFile($@"\\.\{device}", 0,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>Probe \\.\TAPE0 .. \\.\TAPE9 and read vendor/product/serial.</summary>
    public static List<TapeDrive> Enumerate(IActivityLog? log = null)
    {
        var drives = new List<TapeDrive>();
        for (int i = 0; i < 10; i++)
        {
            string dev = $"TAPE{i}";
            using var h = OpenQuery(dev);
            if (h.IsInvalid)
                continue;

            // STORAGE_PROPERTY_QUERY { StorageDeviceProperty, PropertyStandardQuery }
            var query = new byte[12];
            var buf = new byte[1024];
            string vendor = "", product = "", serial = "";
            if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length,
                                buf, buf.Length, out _, IntPtr.Zero))
            {
                vendor = ReadAnsiAt(buf, BitConverter.ToInt32(buf, 12));
                product = ReadAnsiAt(buf, BitConverter.ToInt32(buf, 16));
                serial = ReadAnsiAt(buf, BitConverter.ToInt32(buf, 24));
            }
            drives.Add(new TapeDrive(dev, vendor, product, serial));
        }
        log?.Read("Enumerate tape drives",
            @"IOCTL_STORAGE_QUERY_PROPERTY  \\.\TAPE0 .. \\.\TAPE9",
            drives.Count == 0 ? new[] { "No tape drives detected." }
                              : drives.Select(d => d.Display).ToArray(),
            key: "tape-drives");
        return drives;
    }

    private static string ReadAnsiAt(byte[] buf, int offset)
    {
        if (offset <= 0 || offset >= buf.Length) return "";
        int end = offset;
        while (end < buf.Length && buf[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
    }

    /// <summary>Load (thread) the cartridge currently in the drive.</summary>
    public static void Load(string device) => Prepare(device, TAPE_LOAD);

    /// <summary>Unload and eject the cartridge.</summary>
    public static void Eject(string device) => Prepare(device, TAPE_UNLOAD);

    private static void Prepare(string device, uint op)
    {
        using var h = Open(device);
        if (h.IsInvalid)
            throw new IOException($@"Cannot open \\.\{device} (is it in use by a mount?)");
        uint err = PrepareTape(h, op, false);
        if (err != 0)
            throw new IOException($"PrepareTape failed (win32 error {err})");
    }

    // ---------------------------------------------------------------- MAM

    private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;
    private const byte SCSIOP_MODE_SENSE10 = 0x5A;         // physical WP + medium density code
    private const ushort MAM_APP_FORMAT_VERSION = 0x080B;  // present => LTFS-formatted
    private const ushort MAM_USR_MED_TXT_LABEL = 0x0803;   // LTFS volume name (160 bytes)
    private const uint ERROR_NO_MEDIA_IN_DRIVE = 1112;
    private const uint ERROR_MEDIA_CHANGED = 1110;

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiPassThroughDirect
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SptdWithSense
    {
        public ScsiPassThroughDirect Sptd;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Sense;
    }

    /// <summary>
    /// SCSI READ ATTRIBUTE from the cartridge memory (MAM). No tape motion.
    /// Returns the attribute value bytes or null if absent/unreadable.
    /// </summary>
    private static byte[]? ReadMamAttribute(SafeFileHandle h, ushort attributeId)
    {
        const int bufLen = 512;
        IntPtr data = Marshal.AllocHGlobal(bufLen);
        try
        {
            var s = new SptdWithSense
            {
                Sptd = new ScsiPassThroughDirect
                {
                    Length = (ushort)Marshal.SizeOf<ScsiPassThroughDirect>(),
                    CdbLength = 16,
                    SenseInfoLength = 32,
                    DataIn = 1, // SCSI_IOCTL_DATA_IN
                    DataTransferLength = bufLen,
                    TimeOutValue = 30,
                    DataBuffer = data,
                    SenseInfoOffset = (uint)Marshal.OffsetOf<SptdWithSense>(nameof(SptdWithSense.Sense)),
                    Cdb = new byte[16],
                },
                Sense = new byte[32],
            };
            s.Sptd.Cdb[0] = 0x8C;                        // READ ATTRIBUTE
            s.Sptd.Cdb[1] = 0x00;                        // service action: attribute values
            s.Sptd.Cdb[8] = (byte)(attributeId >> 8);    // first attribute id (BE)
            s.Sptd.Cdb[9] = (byte)(attributeId & 0xFF);
            s.Sptd.Cdb[12] = (byte)(bufLen >> 8);        // allocation length (BE, bytes 10..13)
            s.Sptd.Cdb[13] = (byte)(bufLen & 0xFF);

            int size = Marshal.SizeOf<SptdWithSense>();
            if (!DeviceIoControl(h, IOCTL_SCSI_PASS_THROUGH_DIRECT, ref s, size, ref s, size,
                                 out _, IntPtr.Zero) || s.Sptd.ScsiStatus != 0)
                return null;

            var buf = new byte[bufLen];
            Marshal.Copy(data, buf, 0, bufLen);
            // Response: 4-byte available length, then entries of id(2) fmt(1) len(2) value
            int id = (buf[4] << 8) | buf[5];
            if (id != attributeId) return null;
            int valLen = (buf[7] << 8) | buf[8];
            if (valLen <= 0 || 9 + valLen > bufLen) return null;
            return buf[9..(9 + valLen)];
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>
    /// SCSI MODE SENSE(10) with the block descriptor. The mode-parameter header
    /// carries the physical Write-Protect bit (device-specific parameter, bit 7)
    /// and the 8-byte block descriptor that follows it carries the medium's
    /// density code, which identifies the LTO generation. No tape motion.
    /// Returns null if the command fails.
    /// </summary>
    private static (bool WriteProtected, byte DensityCode)? ReadModeParams(SafeFileHandle h)
    {
        const int bufLen = 64;
        IntPtr data = Marshal.AllocHGlobal(bufLen);
        try
        {
            var s = new SptdWithSense
            {
                Sptd = new ScsiPassThroughDirect
                {
                    Length = (ushort)Marshal.SizeOf<ScsiPassThroughDirect>(),
                    CdbLength = 10,
                    SenseInfoLength = 32,
                    DataIn = 1, // SCSI_IOCTL_DATA_IN
                    DataTransferLength = bufLen,
                    TimeOutValue = 30,
                    DataBuffer = data,
                    SenseInfoOffset = (uint)Marshal.OffsetOf<SptdWithSense>(nameof(SptdWithSense.Sense)),
                    Cdb = new byte[16],
                },
                Sense = new byte[32],
            };
            s.Sptd.Cdb[0] = SCSIOP_MODE_SENSE10;   // MODE SENSE(10)
            s.Sptd.Cdb[1] = 0x00;                  // DBD=0: include the block descriptor
            s.Sptd.Cdb[2] = 0x3F;                  // PC=current values, page 0x3F (all pages)
            s.Sptd.Cdb[7] = (byte)(bufLen >> 8);   // allocation length (BE)
            s.Sptd.Cdb[8] = (byte)(bufLen & 0xFF);

            int size = Marshal.SizeOf<SptdWithSense>();
            if (!DeviceIoControl(h, IOCTL_SCSI_PASS_THROUGH_DIRECT, ref s, size, ref s, size,
                                 out _, IntPtr.Zero) || s.Sptd.ScsiStatus != 0)
                return null;

            var buf = new byte[bufLen];
            Marshal.Copy(data, buf, 0, bufLen);
            // Header: [3] device-specific parameter (bit 7 = WP),
            //         [6..7] block descriptor length. The block descriptor follows the
            //         8-byte header; its first byte ([8]) is the density code.
            bool wp = (buf[3] & 0x80) != 0;
            int bdLen = (buf[6] << 8) | buf[7];
            byte density = bdLen >= 8 ? buf[8] : (byte)0;
            return (wp, density);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>Map an LTO medium density code to its generation name.</summary>
    private static string? LtoGenerationName(byte density) => density switch
    {
        0x40 => "LTO-1",
        0x42 => "LTO-2",
        0x44 => "LTO-3",
        0x46 => "LTO-4",
        0x58 => "LTO-5",
        0x5A => "LTO-6",
        0x5C => "LTO-7",
        0x5D => "LTO-8",   // Type M (M8): LTO-7 media formatted at LTO-8 density
        0x5E => "LTO-8",
        0x60 => "LTO-9",
        _ => null,
    };

    /// <summary>
    /// Identify the cartridge in a drive without mounting: media presence via
    /// GetTapeStatus, LTFS detection and volume name via MAM attributes.
    /// </summary>
    public static CartridgeInfo ReadCartridgeInfo(string device, IActivityLog? log = null)
    {
        var info = ReadCartridgeInfoCore(device);
        log?.Read($"Read cartridge — {device}",
            $@"GetTapeStatus + SCSI READ ATTRIBUTE (MAM 0x080B, 0x0803)  \\.\{device}",
            new[] { DescribeCartridge(info) },
            key: $"cartridge:{device}");
        return info;
    }

    private static string DescribeCartridge(CartridgeInfo info)
    {
        string extra = (info.LtoGeneration is { } g ? $" [{g}]" : "")
                     + (info.WriteProtected ? " [Write-Protected]" : "");
        return info.State switch
        {
            CartridgeState.DriveInUse => "Drive busy — cannot open (in use by another program or a mount).",
            CartridgeState.NotReady => "Drive not ready.",
            CartridgeState.NoMedia => "No cartridge present.",
            CartridgeState.NotLtfs => "Cartridge present — not LTFS formatted." + extra,
            CartridgeState.Ltfs => $"LTFS cartridge: {info.VolumeName ?? "(unlabelled)"}, format {info.FormatVersion ?? "?"}" + extra,
            _ => info.State.ToString(),
        };
    }

    private static CartridgeInfo ReadCartridgeInfoCore(string device)
    {
        using var h = Open(device);
        if (h.IsInvalid)
            return new CartridgeInfo(CartridgeState.DriveInUse, null, null);

        uint status = GetTapeStatus(h);
        if (status == ERROR_MEDIA_CHANGED)
            status = GetTapeStatus(h);          // first call after a change reports it once
        if (status == ERROR_NO_MEDIA_IN_DRIVE)
            return new CartridgeInfo(CartridgeState.NoMedia, null, null);
        if (status != 0)
            return new CartridgeInfo(CartridgeState.NotReady, null, null);

        // Physical write-protect and LTO generation come from one MODE SENSE(10).
        // They apply whether or not the cartridge carries an LTFS volume.
        var mp = ReadModeParams(h);
        bool wp = mp?.WriteProtected ?? false;
        string? gen = mp is { } p ? LtoGenerationName(p.DensityCode) : null;

        var fmtRaw = ReadMamAttribute(h, MAM_APP_FORMAT_VERSION);
        if (fmtRaw == null || fmtRaw.All(b => b is 0 or 0x20))
            return new CartridgeInfo(CartridgeState.NotLtfs, null, null, gen, wp);
        string version = System.Text.Encoding.ASCII.GetString(fmtRaw).Trim('\0', ' ');

        string? name = null;
        var label = ReadMamAttribute(h, MAM_USR_MED_TXT_LABEL);
        if (label != null)
            name = System.Text.Encoding.ASCII.GetString(label).Trim('\0', ' ');
        return new CartridgeInfo(CartridgeState.Ltfs,
            string.IsNullOrEmpty(name) ? null : name,
            string.IsNullOrEmpty(version) ? null : version,
            gen, wp);
    }

    /// <summary>Unused drive letters D:..Z: (A/B/C never offered).</summary>
    public static List<string> UnusedLetters()
    {
        uint mask = GetLogicalDrives();
        var free = new List<string>();
        for (int i = 3; i < 26; i++)
            if ((mask & (1u << i)) == 0)
                free.Add($"{(char)('A' + i)}:");
        return free;
    }
}
