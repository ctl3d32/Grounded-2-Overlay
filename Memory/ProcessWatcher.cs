using System.Diagnostics;
using G2Overlay.Utils;

namespace G2Overlay.Memory;

/// <summary>
/// Localiza o processo do jogo e a janela principal (classe "UnrealWindow").
/// </summary>
public static class ProcessWatcher
{
    private const string Cat = "Process";

    public static Process? FindProcess(string processName)
    {
        // processName sem ".exe"
        string name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        Process[] list;
        try
        {
            list = Process.GetProcessesByName(name);
        }
        catch (Exception ex)
        {
            Logger.Error(Cat, "Falha ao enumerar processos.", ex);
            return null;
        }

        Process? found = null;
        foreach (var p in list)
        {
            if (found == null) found = p;
            else p.Dispose();
        }

        return found;
    }

    /// <summary>
    /// Retorna a maior janela visível do processo, priorizando a classe "UnrealWindow".
    /// </summary>
    public static IntPtr FindGameWindow(int pid)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        bool bestIsUnreal = false;

        Native.EnumWindowsProc cb = (hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out uint wpid);
            if (wpid != (uint)pid || !Native.IsWindowVisible(hwnd)) return true;

            Native.GetClientRect(hwnd, out var rc);
            long area = (long)rc.Width * rc.Height;
            bool isUnreal = Native.GetClassName(hwnd) == "UnrealWindow";

            if ((isUnreal && !bestIsUnreal) || (isUnreal == bestIsUnreal && area > bestArea))
            {
                best = hwnd;
                bestArea = area;
                bestIsUnreal = isUnreal;
            }

            return true;
        };

        Native.EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return best;
    }

    /// <summary>
    /// Retângulo da área cliente da janela do jogo em coordenadas de tela.
    /// </summary>
    public static bool TryGetClientScreenRect(IntPtr hwnd, out Native.RECT rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || Native.IsIconic(hwnd)) return false;
        if (!Native.GetClientRect(hwnd, out var rc)) return false;

        var tl = new Native.POINT { X = 0, Y = 0 };
        if (!Native.ClientToScreen(hwnd, ref tl)) return false;

        rect = new Native.RECT { Left = tl.X, Top = tl.Y, Right = tl.X + rc.Width, Bottom = tl.Y + rc.Height };
        return rc.Width > 0 && rc.Height > 0;
    }
}
