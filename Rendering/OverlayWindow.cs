using System.Runtime.InteropServices;
using G2Overlay.Memory;
using G2Overlay.Utils;

namespace G2Overlay.Rendering;

/// <summary>
/// Janela do overlay: popup sem borda, transparente por pixel (DWM), sempre no topo,
/// fora da barra de tarefas e — com o menu fechado — totalmente "click-through" (WS_EX_TRANSPARENT).
/// </summary>
public sealed unsafe class OverlayWindow : IDisposable
{
    private const string Cat = "Window";
    private const string ClassName = "G2OverlayWindowClass";

    public IntPtr Handle { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool Visible { get; private set; }
    public bool ClickThrough { get; private set; } = true;
    public bool CloseRequested { get; private set; }

    private static OverlayWindow? _instance;
    private readonly LogThrottle _topmostThrottle = new(TimeSpan.FromMilliseconds(500));

    public bool Create()
    {
        _instance = this;
        IntPtr hInstance = Native.GetModuleHandleW(IntPtr.Zero);

        var wc = new Native.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            hInstance = hInstance,
            hCursor = Native.LoadCursorW(IntPtr.Zero, Native.IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName,
        };

        if (Native.RegisterClassExW(ref wc) == 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 1410) // ERROR_CLASS_ALREADY_EXISTS
            {
                Logger.Error(Cat, $"RegisterClassExW falhou (erro {err}).");
                return false;
            }
        }

        uint ex = Native.WS_EX_TOPMOST | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        Handle = Native.CreateWindowExW(ex, ClassName, "G2Overlay", Native.WS_POPUP, 0, 0, 800, 600,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            Logger.Error(Cat, $"CreateWindowExW falhou (erro {Marshal.GetLastWin32Error()}).");
            return false;
        }

        // Alpha global 255; a transparência real vem do canal alpha do Direct2D (frame DWM estendido).
        Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        int hr = Native.DwmExtendFrameIntoClientArea(Handle, ref margins);
        if (hr < 0) Logger.Warn(Cat, $"DwmExtendFrameIntoClientArea retornou 0x{hr:X8}.");

        Width = 800;
        Height = 600;
        Logger.Info(Cat, $"Janela do overlay criada (HWND=0x{Handle:X}).");
        return true;
    }

    /// <summary>Alinha o overlay à área cliente da janela do jogo. Retorna true se o tamanho mudou.</summary>
    public bool AlignTo(in Native.RECT r)
    {
        bool resized = r.Width != Width || r.Height != Height;
        if (r.Left != X || r.Top != Y || resized || !Visible)
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height,
                Native.SWP_NOACTIVATE | (Visible ? 0u : Native.SWP_SHOWWINDOW));
            if (resized) Logger.Info(Cat, $"Overlay alinhado: {r.Left},{r.Top} {r.Width}x{r.Height}");
            X = r.Left;
            Y = r.Top;
            Width = r.Width;
            Height = r.Height;
            Visible = true;
        }
        else if (_topmostThrottle.Allow())
        {
            // Reafirma o TOPMOST periodicamente (jogos em tela cheia sem borda às vezes sobem na ordem Z).
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        return resized;
    }

    public void Hide()
    {
        if (!Visible) return;
        Native.ShowWindow(Handle, Native.SW_HIDE);
        Visible = false;
    }

    /// <summary>Menu aberto → captura cliques; menu fechado → cliques passam para o jogo.</summary>
    public void SetClickThrough(bool clickThrough)
    {
        if (clickThrough == ClickThrough) return;
        long ex = (long)Native.GetWindowLongPtrW(Handle, Native.GWL_EXSTYLE);
        if (clickThrough) ex |= Native.WS_EX_TRANSPARENT;
        else ex &= ~(long)Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtrW(Handle, Native.GWL_EXSTYLE, (IntPtr)ex);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        ClickThrough = clickThrough;
        Logger.Debug(Cat, clickThrough ? "Click-through ATIVADO (menu fechado)." : "Click-through DESATIVADO (menu aberto).");
    }

    /// <summary>Processa mensagens pendentes. Retorna false quando WM_QUIT chega.</summary>
    public bool PumpMessages()
    {
        while (Native.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
        {
            if (msg.message == Native.WM_QUIT) return false;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        return !CloseRequested;
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_ERASEBKGND:
                return (IntPtr)1;
            case Native.WM_MOUSEACTIVATE:
                return (IntPtr)Native.MA_NOACTIVATE; // nunca rouba o foco do jogo
            case Native.WM_CLOSE:
                if (_instance != null) _instance.CloseRequested = true;
                return IntPtr.Zero;
            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }

        Native.UnregisterClassW(ClassName, Native.GetModuleHandleW(IntPtr.Zero));
    }
}
