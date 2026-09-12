using System;
using System.Reflection;
using System.Runtime.InteropServices;
using NMaier.SimpleDlna.Utilities;

namespace NMaier.SimpleDlna
{
  internal class ProgramIcon : Logging, IDisposable
  {
    private readonly IntPtr oldLg = IntPtr.Zero;
    private readonly IntPtr oldSm = IntPtr.Zero;
    private IntPtr window = IntPtr.Zero;

    public ProgramIcon()
    {
      // Setting the console window icon is a Windows console concept built on
      // kernel32/user32. Attempting it elsewhere only produces a
      // DllNotFoundException and a stack trace in the log on every startup.
      if (!OperatingSystem.IsWindows()) {
        return;
      }
      try {
        window = SafeNativeMethods.GetConsoleWindow();
        if (window == IntPtr.Zero) {
          throw new Exception("Cannot get console window");
        }
        // IL3002: GetHINSTANCE returns -1 for a module with no file on disk,
        // which is the case under single-file publish. LoadImage then fails,
        // the catch below logs it at debug level and the console keeps its
        // default icon. Purely cosmetic, so it is not worth special casing.
#pragma warning disable IL3002
        var inst = Marshal.GetHINSTANCE(
          Assembly.GetEntryAssembly().GetModules()[0]);
#pragma warning restore IL3002
        var iconLg = SafeNativeMethods.LoadImage(inst, "#32512", 1, 0, 0, 0x40);
        if (iconLg == IntPtr.Zero) {
          throw new Exception("Failed to load large icon");
        }
        var desired = SafeNativeMethods.GetSystemMetrics(49);
        var iconSm = SafeNativeMethods.LoadImage(
          inst, "#32512", 1, desired, desired, 0);
        if (iconLg == IntPtr.Zero) {
          throw new Exception("Failed to load small icon");
        }
        oldLg = SafeNativeMethods.SendMessage(
          window, SafeNativeMethods.WM_SETICON, new IntPtr(1), iconLg);
        oldSm = SafeNativeMethods.SendMessage(
          window, SafeNativeMethods.WM_SETICON, IntPtr.Zero, iconSm);
      }
      catch (Exception ex) {
        Debug("Couldnd't set icon", ex);
      }
    }

    public void Dispose()
    {
      GC.SuppressFinalize(this);
      try {
        if (window == IntPtr.Zero) {
          return;
        }
        if (oldLg != IntPtr.Zero) {
          SafeNativeMethods.SendMessage(
            window, SafeNativeMethods.WM_SETICON, new IntPtr(1), oldLg);
        }
        if (oldSm != IntPtr.Zero) {
          SafeNativeMethods.SendMessage(
            window, SafeNativeMethods.WM_SETICON, IntPtr.Zero, oldSm);
        }
        window = IntPtr.Zero;
      }
      catch (Exception ex) {
        Debug("Couldn't restore icon", ex);
      }
    }

    ~ProgramIcon()
    {
      Dispose();
    }
  }
}
