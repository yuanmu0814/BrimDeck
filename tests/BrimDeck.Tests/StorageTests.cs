using BrimDeck.Core;

// The system monitor's drives: which volumes are listed and on which physical drive, a drive split into several letters, the
// entries the page shows (one per volume, in one or two columns) and the volumes folded into the last one, their figures, their
// temperatures (the drive's, shown on each of its volumes),
// and every combination of missing temperatures, which must leave no gap, zero or dash.
static class StorageTests
{
    private const ulong G = 1UL << 30;
    private static StorageVolume Volume(string letter, double total, double free, string label = "") => new(letter, label, (ulong)(total * G), (ulong)(free * G));
    private static readonly Dictionary<int, StorageDisk> Disks = new()
    {
        [0] = new("YMTC YMSS2ED06D25MC", 17, false), [1] = new("Lecoo L700 1T", 17, false), [2] = new("WD Blue", 11, false),
        [3] = new("Ventoy", 7, true), [4] = new("Msft Virtual Disk", 15, false), [7] = new("Virtual adapter", 14, false), [8] = new("Mounted VHD to remove", 15, true), [5] = new("SD card", 12, false), [6] = new("USB SSD", 7, false)
    };

    public static void Run(Action<string, bool> check)
    {
        Grouping(check);
        Exclusions(check);
        ExternalDrives(check);
        UsbTemperatures(check);
        Lines(check);
        Figures(check);
        Temperatures_(check);
        Missing(check);
        Strip(check);
        Colors(check);
    }

    private static void Grouping(Action<string, bool> check)
    {
        // This machine: C: and D: on one drive, E: on another.
        var drives = Storage.Group([(Volume("D:", 174.7, 55.4, "Data"), 0), (Volume("E:", 953.9, 170.3), 1), (Volume("C:", 300, 54.5, "Windows-SSD"), 0)], Disks);
        check("Storage: volumes group by physical drive, in letter order", drives.Count == 2 && drives[0] is { Number: 0, Letters: "C: D:" } && drives[1] is { Number: 1, Letters: "E:" });
        check("Storage: a split drive sums its volumes", drives[0].Total == (ulong)(300 * G) + (ulong)(174.7 * G) && drives[0].Used == drives[0].Volumes[0].Used + drives[0].Volumes[1].Used);
        check("Storage: the model is the drive's", drives[0].Model == "YMTC YMSS2ED06D25MC" && drives[1].Model == "Lecoo L700 1T");
        var spanned = Storage.Group([(Volume("F:", 2000, 1000), null), (Volume("C:", 300, 50), 0)], Disks);
        check("Storage: a volume without one drive (spanned) is a drive of its own, without a model", spanned.Count == 2 && spanned[1] is { Number: -1, Model: "", Letters: "F:" });
        check("Storage: no volumes, no drives", Storage.Group([], Disks).Count == 0);
        check("Storage: drives ordered by their first letter", Storage.Group([(Volume("G:", 100, 50), 2), (Volume("C:", 300, 50), 0), (Volume("B:", 100, 50), 1)], Disks)
            .Select(drive => drive.Letters).SequenceEqual(["B:", "C:", "G:"]));
        check("Storage: letters sort ignoring case", Storage.Group([(Volume("d:", 100, 50), 0), (Volume("C:", 100, 50), 0)], Disks)[0].Letters == "C: d:");
    }

