using System.Runtime.InteropServices;
using BrimDeck.Core;

namespace BrimDeck.Native;

// SATA disks behind a USB bridge often do not implement StorageDeviceTemperatureProperty. SAT lets the bridge forward a
// SMART READ DATA command instead. Both CDBs below are data-in only; no SMART enable, test, power or configuration command is sent.
// SCSI_PASS_THROUGH requires a read/write handle even for reads. If Windows denies that handle, the reading stays unavailable;
// no elevation or driver is requested. The caller runs this inside the drive's background SensorGuard, at the temperature cadence.
// Layout: https://learn.microsoft.com/windows-hardware/drivers/ddi/ntddscsi/ns-ntddscsi-_scsi_pass_through
// SAT: https://www.t10.org/ftp/t10/document.04/04-262r8.pdf
internal static class UsbDiskTemperature
{
    public static DiskTemperature? Read(int number, string model)
    {
        if (StorageReader.Describe(number) is not { Bus: Storage.BusUsb } disk || disk.Model != model) return null;
        var device = CreateFileW($@"\\.\PhysicalDrive{number}", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (device == new IntPtr(-1)) return null;
        try
        {
            // ATA PASS-THROUGH (16), then (12) for bridges that only implement the older CDB. PIO data-in, one 512-byte sector,
            // SMART feature D0h (READ DATA), signature 4Fh/C2h and ATA command B0h. CK_COND is clear: successful reads return GOOD.
            byte[][] commands = [
                [0x85, 0x08, 0x0E, 0, 0xD0, 0, 1, 0, 0, 0, 0x4F, 0, 0xC2, 0, 0xB0, 0],
                [0xA1, 0x08, 0x0E, 0xD0, 1, 0, 0x4F, 0xC2, 0, 0xB0, 0, 0]
            ];
            foreach (var command in commands)
                if (ReadData(device, command) is { } data && Core.Temperatures.SmartDisk(model, data) is { } heat) return heat;
            return null;
        }
        finally { CloseHandle(device); }
    }

    private static byte[]? ReadData(IntPtr device, byte[] command)
    {
        const int senseLength = 32, dataOffset = 512, dataLength = 512, packetLength = dataOffset + dataLength;
        int headerLength = Marshal.SizeOf<ScsiPassThrough>();
        var packet = Marshal.AllocHGlobal(packetLength);
        try
        {
            Marshal.Copy(new byte[packetLength], 0, packet, packetLength);
            var cdb = new byte[16]; command.CopyTo(cdb, 0);
            var request = new ScsiPassThrough
            {
                Length = (ushort)headerLength, CdbLength = (byte)command.Length, SenseInfoLength = senseLength, DataIn = 1,
                DataTransferLength = dataLength, TimeOutValue = 3, DataBufferOffset = (nuint)dataOffset,
                SenseInfoOffset = (uint)headerLength, Cdb = cdb
            };
            Marshal.StructureToPtr(request, packet, false);
            if (!DeviceIoControl(device, 0x4D004, packet, packetLength, packet, packetLength, out int returned, IntPtr.Zero)
                || returned < packetLength) return null;
            var response = Marshal.PtrToStructure<ScsiPassThrough>(packet);
            if (response.ScsiStatus != 0 || response.DataTransferLength != dataLength) return null;
            var data = new byte[dataLength]; Marshal.Copy(packet + dataOffset, data, 0, data.Length);
            return data;
        }
        finally { Marshal.FreeHGlobal(packet); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiPassThrough
    {
        public ushort Length;
        public byte ScsiStatus, PathId, TargetId, Lun, CdbLength, SenseInfoLength, DataIn;
        public uint DataTransferLength, TimeOutValue;
        public nuint DataBufferOffset;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Cdb;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
