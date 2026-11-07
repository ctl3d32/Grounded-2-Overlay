using System.Diagnostics;
using G2Overlay.Config;
using G2Overlay.Memory;
using G2Overlay.Utils;

namespace G2Overlay.Core;

/// <summary>
/// Monitora o processo do jogo: detecção, conexão, perda (jogo fechado/crash) e reconexão automática.
/// </summary>
public sealed class GameStateMonitor : IDisposable
{
    private const string Cat = "Monitor";
    private const int SearchIntervalMs = 2000;
    private const int WindowSearchIntervalMs = 1000;

    private readonly OverlayConfig _cfg;
    private readonly Stopwatch _searchTimer = Stopwatch.StartNew();
    private readonly Stopwatch _windowTimer = Stopwatch.StartNew();
    private readonly Stopwatch _aliveTimer = Stopwatch.StartNew();
    private bool _loggedWaiting;
    private bool _searched;

    public GameSession? Session { get; private set; }
    public IntPtr GameWindow { get; private set; }
    public int Reconnects { get; private set; }

    public GameStateMonitor(OverlayConfig cfg) => _cfg = cfg;

    public void Tick()
    {
        // ---------------- processo perdido?
        if (Session != null && _aliveTimer.ElapsedMilliseconds > 500)
        {
            _aliveTimer.Restart();
            if (!Session.Mem.IsAlive)
            {
                Logger.Warn(Cat, $"Processo do jogo (PID {Session.Mem.ProcessId}) encerrado. Aguardando reconexão…");
                Session.Dispose();
                Session = null;
                GameWindow = IntPtr.Zero;
                _loggedWaiting = false;
            }
        }

        // ---------------- procurar processo
        if (Session == null)
        {
            if (_searched && _searchTimer.ElapsedMilliseconds < SearchIntervalMs) return;
            _searched = true;
            _searchTimer.Restart();

            using var p = ProcessWatcher.FindProcess(_cfg.ProcessName);
            if (p == null)
            {
                if (!_loggedWaiting)
                {
                    Logger.Info(Cat, $"Aguardando o processo '{_cfg.ProcessName}.exe'…");
                    _loggedWaiting = true;
                }

                return;
            }

            // Dá tempo ao processo para mapear o módulo antes de abrir.
            if ((DateTime.Now - SafeStartTime(p)).TotalSeconds < 3) return;

            Session = GameSession.TryAttach(p, _cfg);
            if (Session != null)
            {
                Reconnects++;
                _loggedWaiting = false;
            }

            return;
        }

        // ---------------- janela do jogo
        if ((GameWindow == IntPtr.Zero || !Native.IsWindow(GameWindow)) && _windowTimer.ElapsedMilliseconds > WindowSearchIntervalMs)
        {
            _windowTimer.Restart();
            GameWindow = ProcessWatcher.FindGameWindow(Session.Mem.ProcessId);
            Session.GameWindow = GameWindow;
            if (GameWindow != IntPtr.Zero)
                Logger.Info(Cat, $"Janela do jogo encontrada: HWND=0x{GameWindow:X} classe='{Native.GetClassName(GameWindow)}'.");
        }

        Session.Tick();
    }

    private static DateTime SafeStartTime(Process p)
    {
        try { return p.StartTime; }
        catch { return DateTime.MinValue; }
    }

    public void Dispose()
    {
        Session?.Dispose();
        Session = null;
    }
}