    private static void Exclusions(Action<string, bool> check)
    {
        var drives = Storage.Group([(Volume("C:", 300, 50), 0), (Volume("R:", .5, .1, "Recovery"), 0), (Volume("V:", 64, 1, "Ventoy"), 3), (Volume("X:", 40, 10), 4),
            (Volume("S:", 32, 16), 5), (Volume("U:", 1000, 500), 6), (Volume("H:", 2000, 1500), 2), (Volume("Z:", 100, 50), 9), (Volume("Q:", 50, 10), 7), (Volume("W:", 50, 10), 8)], Disks);
        check("Storage: USB disks, USB sticks and SD cards are included", new[] { "V:", "S:", "U:" }.All(letter => drives.Any(drive => drive.Letters == letter && drive.External)));
        check("Storage: virtual adapters and removable virtual drives are left out", drives.SelectMany(drive => drive.Volumes).All(volume => volume.Letter is not ("Q:" or "W:")));
        check("Storage: a Dev Drive (a fixed VHDX) stays", drives.Any(drive => drive.Letters == "X:"));
        check("Storage: a small partition (system reserved, recovery) is not listed", drives[0].Letters == "C:");
        check("Storage: a hard disk's space is listed (only its temperature is not asked for)", drives.Any(drive => drive.Letters == "H:"));
        check("Storage: a volume on a drive Windows did not describe is left out", drives.All(drive => drive.Letters != "Z:"));
        check("Storage: internal buses", Storage.Internal(new("", 17, false)) && Storage.Internal(new("", 11, false)) && Storage.Internal(new("", 16, false))
            && !Storage.Internal(new("", 17, true)) && !Storage.Internal(new("", Storage.BusUsb, false)) && !Storage.Internal(new("", Storage.BusVirtual, false))
            && Storage.Internal(new("", Storage.BusFileBackedVirtual, false)) && !Storage.Internal(new("", Storage.BusFileBackedVirtual, true)) && !Storage.Internal(new("", Storage.BusSd, false)) && !Storage.Internal(new("", Storage.BusMmc, false)));
        check("Storage: a drive behind a RAID controller is listed but not asked for its temperature", Storage.Internal(new("", Storage.BusRaid, false))
            && !Storage.AsksTemperature(new("", Storage.BusRaid, false)) && Storage.AsksTemperature(new("", 17, false)) && Storage.AsksTemperature(new("", 11, false)));
        check("Storage: exactly 1 GB is listed", Storage.Group([(new StorageVolume("C:", "", G, 0), 0)], Disks).Count == 1 && Storage.Group([(new StorageVolume("C:", "", G - 1, 0), 0)], Disks).Count == 0);
    }

    private static void ExternalDrives(Action<string, bool> check)
    {
        var smallUsb = Storage.Group([(Volume("U:", .5, .1), 3)], Disks);
        check("USB: even a small ready stick has a capacity reading", smallUsb is [{ External: true }] && smallUsb[0].Volumes[0].Total == G / 2);
        var fallback = Storage.Group([(Volume("V:", .5, .1) with { Removable = true }, null)], Disks);
        check("USB: removable media without physical extents still shows its volume", fallback is [{ Number: -1, External: true, Letters: "V:" }]);
        check("USB: an empty or unready volume has no false capacity", Storage.Group([(Volume("U:", 0, 0), 3)], Disks).Count == 0);
        check("USB: USB bridges allow temperature requests while SD readers do not", Storage.AsksTemperature(Disks[3]) && !Storage.AsksTemperature(Disks[5]) && Storage.AsksTemperature(Disks[6]) && Storage.AsksTemperature(Disks[0]));

        var attached = Storage.Group([(Volume("C:", 300, 50), 0), (Volume("D:", 12000, 9000), 3), (Volume("E:", 1000, 200), 1), (Volume("F:", 12000, 4000), 6)], Disks);
        var detached = Storage.Group([(Volume("C:", 300, 50), 0), (Volume("E:", 1000, 200), 1)], Disks);
        check("USB: all four letters have their own capacity rows", Storage.Rows(attached, 4).Select(row => row.Label).SequenceEqual(["C:", "D:", "E:", "F:"]));
        var readings = CompactMonitor.Normalize([new() { Item = CompactMonitorItems.Storage, Sources = ["D:", "F:"] }]);
        var admission = new StripAdmission();
        var states = SensorStates.Unknown with { Storage = SensorState.Found };
        var snapshot = MonitorSnapshot.Empty(4, "test") with { Storage = attached };
        admission.Update(snapshot, readings, states);
        check("USB: chosen letters join the capsule", admission.Letters(readings[0]).SequenceEqual(["D:", "F:"]));
        admission.Update(snapshot with { Storage = detached }, readings, states);
        check("USB: unplugged letters leave instead of retaining old figures", admission.Letters(readings[0]).Count == 0 && (admission.Hidden & CompactMonitorItems.Storage) != 0);
        admission.Update(snapshot, readings, states);
        check("USB: a later snapshot restores reattached letters", admission.Letters(readings[0]).SequenceEqual(["D:", "F:"]));
    }

