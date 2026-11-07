using G2Overlay.Utils;

namespace G2Overlay.Rendering;

public enum TextAlign
{
    Left,
    Center,
}

/// <summary>
/// API de desenho de alto nível sobre um ID2D1HwndRenderTarget com alpha pré-multiplicado.
/// Um único pincel sólido é reutilizado (SetColor) para minimizar objetos COM.
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    private const string Cat = "Renderer";

    private IntPtr _d2dFactory;
    private IntPtr _dwFactory;
    private IntPtr _target;
    private IntPtr _brush;
    private IntPtr _fontLeft;
    private IntPtr _fontCenter;
    private IntPtr _fontTitle;
    private IntPtr _hwnd;
    private uint _width, _height;
    private uint _lastBrushColor = 0xFFFFFFFF;
    private bool _inFrame;

    public float FontSize { get; } = 13f;
    public bool IsReady => _target != IntPtr.Zero;

    public bool Initialize(IntPtr hwnd, int width, int height)
    {
        _hwnd = hwnd;
        int hr = D2DNative.D2D1CreateFactory(D2DNative.D2D1_FACTORY_TYPE_SINGLE_THREADED, D2DNative.IID_ID2D1Factory, IntPtr.Zero, out _d2dFactory);
        if (hr < 0)
        {
            Logger.Error(Cat, $"D2D1CreateFactory falhou (0x{hr:X8}).");
            return false;
        }

        hr = D2DNative.DWriteCreateFactory(D2DNative.DWRITE_FACTORY_TYPE_SHARED, D2DNative.IID_IDWriteFactory, out _dwFactory);
        if (hr < 0)
        {
            Logger.Error(Cat, $"DWriteCreateFactory falhou (0x{hr:X8}).");
            return false;
        }

        if (!CreateFonts()) return false;
        if (!CreateTarget((uint)Math.Max(1, width), (uint)Math.Max(1, height))) return false;
        Logger.Info(Cat, "Direct2D inicializado (HwndRenderTarget, B8G8R8A8 premultiplied, PRESENT_IMMEDIATELY).");
        return true;
    }

    private bool CreateFonts()
    {
        if (D2DNative.CreateTextFormat(_dwFactory, "Segoe UI", D2DNative.DWRITE_FONT_WEIGHT_NORMAL, FontSize, out _fontLeft) < 0 ||
            D2DNative.CreateTextFormat(_dwFactory, "Segoe UI", D2DNative.DWRITE_FONT_WEIGHT_SEMI_BOLD, 12f, out _fontCenter) < 0 ||
            D2DNative.CreateTextFormat(_dwFactory, "Segoe UI", D2DNative.DWRITE_FONT_WEIGHT_SEMI_BOLD, 15f, out _fontTitle) < 0)
        {
            Logger.Error(Cat, "CreateTextFormat falhou.");
            return false;
        }

        foreach (var f in new[] { _fontLeft, _fontCenter, _fontTitle })
        {
            D2DNative.SetWordWrapping(f, D2DNative.DWRITE_WORD_WRAPPING_NO_WRAP);
            D2DNative.SetParagraphAlignment(f, D2DNative.DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
        }

        D2DNative.SetTextAlignment(_fontCenter, D2DNative.DWRITE_TEXT_ALIGNMENT_CENTER);
        return true;
    }

    private bool CreateTarget(uint w, uint h)
    {
        ReleaseTarget();
        var rtp = new D2DRenderTargetProperties
        {
            Type = D2DNative.D2D1_RENDER_TARGET_TYPE_DEFAULT,
            PixelFormat = new D2DPixelFormat { Format = D2DNative.DXGI_FORMAT_B8G8R8A8_UNORM, AlphaMode = D2DNative.D2D1_ALPHA_MODE_PREMULTIPLIED },
            DpiX = 96f,
            DpiY = 96f,
        };
        var hp = new D2DHwndRenderTargetProperties
        {
            Hwnd = _hwnd,
            PixelSize = new D2DSizeU { Width = w, Height = h },
            PresentOptions = D2DNative.D2D1_PRESENT_OPTIONS_IMMEDIATELY,
        };

        int hr = D2DNative.CreateHwndRenderTarget(_d2dFactory, &rtp, &hp, out _target);
        if (hr < 0)
        {
            Logger.Error(Cat, $"CreateHwndRenderTarget falhou (0x{hr:X8}).");
            _target = IntPtr.Zero;
            return false;
        }

        var white = new D2DColor(1, 1, 1, 1);
        hr = D2DNative.CreateSolidColorBrush(_target, &white, out _brush);
        if (hr < 0)
        {
            Logger.Error(Cat, $"CreateSolidColorBrush falhou (0x{hr:X8}).");
            return false;
        }

        _lastBrushColor = white.Key;
        D2DNative.SetTextAntialiasMode(_target, D2DNative.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        _width = w;
        _height = h;
        return true;
    }

    public void Resize(int width, int height)
    {
        if (_target == IntPtr.Zero) return;
        var size = new D2DSizeU { Width = (uint)Math.Max(1, width), Height = (uint)Math.Max(1, height) };
        if (size.Width == _width && size.Height == _height) return;
        int hr = D2DNative.Resize(_target, &size);
        if (hr < 0)
        {
            Logger.Warn(Cat, $"Resize falhou (0x{hr:X8}); recriando render target.");
            CreateTarget(size.Width, size.Height);
            return;
        }

        _width = size.Width;
        _height = size.Height;
    }

    public void BeginFrame()
    {
        if (_target == IntPtr.Zero) return;
        D2DNative.BeginDraw(_target);
        var clear = new D2DColor(0, 0, 0, 0);
        D2DNative.Clear(_target, &clear);
        _inFrame = true;
    }

    public void EndFrame()
    {
        if (!_inFrame) return;
        _inFrame = false;
        int hr = D2DNative.EndDraw(_target);
        if (hr == D2DNative.D2DERR_RECREATE_TARGET)
        {
            Logger.Warn(Cat, "D2DERR_RECREATE_TARGET: recriando render target (dispositivo perdido).");
            CreateTarget(_width, _height);
        }
        else if (hr < 0)
        {
            Logger.Error(Cat, $"EndDraw falhou (0x{hr:X8}).");
        }
    }

    private void UseColor(in D2DColor c)
    {
        uint key = c.Key;
        if (key == _lastBrushColor) return;
        var copy = c;
        D2DNative.SetColor(_brush, &copy);
        _lastBrushColor = key;
    }

    public void DrawRect(float x, float y, float w, float h, D2DColor color, float thickness = 1f)
    {
        if (!_inFrame) return;
        UseColor(color);
        var r = new D2DRect(x, y, x + w, y + h);
        D2DNative.DrawRectangle(_target, &r, _brush, thickness);
    }

    public void FillRect(float x, float y, float w, float h, D2DColor color)
    {
        if (!_inFrame || w <= 0 || h <= 0) return;
        UseColor(color);
        var r = new D2DRect(x, y, x + w, y + h);
        D2DNative.FillRectangle(_target, &r, _brush);
    }

    public void FillRoundedRect(float x, float y, float w, float h, float radius, D2DColor color)
    {
        if (!_inFrame) return;
        UseColor(color);
        var r = new D2DRoundedRect { Rect = new D2DRect(x, y, x + w, y + h), RadiusX = radius, RadiusY = radius };
        D2DNative.FillRoundedRectangle(_target, &r, _brush);
    }

    public void DrawRoundedRect(float x, float y, float w, float h, float radius, D2DColor color, float thickness = 1f)
    {
        if (!_inFrame) return;
        UseColor(color);
        var r = new D2DRoundedRect { Rect = new D2DRect(x, y, x + w, y + h), RadiusX = radius, RadiusY = radius };
        D2DNative.DrawRoundedRectangle(_target, &r, _brush, thickness);
    }

    public void DrawEllipse(float cx, float cy, float rx, float ry, D2DColor color, float thickness = 1f)
    {
        if (!_inFrame) return;
        UseColor(color);
        var e = new D2DEllipse { Point = new D2DPoint(cx, cy), RadiusX = rx, RadiusY = ry };
        D2DNative.DrawEllipse(_target, &e, _brush, thickness);
    }

    public void DrawLine(float x0, float y0, float x1, float y1, D2DColor color, float thickness = 1f)
    {
        if (!_inFrame) return;
        UseColor(color);
        D2DNative.DrawLine(_target, new D2DPoint(x0, y0), new D2DPoint(x1, y1), _brush, thickness);
    }

    /// <summary>Texto com sombra de 1px (legível sobre qualquer fundo).</summary>
    public void DrawText(string text, float x, float y, D2DColor color, TextAlign align = TextAlign.Left, bool shadow = true, bool title = false)
    {
        if (!_inFrame || string.IsNullOrEmpty(text)) return;
        IntPtr font = title ? _fontTitle : align == TextAlign.Center ? _fontCenter : _fontLeft;

        // Para centralizar, o retângulo de layout é centrado em x.
        const float halfWidth = 400f;
        float left = align == TextAlign.Center ? x - halfWidth : x;
        float right = align == TextAlign.Center ? x + halfWidth : x + 2000f;

        fixed (char* p = text)
        {
            if (shadow)
            {
                UseColor(new D2DColor(0, 0, 0, color.A * 0.85f));
                var rs = new D2DRect(left + 1, y + 1, right + 1, y + 40);
                D2DNative.DrawText(_target, p, (uint)text.Length, font, &rs, _brush, D2DNative.D2D1_DRAW_TEXT_OPTIONS_NO_SNAP);
            }

            UseColor(color);
            var r = new D2DRect(left, y, right, y + 40);
            D2DNative.DrawText(_target, p, (uint)text.Length, font, &r, _brush, D2DNative.D2D1_DRAW_TEXT_OPTIONS_NO_SNAP);
        }
    }

    /// <summary>Estimativa de largura (Segoe UI) usada pelo layout do menu.</summary>
    public float MeasureApprox(string text, bool title = false) => text.Length * (title ? 8.2f : 6.9f);

    private void ReleaseTarget()
    {
        D2DNative.Release(_brush);
        _brush = IntPtr.Zero;
        D2DNative.Release(_target);
        _target = IntPtr.Zero;
    }

    public void Dispose()
    {
        ReleaseTarget();
        D2DNative.Release(_fontLeft);
        D2DNative.Release(_fontCenter);
        D2DNative.Release(_fontTitle);
        D2DNative.Release(_dwFactory);
        D2DNative.Release(_d2dFactory);
        _fontLeft = _fontCenter = _fontTitle = _dwFactory = _d2dFactory = IntPtr.Zero;
    }
}
