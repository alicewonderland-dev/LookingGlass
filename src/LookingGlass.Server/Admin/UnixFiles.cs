using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace LookingGlass.Server.Admin;

/// <summary>
/// What .NET doesn't say about a file on Linux, for the operator web page's socket and its folder: its type and owner, without
/// following a symbolic link (<c>statx</c>, whose layout is the same on every processor), the service's own user ID, and the
/// umask. Linux only.
/// </summary>
internal static class UnixFiles {
    /// <summary>A file's type (the <c>S_IFMT</c> bits), permissions and owner.</summary>
    public readonly record struct Stat(uint Type, uint Permissions, uint Owner) {
        public bool IsDirectory => this.Type == 0x4000;
        public bool IsSocket => this.Type == 0xC000;
        public bool IsSymbolicLink => this.Type == 0xA000;
    }

    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxTypeModeUid = 0x1 | 0x2 | 0x8;
    private const int ENOENT = 2;

    /// <summary>The file at <paramref name="path"/>, not following a link; null if there is none.</summary>
    /// <exception cref="IOException">It couldn't be looked at (or this system has no <c>statx</c>).</exception>
    public static Stat? Lstat(string path) {
        var buffer = new byte[256];
        int result;
        try {
            result = StatxNative(AtFdCwd, path, AtSymlinkNoFollow, StatxTypeModeUid, buffer);
        } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) {
            throw new IOException($"Couldn't look at {path}: this system has no statx.", ex);
        }

        if (result != 0) {
            var error = Marshal.GetLastPInvokeError();
            return error == ENOENT ? null : throw new IOException($"Couldn't look at {path} (error {error}).");
        }

        // struct statx: stx_uid at 20, stx_mode at 28 (little-endian on the processors the server runs on).
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(28));
        return new Stat(mode & 0xF000u, mode & 0xFFFu, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(20)));
    }

    /// <summary>The user the server runs as.</summary>
    public static uint EffectiveUser() => GetEuid();

    /// <summary>Sets the process's umask, returning the one before.</summary>
    public static uint SetUmask(uint mask) => Umask(mask);

    // DllImport (not LibraryImport, which needs unsafe code): a string and a byte array, both marshalled simply.
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatxNative(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint Umask(uint mask);
}