    private static void UsbTemperatures(Action<string, bool> check)
    {
        static byte[] Smart(params (byte Id, byte Raw)[] attributes)
        {
            var bytes = new byte[512]; bytes[0] = 1;
            for (int i = 0; i < attributes.Length; i++)
            {
                int offset = 2 + 12 * i;
                bytes[offset] = attributes[i].Id; bytes[offset + 3] = 100; bytes[offset + 4] = 90; bytes[offset + 5] = attributes[i].Raw;
            }
            bytes[511] = unchecked((byte)-bytes.Sum(value => (int)value));
            return bytes;
        }
        var data = Smart((194, 43));
        check("USB SMART: raw Celsius is used rather than the health score", Temperatures.SmartDisk("USB HDD", data)?.Celsius == 43);
        check("USB SMART: disk temperature wins over airflow", Temperatures.SmartDisk("USB HDD", Smart((190, 38), (194, 43)))?.Celsius == 43);
        check("USB SMART: airflow can supply a temperature", Temperatures.SmartDisk("USB HDD", Smart((190, 38)))?.Celsius == 38);
        check("USB SMART: invalid disk temperature can fall back to airflow", Temperatures.SmartDisk("USB HDD", Smart((194, 255), (190, 38)))?.Celsius == 38);
        check("USB SMART: a stick without a temperature stays unavailable", Temperatures.SmartDisk("USB stick", Smart((9, 42))) is null);
        check("USB SMART: zero, invalid and out-of-range sensors stay unavailable", new byte[] { 0, 126, 255 }.All(raw => Temperatures.SmartDisk("USB", Smart((194, raw))) is null));
        check("USB SMART: truncated, oversized and empty responses are rejected", Temperatures.SmartDisk("USB", data.AsSpan(0, 511)) is null
            && Temperatures.SmartDisk("USB", new byte[513]) is null && Temperatures.SmartDisk("USB", new byte[512]) is null);
        data[100] ^= 1;
        check("USB SMART: a corrupted response is rejected", Temperatures.SmartDisk("USB", data) is null);

        var heat = new Dictionary<int, DiskTemperature> { [3] = new("USB HDD", 43) { Number = 3 }, [0] = new("SSD", 52) { Number = 0 } };
        var drives = Storage.Group([(Volume("D:", 12000, 9000), 3), (Volume("F:", 12000, 4000), 6)], Disks, heat);
        check("USB temperature: attached to its own physical disk, not the other USB disk", drives[0] is { External: true, SolidState: false, Temperature.Celsius: 43 } && drives[1].Temperature is null);
        check("USB temperature: page rows use the same temperature field as internal drives", Storage.Rows(drives, 2) is [{ Label: "D:", Celsius: 43 }, { Label: "F:", Celsius: null }]);
        var snapshot = MonitorSnapshot.Empty(4, "test") with { Storage = drives };
        var states = SensorStates.Unknown with { Storage = SensorState.Found, DiskTemperatures = SensorState.Found };
        check("USB temperature: a mechanical USB disk's available temperature is admitted to the capsule", !CompactSources.HeatMissing("D:", snapshot, states) && CompactSources.Heat("D:", snapshot)?.Celsius == 43);
        check("USB temperature: a disk with no sensor stays hidden", CompactSources.HeatMissing("F:", snapshot, states));
        check("USB temperature: turning temperatures off leaves capacity rows", Storage.Rows(drives, 2, temperatures: false).All(row => row.Celsius is null));
    }

    private static List<StorageDrive> Machine(int count) => Storage.Group(
        Enumerable.Range(0, count).Select(i => (Volume(((char)('C' + i)).ToString() + ":", 100 * (i + 1), 10 * (i + 1)), (int?)(i % 3))), Disks);

