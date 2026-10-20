using System.Runtime.InteropServices;

namespace DocsViewer.Infrastructure;

internal static class NativeMethods
{
    public const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(int processId);
}
