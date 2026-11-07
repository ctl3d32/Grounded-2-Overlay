using System.Diagnostics;
using G2Overlay.Memory;

namespace G2Overlay.Utils;

/// <summary>
/// Limitador de FPS com timer de alta resolução (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Win10 1803+).
/// Dorme de verdade (sem spin), o que mantém o uso de CPU baixo.
/// </summary>
public sealed class FrameLimiter : IDisposable
{
    private readonly IntPtr _timer;
    private readonly bool _highRes;
    private long _next;

    public double LastFrameMs { get; private set; }
    public double Fps { get; private set; }
    private long _fpsWindowStart;
    private int _fpsFrames;
    private long _frameStart;

    public FrameLimiter()
    {
        _timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        _highRes = _timer != IntPtr.Zero;
        if (!_highRes)
        {
            _timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null, 0, Native.TIMER_ALL_ACCESS);
            Native.timeBeginPeriod(1);
        }

        Logger.Info("Timing", _highRes ? "Timer de alta resolução ativo." : "Timer padrão + timeBeginPeriod(1).");
        _fpsWindowStart = Stopwatch.GetTimestamp();
    }

    public void BeginFrame() => _frameStart = Stopwatch.GetTimestamp();

    public void EndFrameAndWait(int fps)
    {
        long now = Stopwatch.GetTimestamp();
        LastFrameMs = (now - _frameStart) * 1000.0 / Stopwatch.Frequency;

        _fpsFrames++;
        double win = (now - _fpsWindowStart) / (double)Stopwatch.Frequency;
        if (win >= 1.0)
        {
            Fps = _fpsFrames / win;
            _fpsFrames = 0;
            _fpsWindowStart = now;
        }

        if (fps <= 0) return;
        long period = Stopwatch.Frequency / fps;
        if (_next == 0 || now - _next > period * 4) _next = now; // atraso grande: ressincroniza
        _next += period;

        long remaining = _next - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;

        long hundredNs = remaining * 10_000_000 / Stopwatch.Frequency;
        if (_timer != IntPtr.Zero)
        {
            long due = -hundredNs; // relativo
            if (Native.SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                Native.WaitForSingleObject(_timer, Native.INFINITE);
                return;
            }
        }

        Thread.Sleep((int)Math.Max(0, hundredNs / 10_000));
    }

    public void Dispose()
    {
        if (_timer != IntPtr.Zero) Native.CloseHandle(_timer);
        if (!_highRes) Native.timeEndPeriod(1);
    }
}
