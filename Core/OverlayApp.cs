using G2Overlay.Aim;
using G2Overlay.Config;
using G2Overlay.ESP;
using G2Overlay.Memory;
using G2Overlay.Menu;
using G2Overlay.Rendering;
using G2Overlay.UnrealSDK;
using G2Overlay.Utils;

namespace G2Overlay.Core;

/// <summary>
/// Loop principal:
///   1. leitura de memória / estado do jogo  2. entidades  3. câmera  4. WorldToScreen
///   5. ESP  6. menu  7. logs (periódicos) — com limitação de FPS.
/// </summary>
public sealed class OverlayApp : IDisposable
{
    private const string Cat = "App";

    private readonly OverlayConfig _cfg;
    private readonly OverlayWindow _window = new();
    private readonly Renderer _renderer = new();
    private readonly InputState _input = new();
    private readonly MenuSystem _menu;
    private readonly EspRenderer _esp;
    private readonly GameStateMonitor _monitor;
    private readonly FrameLimiter _limiter = new();
    private readonly LogThrottle _perfLog = new(TimeSpan.FromSeconds(30));
    private readonly List<string> _status = new(8);
    private readonly System.Diagnostics.Process _self = System.Diagnostics.Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private DateTime _lastCpuSample = DateTime.UtcNow;
    private double _cpuPercent;
    private long _lastReads;
    private double _readsPerSec;
    private int _consecutiveErrors;
    private bool _diagnosticRequested;
    private readonly AimAssist _aim;

    public OverlayApp(OverlayConfig cfg)
    {
        _cfg = cfg;
        _menu = new MenuSystem(cfg);
        _esp = new EspRenderer(cfg);
        _aim = new AimAssist(cfg);
        _monitor = new GameStateMonitor(cfg);
    }

    public int Run()
    {
        Logger.Info(Cat, "Inicializando overlay…");
        OffsetBootstrap.Run(_cfg);

        if (!_window.Create()) return 2;
        if (!_renderer.Initialize(_window.Handle, _window.Width, _window.Height)) return 3;

        Logger.Info(Cat, "Loop principal iniciado. INSERT = menu, END = sair.");
        _lastCpu = _self.TotalProcessorTime;

        while (true)
        {
            _limiter.BeginFrame();
            if (!_window.PumpMessages()) break;

            _input.Update(_window.Handle);
            if (_input.Pressed(Native.VK_END))
            {
                Logger.Info(Cat, "END pressionado: encerrando.");
                break;
            }

            if (_input.Pressed(Native.VK_F9)) _diagnosticRequested = true;

            if (_input.Pressed(Native.VK_INSERT))
            {
                _menu.IsOpen = !_menu.IsOpen;
                Logger.Debug(Cat, _menu.IsOpen ? "Menu aberto." : "Menu fechado.");
            }

            bool keepRunning = true;
            try
            {
                keepRunning = Frame();
                _consecutiveErrors = 0;
            }
            catch (Exception ex)
            {
                _consecutiveErrors++;
                Logger.Error(Cat, $"Exceção no frame (#{_consecutiveErrors} seguida).", ex);
                if (_consecutiveErrors > 300)
                {
                    Logger.Fatal(Cat, "Muitas exceções consecutivas; encerrando para não travar o sistema.");
                    break;
                }
            }

            if (!keepRunning) break;

            SamplePerformance();
            // Sem janela do jogo visível: 10 FPS bastam (economiza CPU).
            _limiter.EndFrameAndWait(_window.Visible ? _cfg.FpsLimit : 10);
        }

        Logger.Info(Cat, "Saindo do loop principal.");
        return 0;
    }

    /// <summary>Um frame completo. Retorna false para encerrar.</summary>
    private bool Frame()
    {
        // 1) Estado do jogo / leitura de memória
        _monitor.Tick();
        var session = _monitor.Session;

        // Alinhamento com a janela do jogo
        bool haveRect = ProcessWatcher.TryGetClientScreenRect(_monitor.GameWindow, out var rect);
        IntPtr fg = Native.GetForegroundWindow();
        bool gameFocused = fg == _monitor.GameWindow || fg == _window.Handle;
        bool show = haveRect && (!_cfg.HideWhenGameNotFocused || gameFocused || _menu.IsOpen);

        if (!show)
        {
            // Sem o jogo: mostra só o menu no monitor principal (se aberto) para permitir configurar.
            if (_menu.IsOpen && !haveRect)
            {
                var r = new Native.RECT { Left = 0, Top = 0, Right = 1280, Bottom = 800 };
                if (_window.AlignTo(r)) _renderer.Resize(r.Width, r.Height);
            }
            else
            {
                _window.Hide();
                _window.SetClickThrough(true);
                return true;
            }
        }
        else if (_window.AlignTo(rect))
        {
            _renderer.Resize(rect.Width, rect.Height);
        }

        // Captura cliques somente com o menu aberto.
        _window.SetClickThrough(!_menu.IsOpen);

        _renderer.BeginFrame();
        try
        {
            // 2–4) Entidades, câmera e WorldToScreen
            if (_cfg.EnableOverlay && session != null && session.UpdateFrame(_window.Width, _window.Height))
            {
                // 5) ESP
                if (_diagnosticRequested)
                {
                    _diagnosticRequested = false;
                    session.Entities!.DumpNearby(session.Camera, _cfg.DiagnosticRadiusMeters);
                }

                _esp.Draw(_renderer, session.Camera, session.Entities!.Creatures);
                if (_cfg.EspCollectibles) _esp.DrawCollectibles(_renderer, session.Camera, session.Entities.Collectibles);

                // AimBot: só com a janela do JOGO em foco (não o overlay) e o menu fechado.
                bool gameHasFocus = Native.GetForegroundWindow() == _monitor.GameWindow;
                _aim.Update(session.Camera, session.Entities.Creatures, gameHasFocus, _menu.IsOpen);
                _aim.Draw(_renderer, session.Camera);
            }

            // 6) Menu
            BuildStatus(session);
            var action = _menu.Draw(_renderer, _input, _window.Width, _window.Height, _status);
            if (_menu.Changed) _cfg.Sanitize();
            if (!HandleAction(action, session)) return false;
        }
        finally
        {
            _renderer.EndFrame();
        }

        return true;
    }