    private static void Lines(Action<string, bool> check)
    {
        // At each size, as many lines as leave the core columns MinimumGraph of height: none on the smallest page, one at the
        // default size, three on a tall one.
        check("Storage lines: room by what the readings leave", MonitorLayout.For(440, 110, false, 3).StorageLines == 0 && MonitorLayout.For(600, 170, true, 5).StorageLines == 1
            && MonitorLayout.For(620, 160, true, 2).StorageLines == 1 && MonitorLayout.For(700, 290, true, 2).StorageLines == 2 && MonitorLayout.For(700, 330, true, 5).StorageLines == 3
            && MonitorLayout.For(600, 170, true).StorageLines == 0 && MonitorLayout.For(440, 380, true, 3) is { List: ListPlacement.Below, StorageLines: 1 });
        check("Storage lines: memory and network keep their room, so their sparklines keep their width", new[] { 110.0, 160, 170, 249, 250, 290, 400 }
            .All(height => MonitorLayout.For(700, height, true, 3).Metrics == MonitorLayout.For(700, height, true).Metrics) && MonitorLayout.For(700, 290, true, 2).Metrics == MetricsMode.Blocks);
        check("Storage lines: the core columns keep their least height with the drives' lines", new[] { 110.0, 140, 160, 200, 249, 250, 290, 330, 400 }.All(height =>
        {
            var layout = MonitorLayout.For(700, height, true, 3);
            return layout.StorageLines == MonitorLayout.StorageRoom(height, layout.Metrics, layout.Figure, layout.Body)
                && layout.StorageLines is >= 0 and <= 3;
        }));
        // Readings StorageTwoColumns wide or wider put the volumes in two columns, filled line by line; narrower ones keep one.
        check("Storage columns: two where the readings are wide, so the lines are halved", MonitorLayout.For(1320, 290, true, 2) is { StorageLines: 1, StorageColumns: 2 }
            && MonitorLayout.For(1320, 330, true, 5) is { StorageLines: 3, StorageColumns: 2 } && MonitorLayout.For(1320, 330, true, 9) is { StorageLines: 3, StorageColumns: 2 }
            && MonitorLayout.For(600, 330, false, 4) is { StorageLines: 2, StorageColumns: 2 } && MonitorLayout.For(880, 290, true, 2) is { StorageLines: 1, StorageColumns: 2 });
        check("Storage columns: one for a single volume or narrow readings", MonitorLayout.For(1320, 290, true, 1) is { StorageLines: 1, StorageColumns: 1 }
            && MonitorLayout.For(700, 330, true, 5) is { StorageLines: 3, StorageColumns: 1 } && MonitorLayout.For(440, 380, true, 3) is { StorageColumns: 1 });
        check("Storage columns: none without lines", MonitorLayout.For(1320, 110, true, 4) is { StorageLines: 0, StorageColumns: 0 } && MonitorLayout.For(1320, 330, true) is { StorageColumns: 0 });
        var one = Storage.Group([(Volume("C:", 300, 50), 0)], Disks);
        check("Storage lines: one volume, one entry", Storage.Rows(one, 3) is [{ Label: "C:", Others: false }]);
        var split = Storage.Group([(Volume("C:", 300, 50), 0), (Volume("D:", 100, 50), 0)], Disks);
        var entries = Storage.Rows(split, 3);
        check("Storage lines: a split drive gives an entry per volume, each with its own figures",
            entries is [{ Label: "C:" }, { Label: "D:" }] && entries[0].Total == 300 * G && entries[0].Used == 250 * G && entries[1].Total == 100 * G && Math.Abs(entries[1].UsedFraction - .5) < 1e-9
            && entries.All(row => row.Drives.Single().Number == 0));
        var mixed = Storage.Group([(Volume("E:", 100, 50), 0), (Volume("C:", 300, 50), 0), (Volume("D:", 100, 50), 1)], Disks);
        check("Storage lines: volumes in letter order across drives", Storage.Rows(mixed, 6).Select(row => row.Label).SequenceEqual(["C:", "D:", "E:"]));
        var three = Storage.Rows(Machine(3), 3);
        check("Storage lines: three volumes fill three places", three.Count == 3 && three.All(row => !row.Others));
        var five = Storage.Group(Enumerable.Range(0, 5).Select(i => (Volume(((char)('C' + i)).ToString() + ":", 100, 50), (int?)(10 + i))),
            Enumerable.Range(10, 5).ToDictionary(i => i, i => new StorageDisk("", 17, false)));
        var folded = Storage.Rows(five, 3);
        check("Storage lines: the last entry holds the others, counted", folded.Count == 3 && folded[2] is { Label: "+3", Others: true } && folded[2].Drives.Count == 3
            && folded[2].Total == 300 * G);
        check("Storage lines: more volumes than places fold into the last; two places show one volume and the rest", Storage.Rows(five, 2) is [{ Label: "C:" }, { Label: "+4", Others: true }]);
        check("Storage lines: one place names the first volume and counts the rest", Storage.Rows(five, 1) is [{ Label: "C: +4", Others: true }] && Storage.Rows(five, 0).Count == 1);
        var splitFold = Storage.Rows(Storage.Group([(Volume("C:", 300, 50), 0), (Volume("D:", 100, 50), 0), (Volume("E:", 100, 50), 0)], Disks), 2);
        check("Storage lines: the others on one drive count that drive once", splitFold[1] is { Label: "+2", Others: true } && splitFold[1].Drives.Count == 1);
        check("Storage lines: no drives, no entries", Storage.Rows([], 3).Count == 0);
        var empty = Storage.Rows([new StorageDrive(0, "", [new StorageVolume("C:", "", 0, 0)])], 3).Single();
        check("Storage lines: a volume of no size draws an empty bar", empty.UsedFraction == 0);
        check("Storage lines: the volumes counted for the page", Storage.VolumeCount(mixed) == 3 && Storage.VolumeCount([]) == 0);
    }

