using System.Runtime.InteropServices;
using System.Text;

namespace MpoFix;

internal enum DesktopStatus { Default, Other, AccessDenied, Unknown }
internal sealed record DesktopObservation(DesktopStatus Status, string? Name, int? NativeError);

internal interface IDesktopNative
{
    IntPtr Open(out int error);
    bool Name(IntPtr handle, StringBuilder buffer, uint bytes, out uint needed, out int error);
    bool Close(IntPtr handle, out int error);
}

internal sealed class DesktopProbe(IDesktopNative native, IDiagnosticSink log)
{
    internal DesktopObservation Observe()
    {
        IntPtr handle = IntPtr.Zero;
        var result = new DesktopObservation(DesktopStatus.Unknown, null, null);
        try
        {
            handle = native.Open(out int error);
            log.Write("desktop.open", $"error={error}, opened={handle != IntPtr.Zero}");
            if (handle == IntPtr.Zero)
                result = new(error == 5 ? DesktopStatus.AccessDenied : DesktopStatus.Unknown, null, error);
            else
            {
                var buffer = new StringBuilder(512);
                bool ok = native.Name(handle, buffer, 1024, out uint needed, out error);
                log.Write("desktop.name", $"error={error}, success={ok}, bytesNeeded={needed}");
                var name = ok && needed <= 1024 ? buffer.ToString() : null;
                result = new(string.IsNullOrWhiteSpace(name) ? DesktopStatus.Unknown :
                    name.Equals("Default", StringComparison.OrdinalIgnoreCase) ? DesktopStatus.Default : DesktopStatus.Other,
                    name, ok ? null : error);
            }
        }
        catch (DiagnosticException) { throw; }
        catch (Exception ex) { log.Write("desktop.exception", Diagnostics.ExceptionDetail(ex)); }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    bool closed = native.Close(handle, out int error);
                    log.Write("desktop.close", $"error={error}, success={closed}");
                    if (!closed) result = result with { Status = DesktopStatus.Unknown, NativeError = error };
                }
                catch (DiagnosticException) { throw; }
                catch (Exception ex)
                {
                    result = result with { Status = DesktopStatus.Unknown };
                    log.Write("desktop.close.exception", Diagnostics.ExceptionDetail(ex));
                }
            }
        }
        log.Write("desktop.result", $"status={result.Status}, name={result.Name ?? "unknown"}; not proof of an unlocked/interactive session");
        return result;
    }
}

internal sealed class Win32DesktopNative : IDesktopNative
{
    // Read only: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openinputdesktop
    public IntPtr Open(out int error)
    {
        var handle = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS only
        error = handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        return handle;
    }
    public bool Name(IntPtr handle, StringBuilder buffer, uint bytes, out uint needed, out int error)
    {
        bool ok = GetUserObjectInformationW(handle, 2, buffer, bytes, out needed); // UOI_NAME
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }
    public bool Close(IntPtr handle, out int error)
    {
        bool ok = CloseDesktop(handle);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder buffer, uint bytes, out uint needed);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr handle);
}
