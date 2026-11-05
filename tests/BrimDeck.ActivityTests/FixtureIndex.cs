using System.Runtime.InteropServices;

internal static class FixtureIndex
{
    internal static void Create(string path, IEnumerable<(string File, string Title)> rows)
    {
        if (sqlite3_open_v2(path, out var database, 6, IntPtr.Zero) != 0) throw new IOException("Could not create synthetic index.");
        try
        {
            static string Sql(string value) => "'" + value.Replace("'", "''") + "'";
            string statement = "CREATE TABLE threads (rollout_path TEXT, title TEXT, updated_at INTEGER, archived INTEGER);"
                + string.Concat(rows.Select(row => $"INSERT INTO threads VALUES ({Sql(row.File)}, {Sql(row.Title)}, 1, 0);"));
            int result = sqlite3_exec(database, statement, IntPtr.Zero, IntPtr.Zero, out var message);
            if (message != IntPtr.Zero) sqlite3_free(message);
            if (result != 0) throw new IOException("Could not populate synthetic index.");
        }
        finally { sqlite3_close(database); }
    }

    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr data, out IntPtr message);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr database);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr memory);
}