    private static void Figures(Action<string, bool> check)
    {
        check("Storage figures: gigabytes, whole from 10", Storage.Sizes(212 * G, 477 * G) == ("212", "477", "GB") && Storage.Sizes((ulong)(5.5 * G), 64 * G) == ("5.5", "64", "GB"));
        check("Storage figures: terabytes from 1000 GB, one decimal", Storage.Sizes(400 * G, 1863 * G) == ("0.4", "1.8", "TB") && Storage.Sizes(0, 999 * G) == ("0.0", "999", "GB"));
        check("Storage figures: a volume's used space never passes its size", new StorageVolume("C:", "", 10, 20).Used == 0 && new StorageVolume("C:", "", 10, 20).UsedFraction == 0);
        check("Storage figures: the Windows drive", Storage.WindowsLetter is { Length: 2 } letter && letter[1] == ':');
        var drives = Storage.Group([(Volume("C:", 300, 50), 0), (Volume("E:", 900, 100), 1)], Disks);
        check("Storage figures: the system drive by its letter, else the first", Storage.System(drives, "E:")?.Number == 1 && Storage.System(drives, "Q:")?.Number == 0
            && Storage.System(drives, null)?.Number == 0 && Storage.System([], "C:") is null);
    }

    private static void Temperatures_(Action<string, bool> check)
    {
        var heat = new Dictionary<int, DiskTemperature> { [0] = new DiskTemperature("YMTC", 44, 80, 85) { Number = 0 } };
        var drives = Storage.Group([(Volume("C:", 300, 50), 0), (Volume("D:", 100, 50), 0), (Volume("E:", 900, 100), 1)], Disks, heat);
        check("Storage temperatures: the drive's, shared by its volumes' line", drives[0].Temperature?.Celsius == 44 && drives[1].Temperature is null);
        var rows = Storage.Rows(drives, 3);
        check("Storage temperatures: each volume shows its drive's", rows is [{ Label: "C:", Celsius: 44 }, { Label: "D:", Celsius: 44 }, { Label: "E:", Celsius: null }]);
        check("Storage temperatures: left off when temperatures are off", Storage.Rows(drives, 3, temperatures: false).All(row => row.Celsius is null));
        var hot = new Dictionary<int, DiskTemperature> { [10] = new("A", 50, 80, 85), [11] = new("B", 82, 80, 85), [12] = new("C", 75, 70, 90) };
        var five = Storage.Group(Enumerable.Range(0, 3).Select(i => (Volume(((char)('C' + i)).ToString() + ":", 100, 50), (int?)(10 + i))),
            Enumerable.Range(10, 3).ToDictionary(i => i, i => new StorageDisk("", 17, false)), hot);
        var folded = Storage.Rows(five, 2);
        check("Storage temperatures: the others' line takes the highest level, then the hottest", folded[1] is { Others: true, Celsius: 82, Level: MemoryLevel.Elevated });
        check("Storage temperatures: a line's level is its drive's", Storage.Rows(five, 3).Select(row => row.Level).SequenceEqual([MemoryLevel.Normal, MemoryLevel.Elevated, MemoryLevel.Elevated]));
    }