    private bool HandleAction(MenuAction action, GameSession? session)
    {
        switch (action)
        {
            case MenuAction.SaveConfig:
                ConfigManager.Save(_cfg);
                break;
            case MenuAction.ReloadOffsets:
                Logger.Info(Cat, "Recarregando offsets (offsets.json + dump mais recente)…");
                OffsetBootstrap.Run(_cfg);
                session?.ResetCaches();
                break;
            case MenuAction.Diagnostic:
                _diagnosticRequested = true;
                break;
            case MenuAction.Exit:
                Logger.Info(Cat, "Sair pelo menu.");
                return false;
        }

        return true;
    }

    private void BuildStatus(GameSession? s)
    {
        _status.Clear();
        if (!_menu.IsOpen) return;

        if (s == null)
        {
            _status.Add($"Processo: aguardando {_cfg.ProcessName}.exe");
        }
        else
        {
            _status.Add($"Processo: PID {s.Mem.ProcessId} · {s.State}");
            _status.Add($"Estado: {s.StatusText}");
            if (s.Entities != null)
                _status.Add($"Criaturas: {s.Entities.Creatures.Count} rastreadas · {_esp.DrawnLastFrame} na tela");
            if (_cfg.AimEnabled)
                _status.Add(_aim.Target != null
                    ? $"AimBot: alvo '{_aim.Target.Name}' a {_aim.Target.DistanceMeters:F0} m"
                    : $"AimBot: pronto (segure {AimAssist.KeyLabel(_cfg.AimKey)})");
            if (s.Entities != null && _cfg.EspCollectibles)
                _status.Add($"Coletáveis: {s.Entities.Collectibles.Count} rastreados · {_esp.CollectiblesDrawnLastFrame} na tela");
            _status.Add($"Layout: Actors {Ok(s.Layout.ActorsValidated)} · C2W {Ok(s.Layout.TransformValidated)} · Bounds {Ok(s.Layout.BoundsValidated)}");
            _status.Add($"Leituras/s: {_readsPerSec:F0}");
        }

        _status.Add($"FPS: {_limiter.Fps:F0} · frame {_limiter.LastFrameMs:F2} ms · CPU {_cpuPercent:F1}% · RAM {_self.WorkingSet64 / (1024 * 1024)} MB");
        _status.Add($"Offsets: {Off.LoadedFrom}");
    }

    private static string Ok(bool v) => v ? "ok" : "…";

    private void SamplePerformance()
    {
        var now = DateTime.UtcNow;
        double dt = (now - _lastCpuSample).TotalSeconds;
        if (dt < 1.0) return;

        _self.Refresh();
        var cpu = _self.TotalProcessorTime;
        _cpuPercent = (cpu - _lastCpu).TotalSeconds / dt / Environment.ProcessorCount * 100.0;
        _lastCpu = cpu;
        _lastCpuSample = now;

        long reads = _monitor.Session?.Mem.ReadCount ?? 0;
        _readsPerSec = Math.Max(0, reads - _lastReads) / dt;
        _lastReads = reads;

        if (_perfLog.Allow())
        {
            Logger.Info("Perf", $"FPS={_limiter.Fps:F0} frame={_limiter.LastFrameMs:F2}ms CPU={_cpuPercent:F2}% " +
                                $"RAM={_self.WorkingSet64 / (1024 * 1024)}MB GC={GC.GetTotalMemory(false) / 1024}KB leituras/s={_readsPerSec:F0} " +
                                $"falhas={_monitor.Session?.Mem.ReadFailures ?? 0}");
        }
    }

    public void Dispose()
    {
        try
        {
            ConfigManager.Save(_cfg);
        }
        catch
        {
            // ignorado
        }

        _monitor.Dispose();
        _renderer.Dispose();
        _window.Dispose();
        _limiter.Dispose();
        _self.Dispose();
    }
}
