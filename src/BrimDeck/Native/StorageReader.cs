using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BrimDeck.Core;

namespace BrimDeck.Native;

// Local drives for the system monitor (Storage): fixed and removable volumes with letters, their size and free space from the file
// system, the physical drive it lies on from the volume's disk extents, and what Windows says of that drive (model, bus,
// removable media). Optionally each internal solid-state drive's and USB disk's temperature through the storage stack. Each
// volume and drive is opened without read or write access, which a normal user may do; nothing here needs administrator rights.
// Asking a drive for its temperature sends it a command (a few to over a hundred milliseconds, and it leaves its deepest power
// state), and listing the volumes can wait on a slow drive, so everything is read on the thread pool: the volumes every 10
// seconds, the temperatures every 30, never on a tick. Internal hard disks and drives behind a RAID controller are not asked for
// a temperature (Storage.AsksTemperature). Each temperature query is guarded (SensorGuard): a drive whose
// query never answered is not asked again, and a reading stuck on one is given up so the next can start.
// The readings and states are kept while the sampler pauses, so a page opened again has its drives at once.
internal sealed class StorageReader
{
    private const double Every = 10, TemperatureEvery = 30;
    private const int MaximumDrives = 64;
    private volatile IReadOnlyList<StorageDrive> _plain = [], _heated = [];
    private volatile SensorState _drives, _temperatures;
    private volatile int _count = -1;
    private double _readAt = double.NegativeInfinity;
    // Touched only by the reading under way.
    private double _temperaturesAt = double.NegativeInfinity;
    private Dictionary<int, DiskTemperature> _known = [];
    // When the temperatures were last read (a double's bits, so the sampler's thread reads it whole); older than twice their
    // interval they are no longer shown.
    private long _heatedAt = BitConverter.DoubleToInt64Bits(double.NegativeInfinity);
    private int _running;
    // Raised when a reading stuck on a drive is given up (SensorGuard): only the reading of this generation may say it has ended.
    private int _generation;
    private volatile Task _reading = Task.CompletedTask;
    private int _usbGeneration = -1;

    // Whether any local drive with a lettered volume was found, and how many such volumes; whether any drive gave a temperature.
    public SensorState Drives => _drives;
    public int Count => _count;
    public SensorState Temperatures => _temperatures;
    // The reading under way, or a finished task.
    public Task Pending => _reading;
    // The drives as last read, without temperatures.
    public IReadOnlyList<StorageDrive> Listed => _plain;