    // Every combination of the processor's, the graphics adapter's and the drives' temperatures: what is missing leaves nothing
    // behind (an empty detail, no temperature on a line), never a zero or a dash, and with none the temperatures vanish entirely.
    private static void Missing(Action<string, bool> check)
    {
        var combinations = new (string Name, double? Cpu, double? Gpu, bool Drives)[]
        {
            ("all", 61, 48, true), ("GPU only", null, 48, false), ("drives only", null, null, true), ("CPU only", 61, null, false),
            ("GPU and drives", null, 48, true), ("none", null, null, false), ("CPU and GPU", 61, 48, false), ("CPU and drives", 61, null, true)
        };
        foreach (var (name, cpu, gpu, withDrives) in combinations)
            foreach (int count in new[] { 1, 2, 3, 5 })
            {
                var heat = withDrives ? Enumerable.Range(10, count).ToDictionary(i => i, i => new DiskTemperature("SSD", 40 + i, 80, 85) { Number = i }) : null;
                var drives = Storage.Group(Enumerable.Range(0, count).Select(i => (Volume(((char)('C' + i)).ToString() + ":", 100, 50), (int?)(10 + i))),
                    Enumerable.Range(10, count).ToDictionary(i => i, i => new StorageDisk("", 17, false)), heat);
                var snapshot = MonitorSnapshot.Empty(4, "test") with
                {
                    Cpu = new CpuSample([new CoreSample(0, 0, CoreTier.Unknown, 1, .2, Reading<double>.Unavailable(""), null)], Reading<double>.Unavailable("")) { Temperature = cpu },
                    Gpu = new GpuSample(.1, Reading<ulong>.Unavailable("")) { Temperature = gpu }, Storage = drives
                };
                var rows = Storage.Rows(snapshot.Storage, 3);
                string cpuDetail = Temperatures.Detail(snapshot.Cpu.OrDefault?.Temperature), gpuDetail = Temperatures.Detail(snapshot.Gpu.OrDefault?.Temperature);
                bool lines = rows.All(row => (row.Celsius is not null) == withDrives) && rows.Count == Math.Min(count, 3);
                bool details = (cpuDetail.Length > 0) == (cpu is not null) && (gpuDetail.Length > 0) == (gpu is not null)
                    && !cpuDetail.Contains('—') && !gpuDetail.Contains('—') && !cpuDetail.StartsWith('0') && !gpuDetail.StartsWith('0');
                bool strip = (CompactFigures.Heat(CompactMonitorItems.Cpu, snapshot) is null) == (cpu is null) && (CompactFigures.Heat(CompactMonitorItems.Gpu, snapshot) is null) == (gpu is null);
                check($"Missing temperatures ({name}, {count} drive(s)): nothing left behind", lines && details && strip);
            }
        check("Missing temperatures: an empty detail for no reading", Temperatures.Detail(null) == "" && Temperatures.Detail(47.4) == "47 °C");
    }

