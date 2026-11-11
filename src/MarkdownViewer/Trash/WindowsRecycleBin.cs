using System.IO;
using System.Runtime.InteropServices;

namespace MarkdownViewer.Trash;

/// <summary>
/// Sends files or folders to the Windows Recycle Bin, the same way Explorer's Delete key does. Uses the Windows
/// shell function SHFileOperation directly, so no extra package is needed.
/// </summary>
public static class WindowsRecycleBin
{
    /// <summary>SHFileOperation function code for "delete".</summary>
    private const uint DeleteOperation = 0x0003;

    /// <summary>No progress window.</summary>
    private const ushort SilentFlag = 0x0004;

    /// <summary>No "are you sure?" question.</summary>
    private const ushort NoConfirmationFlag = 0x0010;

    /// <summary>"Allow undo": this is what makes a delete go to the Recycle Bin instead of being permanent.</summary>
    private const ushort AllowUndoFlag = 0x0040;

    /// <summary>No error message boxes from Windows; failures are reported through the return value instead.</summary>
    private const ushort NoErrorDialogFlag = 0x0400;

    /// <summary>The SHFILEOPSTRUCTW structure of the Windows API (64-bit layout, the only one this app is built for).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr OwnerWindowHandle;
        public uint Function;
        /// <summary>The paths to delete, each ended by "\0", with an extra "\0" at the very end.</summary>
        public string SourcePaths;
        public string? DestinationPaths;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShellFileOperation fileOperation);

    /// <summary>
    /// Moves a file or folder (with everything in it) to the Recycle Bin. Throws if Windows reports a failure.
    /// Note: on a drive without a Recycle Bin (e.g. some network shares) Windows deletes it for good instead.
    /// Call from the UI thread: the shell function expects a thread set up like the UI thread is.
    /// </summary>
    public static void Send(string fileOrFolderPath)
    {
        if (!File.Exists(fileOrFolderPath) && !Directory.Exists(fileOrFolderPath))
        {
            return;
        }

        var fileOperation = new ShellFileOperation
        {
            Function = DeleteOperation,
            // The shell needs a full path (a relative one would be deleted for good) and a double "\0" at the end.
            SourcePaths = Path.GetFullPath(fileOrFolderPath) + "\0\0",
            Flags = (ushort)(AllowUndoFlag | NoConfirmationFlag | SilentFlag | NoErrorDialogFlag),
        };

        int resultCode = SHFileOperation(ref fileOperation);
        if (resultCode != 0 || fileOperation.AnyOperationsAborted)
        {
            throw new IOException($"Windows could not move it to the Recycle Bin (error code {resultCode}): {fileOrFolderPath}");
        }
    }
}
