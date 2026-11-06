using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck.Native;

// One administrator confirmation copies the hash-pinned USB-only service to protected Program Files and registers it on demand.
// Interactive users may query/start it, never change its executable or configuration. Registration survives app/Windows restarts
// and portable-folder cleanup. No disk permissions, scheduled tasks or third-party drivers are changed.
internal static class UsbTemperatureSetup
{
    public const string ElevatedSwitch = "--usb-temperature-service", ServiceName = "BrimDeckUsbSensors", ExeName = "BrimDeck.UsbSensors.exe";
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BrimDeck USB Sensors");
    public static string ServiceExe => Path.Combine(Folder, ExeName);
    private static string? ExpectedHash => typeof(UsbTemperatureSetup).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(item => item.Key == "UsbSensorsSha256")?.Value is { Length: 64 } hash ? hash : null;
    private static int _generation;
    public static int Generation => Volatile.Read(ref _generation);
    public static bool Installed
    {
        get
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
                return key?.GetValue("ImagePath") is string path && path.Trim('"').Equals(ServiceExe, StringComparison.OrdinalIgnoreCase) && File.Exists(ServiceExe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
        }
    }
    public static async Task InstallAsync()
    {
        if (Installed) return;
        string candidate = Path.Combine(AppContext.BaseDirectory, ExeName);
        if (ExpectedHash is not { } hash || !File.Exists(candidate) || !Hash(candidate).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.T("安装包缺少完整且校验正确的 USB 温度服务", "The package needs a verified USB temperature service"));
        await ElevateAsync("install").ConfigureAwait(false);
    }
    public static Task UninstallAsync() => ElevateAsync("remove");
    private static async Task ElevateAsync(string action)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(ElevatedSwitch); info.ArgumentList.Add(action);
        Process? process;
        try { process = await Task.Run(() => Process.Start(info)).ConfigureAwait(false); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new InvalidOperationException(Loc.T("未取得管理员授权", "Administrator permission was not given")); }
        using (process)
        {
            if (process is null) throw new InvalidOperationException(Loc.T("无法启动服务安装程序", "Could not start service setup"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            finally { Interlocked.Increment(ref _generation); }
            if (process.ExitCode != 0) throw new InvalidOperationException(Loc.T($"USB 温度服务设置失败（{process.ExitCode}）", $"USB temperature service setup failed ({process.ExitCode})"));
        }
    }
    public static void Start()
    {
        try { using var service = new ServiceController(ServiceName); if (service.Status == ServiceControllerStatus.Stopped) service.Start(); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }
    public static uint ProcessId
    {
        get
        {
            var manager = OpenSCManagerW(null, null, 1);
            if (manager == IntPtr.Zero) return 0;
            try
            {
                var service = OpenServiceW(manager, ServiceName, 4);
                if (service == IntPtr.Zero) return 0;
                try { return QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _) ? status.ProcessId : 0; }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }
    }
    public static int RunElevated(string[] args)
    {
        if (args is not (["install"] or ["remove"])) return 2;
        try { if (args[0] == "install") Install(); else Remove(); return 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or System.Security.SecurityException or System.ServiceProcess.TimeoutException)
        { return ex is InvalidDataException ? 3 : 1; }
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void CheckFolder()
    {
        string programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (Path.GetDirectoryName(Path.GetFullPath(Folder)) != programFiles) throw new InvalidDataException("Invalid service path.");
        foreach (string path in new[] { programFiles, Folder, ServiceExe })
            if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse point in service path.");
    }
    private static void Install()
    {
        if (ExpectedHash is not { } expected) throw new InvalidDataException("Service hash missing.");
        CheckFolder();
        if (Directory.Exists(Folder))
        {
            var owner = new DirectoryInfo(Folder).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (owner is null || !owner.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) && !owner.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)))
                throw new InvalidDataException("Service folder is not administrator-owned.");
        }
        Directory.CreateDirectory(Folder);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.SetOwner(admins);
        foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Folder).SetAccessControl(acl);
        string staged = Path.Combine(Folder, ExeName + "." + Guid.NewGuid().ToString("N") + ".new");
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, ExeName), staged);
            if (!Hash(staged).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Service hash mismatch.");
            StopService(); File.Move(staged, ServiceExe, true); Register();
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
    private static void StopService()
    {
        try { using var service = new ServiceController(ServiceName); if (service.Status != ServiceControllerStatus.Stopped) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)); } }
        catch (InvalidOperationException) { }
    }
    private static void Register()
    {
        var manager = OpenSCManagerW(null, null, 0xF003F);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        try
        {
            string command = "\"" + ServiceExe + "\"";
            var service = OpenServiceW(manager, ServiceName, 0xF01FF);
            if (service == IntPtr.Zero) service = CreateServiceW(manager, ServiceName, "BrimDeck USB Temperature", 0xF01FF, 0x10, 3, 1, command, null, IntPtr.Zero, null, null, null);
            else if (!ChangeServiceConfigW(service, 0x10, 3, 1, command, null, IntPtr.Zero, null, null, null, "BrimDeck USB Temperature")) { CloseServiceHandle(service); throw new Win32Exception(); }
            if (service == IntPtr.Zero) throw new Win32Exception();
            try
            {
                const string rights = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;CCLCRP;;;IU)";
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(rights, 1, out var descriptor, IntPtr.Zero)) throw new Win32Exception();
                try { if (!SetServiceObjectSecurity(service, 4, descriptor)) throw new Win32Exception(); }
                finally { LocalFree(descriptor); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    private static void Remove()
    {
        CheckFolder(); StopService();
        var manager = OpenSCManagerW(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var service = OpenServiceW(manager, ServiceName, 0x10000);
            if (service != IntPtr.Zero) { try { if (!DeleteService(service)) throw new Win32Exception(); } finally { CloseServiceHandle(service); } }
        }
        finally { CloseServiceHandle(manager); }
        if (File.Exists(ServiceExe)) File.Delete(ServiceExe);
        if (Directory.Exists(Folder) && !Directory.EnumerateFileSystemEntries(Folder).Any()) Directory.Delete(Folder);
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Accepted, Error, SpecificError, Checkpoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateServiceW(IntPtr manager, string name, string display, uint access, uint type, uint start, uint error, string command, string? group, IntPtr tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfigW(IntPtr service, uint type, uint start, uint error, string command, string? group, IntPtr tag, string? dependencies, string? account, string? password, string display);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int required);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DeleteService(IntPtr service);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr descriptor, IntPtr size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceObjectSecurity(IntPtr service, uint information, IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