    private static void Strip(Action<string, bool> check)
    {
        const CompactMonitorItems Storage_ = CompactMonitorItems.Storage;
        var defaults = new DeckSettings(); defaults.Normalize();
        check("Compact drive: hidden by default, not part of Full", defaults.CompactReadings.Single(r => r.Item == Storage_) is { Visible: false }
            && (CompactMonitorItems.All & Storage_) == 0 && defaults.MonitorStorage && CompactMonitor.Channels(Storage_) == 1 && !CompactMonitor.HasValueChoice(Storage_));
        var readings = CompactMonitor.Normalize(defaults.CompactReadings);
        readings.Single(r => r.Item == Storage_).Sources = ["D:"];
        CompactMonitor.Apply(readings, CompactMonitorMode.Full);
        check("Compact drive: Full shows it with both figures and keeps its letters", (CompactMonitor.Shown(readings) & Storage_) != 0 && CompactMonitor.Preset(readings) == CompactMonitorMode.Full
            && readings.Single(r => r.Item == Storage_) is { Figures: [CompactFigures.Usage, CompactFigures.Temperature], Sources: ["D:"] });
        readings.Single(r => r.Item == Storage_).Figures = [CompactFigures.Usage];
        check("Compact drive: without its temperature, Full no longer matches", CompactMonitor.Preset(readings) is null);
        CompactMonitor.Apply(readings, CompactMonitorMode.Brief);
        check("Compact drive: Brief hides it", !readings.Single(r => r.Item == Storage_).Visible);
        var width = CompactMonitor.Widths(CompactMonitor.Rings, 12, _ => 30, 14, 20, 50, 40);
        // The machine's power and battery and the frame rate are left out: they leave before both.
        var every = CompactMonitor.Every & ~CompactMonitorItems.Power & ~CompactMonitorItems.Charge & ~CompactMonitorItems.Frames & ~CompactMonitorItems.Fans;
        double full = CompactMonitor.Width(every, width, 10);
        check("Compact drive: its own width; it leaves first when the row is short", width(Storage_) == 40 && CompactMonitor.Fit(every, width, 10, full) == every
            && CompactMonitor.Fit(every, width, 10, full - 1) == CompactMonitorItems.All && CompactMonitor.Fit(every, width, 10, full - 55) == (CompactMonitorItems.All & ~CompactMonitorItems.Upload));
        var drives = Storage.Group([(Volume("C:", 100, 5), 0)], Disks);
        var snapshot = MonitorSnapshot.Empty(4, "test") with { Storage = drives };
        check("Compact drive: the level of the Windows drive's space", ReadingLevels.Of(Storage_, snapshot) == MemoryLevel.Critical
            && ReadingLevels.Of(Storage_, MonitorSnapshot.Empty(4, "test")) == MemoryLevel.Normal && ReadingLevels.Of(Storage_, null) == MemoryLevel.Normal);
        var off = SettingsMigrations.Read("""{"AppVersion":"1.2.0","MonitorStorage":false}""");
        check("Storage setting: saved off stays off", !off.MonitorStorage);
    }

    // The drives' bars take the scheme's memory color, so every scheme (and the panel theme's own, and a custom one) colors them.
    private static void Colors(Action<string, bool> check)
    {
        var schemes = Enum.GetValues<MonitorPalette>().SelectMany(palette => new[] { MonitorColors.For(palette, true), MonitorColors.For(palette, false) })
            .Append(MonitorColors.FromTheme("#112233", ["#445566", "#778899", "#AABBCC"], "#FFFF00", "#FF0000")).ToList();
        check("Storage colors: the scheme's memory color in every scheme", schemes.All(scheme => scheme.Storage == scheme.Memory));
        var custom = new MonitorCustomColors { Base = MonitorPalette.Warm, Memory = "#123456" }.Over(MonitorColors.For(MonitorPalette.Warm));
        check("Storage colors: a custom memory color colors the drives too", custom.Storage == "#123456");
        check("Storage colors: the drive reading in the collapsed row takes the memory role", new StripColors(MonitorColors.For(MonitorPalette.Sea)).Normal(CompactMonitorItems.Storage) == MonitorColors.For(MonitorPalette.Sea).Memory);
        check("Storage colors: levels take the scheme's warning and critical", schemes.All(scheme => scheme.Level(MemoryLevel.Elevated, scheme.Storage) == scheme.Warning
            && scheme.Level(MemoryLevel.Critical, scheme.Storage) == scheme.Critical && scheme.Level(MemoryLevel.Normal, scheme.Storage) == scheme.Storage));
    }
}