    // Starts a reading on the thread pool unless one is under way; temperatures: ask the drives for theirs too when they are due.
    public void Start(bool temperatures)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        int generation = Volatile.Read(ref _generation);
        _reading = Task.Run(() =>
        {
            try { Read(temperatures, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, generation); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine(ex); }
            finally { if (Volatile.Read(ref _generation) == generation) Volatile.Write(ref _running, 0); }
        });
    }

    // The latest drives, with their temperatures when asked for; a new reading starts when the last is old, and its result shows
    // from the next tick.
    public IReadOnlyList<StorageDrive> Sample(double now, bool temperatures)
    {
        // Temperatures asked for after the drives were read without them are read at once rather than at the next reading.
        if (now - _readAt >= Every || temperatures && (_temperatures == SensorState.Unknown && _drives != SensorState.Unknown || _usbGeneration != UsbTemperatureAccess.Generation))
        {
            _readAt = now; Start(temperatures);
        }
        return temperatures && now - BitConverter.Int64BitsToDouble(Interlocked.Read(ref _heatedAt)) <= 2 * TemperatureEvery ? _heated : _plain;
    }

    // A reading stuck on a drive's temperature: the next may start.
    private void GiveUp(int generation)
    {
        if (Interlocked.CompareExchange(ref _generation, generation + 1, generation) == generation) Volatile.Write(ref _running, 0);
    }

    // The sampler paused: the next sample reads again at once. The last readings stay until then.
    public void Rest() => _readAt = double.NegativeInfinity;

    private void Read(bool temperatures, double now, int generation)
    {
        var volumes = Volumes();
        var numbers = volumes.Select(item => item.Disk).OfType<int>().Distinct().ToList();
        var disks = new Dictionary<int, StorageDisk>();
        var solid = new HashSet<int>();
        var query = Marshal.AllocHGlobal(QuerySize);
        var output = Marshal.AllocHGlobal(OutputSize);
        try
        {
            foreach (int number in numbers)
            {
                var device = Open($@"\\.\PhysicalDrive{number}");
                if (device == InvalidHandle) continue;
                try
                {
                    if (Describe(device, query, output) is not { } disk) continue;
                    disks[number] = disk;
                    if (Core.Storage.Internal(disk) && SolidState(device, query, output)) solid.Add(number);
                }
                finally { CloseHandle(device); }
            }
            // Not asked for, the temperatures read before are dropped rather than kept to show later as if current.
            if (!temperatures) _known = [];
            else if (now - _temperaturesAt >= TemperatureEvery || _temperatures == SensorState.Unknown || _usbGeneration != UsbTemperatureAccess.Generation)
            {
                _usbGeneration = UsbTemperatureAccess.Generation;
                _temperaturesAt = now;
                var known = new Dictionary<int, DiskTemperature>();
                foreach (int number in numbers.Where(number => disks.TryGetValue(number, out var described) && Core.Storage.AsksTemperature(described)
                    && (solid.Contains(number) || described.Bus == Core.Storage.BusUsb)))
                {
                    var disk = disks[number];
                    var guarded = new GuardedDevice(GuardedReading.DriveTemperature, disk.Model + " | " + disk.Bus, disk.Model.Length > 0 ? disk.Model : "Disk");
                    if (SensorGuard.Stopped(guarded.Reading, guarded.Device)) continue;
                    var device = Open($@"\\.\PhysicalDrive{number}");
                    if (device == InvalidHandle) continue;
                    try
                    {
                        // Given up on, the next reading starts without waiting for this one, and skips the drive.
                        var heat = SensorGuard.Ask(guarded, () => Temperature(device, query, output, disk.Model)
                            ?? (disk.Bus == Core.Storage.BusUsb ? UsbDiskTemperature.Read(number, disk.Model) ?? UsbTemperatureAccess.Read(number, disk.Model) : null), () => GiveUp(generation));
                        if (heat is not null) known[number] = heat with { Number = number };
                    }
                    finally { CloseHandle(device); }
                }
                _known = known;
                Interlocked.Exchange(ref _heatedAt, BitConverter.DoubleToInt64Bits(now));
                if (known.Count > 0) _temperatures = SensorState.Found; else if (_temperatures == SensorState.Unknown) _temperatures = SensorState.None;
            }
        }
        finally { Marshal.FreeHGlobal(query); Marshal.FreeHGlobal(output); }
        // An undescribed fixed drive is left out; removable media still has its file-system capacity when a bridge cannot describe it.
        var described = volumes.Where(item => item.Disk is not { } number || disks.ContainsKey(number) || item.Volume.Removable)
            .Select(item => (item.Volume, Disk: item.Disk is { } number && !disks.ContainsKey(number) ? null : item.Disk)).ToList();
        var plain = Core.Storage.Group(described, disks, solid: solid);
        _plain = plain;
        _heated = Core.Storage.Group(described, disks, _known, solid);
        _count = Core.Storage.VolumeCount(plain);
        _drives = plain.Count > 0 ? SensorState.Found : SensorState.None;
    }

    // Every ready fixed/removable volume with a letter, with the drive it lies on (null when its extents span drives), each volume
    // once. A letter made with subst (a folder of another volume, \??\C:\...) is left out, as is a second letter of a volume already
    // listed (by its volume GUID). Fixed volumes whose extents cannot be read are left out: a VeraCrypt or ImDisk volume is a file
    // or memory on another drive, already counted there, or not a drive at all. Removable media can fall back to its capacity alone.
    public static List<(StorageVolume Volume, int? Disk)> Volumes()
    {
        var volumes = new List<(StorageVolume, int?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extents = Marshal.AllocHGlobal(ExtentsSize);
        var text = new char[1024];
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    bool removable = drive.DriveType == DriveType.Removable;
                    if (drive.DriveType != DriveType.Fixed && !removable || !drive.IsReady) continue;
                    string letter = drive.Name.TrimEnd('\\');
                    if (QueryDosDeviceW(letter, text, text.Length) > 0 && Text(text).StartsWith(@"\??\", StringComparison.Ordinal)) continue;
                    if (GetVolumeNameForVolumeMountPointW(letter + @"\", text, text.Length) && !seen.Add(Text(text))) continue;
                    var placed = Disk(letter, extents);
                    if (!placed.Readable && !removable) continue;
                    string label = "";
                    try { label = drive.VolumeLabel; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    var volume = new StorageVolume(letter, label, (ulong)Math.Max(0, drive.TotalSize), (ulong)Math.Max(0, drive.TotalFreeSpace)) { Removable = removable };
                    volumes.Add((volume, placed.Readable ? placed.Disk : null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex); }
            }
        }
        finally { Marshal.FreeHGlobal(extents); }
        return volumes;
    }

    // VOLUME_DISK_EXTENTS: NumberOfDiskExtents, padding, then DISK_EXTENT entries of 24 bytes from 8 (DiskNumber at 0). A volume on
    // one drive has its extents all on it; one spanning drives (a dynamic or Storage Spaces volume) has no single Disk.
    private static (bool Readable, int? Disk) Disk(string letter, IntPtr extents)
    {
        var volume = Open($@"\\.\{letter}");
        if (volume == InvalidHandle) return (false, null);
        try
        {
            if (!DeviceIoControl(volume, VolumeDiskExtents, IntPtr.Zero, 0, extents, ExtentsSize, out int length, IntPtr.Zero) || length < 32) return (false, null);
            int count = Marshal.ReadInt32(extents);
            if (count < 1 || 8 + count * 24 > length) return (false, null);
            int first = Marshal.ReadInt32(extents, 8);
            for (int i = 1; i < count; i++) if (Marshal.ReadInt32(extents, 8 + i * 24) != first) return (true, null);
            return first is >= 0 and < MaximumDrives ? (true, first) : (false, null);
        }
        finally { CloseHandle(volume); }
    }
    // The text a call wrote, up to its first terminating null.
    private static string Text(char[] text)
    {
        int end = Array.IndexOf(text, '\0');
        return new string(text, 0, end >= 0 ? end : text.Length);
    }

    // STORAGE_DEVICE_DESCRIPTOR: RemovableMedia at 10, the offset of ProductId at 16, BusType at 28.
    internal static StorageDisk? Describe(int number)
    {
        if (number is < 0 or >= MaximumDrives) return null;
        var device = Open($@"\\.\PhysicalDrive{number}");
        if (device == InvalidHandle) return null;
        var query = Marshal.AllocHGlobal(QuerySize); var output = Marshal.AllocHGlobal(OutputSize);
        try { return Describe(device, query, output); }
        finally { Marshal.FreeHGlobal(query); Marshal.FreeHGlobal(output); CloseHandle(device); }
    }

    private static StorageDisk? Describe(IntPtr device, IntPtr query, IntPtr output)
    {
        if (!Property(device, query, output, DeviceProperty, out int length) || length < 32) return null;
        int productAt = Marshal.ReadInt32(output, 16);
        string model = productAt > 0 && productAt < length ? Marshal.PtrToStringAnsi(output + productAt)?.Trim() ?? "" : "";
        return new StorageDisk(model, Marshal.ReadInt32(output, 28), Marshal.ReadByte(output, 10) != 0);
    }

    // DEVICE_SEEK_PENALTY_DESCRIPTOR: IncursSeekPenalty at 8, true for a spinning disk. A drive that does not say is taken for a disk.
    private static bool SolidState(IntPtr device, IntPtr query, IntPtr output) =>
        Property(device, query, output, SeekPenaltyProperty, out int length) && length >= 9 && Marshal.ReadByte(output, 8) == 0;

    // STORAGE_TEMPERATURE_DATA_DESCRIPTOR: CriticalTemperature at 8, WarningTemperature at 10, InfoCount at 12, then the
    // STORAGE_TEMPERATURE_INFO entries from 24, 16 bytes each, Temperature at 2. The first is the drive's own.
    private static DiskTemperature? Temperature(IntPtr device, IntPtr query, IntPtr output, string model)
    {
        if (!Property(device, query, output, TemperatureProperty, out int length) || length < 40 || Marshal.ReadInt16(output, 12) < 1) return null;
        return Core.Temperatures.Disk(model.Length > 0 ? model : "Disk", Marshal.ReadInt16(output, 26), Marshal.ReadInt16(output, 10), Marshal.ReadInt16(output, 8));
    }

    // STORAGE_PROPERTY_QUERY: PropertyId, QueryType (0, the standard query), one byte of parameters.
    private static bool Property(IntPtr device, IntPtr query, IntPtr output, int property, out int length)
    {
        Marshal.WriteInt32(query, 0, property); Marshal.WriteInt32(query, 4, 0); Marshal.WriteInt32(query, 8, 0);
        return DeviceIoControl(device, QueryProperty, query, QuerySize, output, OutputSize, out length, IntPtr.Zero);
    }

    private static IntPtr Open(string path) => CreateFileW(path, 0, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    private const int QuerySize = 12, OutputSize = 1024, ExtentsSize = 8 + 24 * 32;
    private const int DeviceProperty = 0, SeekPenaltyProperty = 7, TemperatureProperty = 52;
    private const uint QueryProperty = 0x2D1400, VolumeDiskExtents = 0x560000, ShareReadWrite = 3, OpenExisting = 3;
    private static readonly IntPtr InvalidHandle = new(-1);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int QueryDosDeviceW(string device, char[] target, int size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, char[] name, int size);
}
