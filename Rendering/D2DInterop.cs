using System.Runtime.InteropServices;

namespace G2Overlay.Rendering;

// Interop mínimo e direto com Direct2D / DirectWrite via vtable (sem dependências NuGet).
// Índices de vtable seguem a ordem de declaração em d2d1.h / dwrite.h:
//   IUnknown: 0 QueryInterface, 1 AddRef, 2 Release
//   ID2D1Factory: 14 CreateHwndRenderTarget
//   ID2D1RenderTarget: 8 CreateSolidColorBrush, 15 DrawLine, 16 DrawRectangle, 17 FillRectangle,
//                      18 DrawRoundedRectangle, 19 FillRoundedRectangle, 20 DrawEllipse, 27 DrawText, 32 SetAntialiasMode,
//                      34 SetTextAntialiasMode, 47 Clear, 48 BeginDraw, 49 EndDraw
//   ID2D1HwndRenderTarget: 58 Resize
//   ID2D1SolidColorBrush: 8 SetColor
//   IDWriteFactory: 15 CreateTextFormat
//   IDWriteTextFormat: 3 SetTextAlignment, 4 SetParagraphAlignment, 5 SetWordWrapping

[StructLayout(LayoutKind.Sequential)]
public struct D2DColor
{
    public float R, G, B, A;

    public D2DColor(float r, float g, float b, float a = 1f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public D2DColor WithAlpha(float a) => new(R, G, B, a);

    public static D2DColor FromHex(string hex, float alpha = 1f)
    {
        string h = hex.TrimStart('#');
        try
        {
            if (h.Length == 6 || h.Length == 8)
            {
                uint v = Convert.ToUInt32(h, 16);
                if (h.Length == 6) return new D2DColor(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, alpha);
                return new D2DColor(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, ((v >> 24) & 0xFF) / 255f);
            }
        }
        catch
        {
            // cai no padrão
        }

        return new D2DColor(0, 1, 0, alpha);
    }

    public static D2DColor Lerp(D2DColor a, D2DColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new D2DColor(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
    }

    public readonly uint Key =>
        ((uint)(Math.Clamp(R, 0, 1) * 255) << 24) | ((uint)(Math.Clamp(G, 0, 1) * 255) << 16) |
        ((uint)(Math.Clamp(B, 0, 1) * 255) << 8) | (uint)(Math.Clamp(A, 0, 1) * 255);
}

[StructLayout(LayoutKind.Sequential)]
public struct D2DRect
{
    public float Left, Top, Right, Bottom;

    public D2DRect(float l, float t, float r, float b)
    {
        Left = l;
        Top = t;
        Right = r;
        Bottom = b;
    }

    public readonly bool Contains(float x, float y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct D2DPoint
{
    public float X, Y;

    public D2DPoint(float x, float y)
    {
        X = x;
        Y = y;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct D2DRoundedRect
{
    public D2DRect Rect;
    public float RadiusX, RadiusY;
}

[StructLayout(LayoutKind.Sequential)]
public struct D2DEllipse
{
    public D2DPoint Point;
    public float RadiusX, RadiusY;
}

[StructLayout(LayoutKind.Sequential)]
public struct D2DSizeU
{
    public uint Width, Height;
}

[StructLayout(LayoutKind.Sequential)]
public struct D2DPixelFormat
{
    public uint Format;    // DXGI_FORMAT
    public uint AlphaMode; // D2D1_ALPHA_MODE
}

[StructLayout(LayoutKind.Sequential)]
public struct D2DRenderTargetProperties
{
    public uint Type;      // D2D1_RENDER_TARGET_TYPE
    public D2DPixelFormat PixelFormat;
    public float DpiX, DpiY;
    public uint Usage;     // D2D1_RENDER_TARGET_USAGE
    public uint MinLevel;  // D2D1_FEATURE_LEVEL
}

[StructLayout(LayoutKind.Sequential)]
public struct D2DHwndRenderTargetProperties
{
    public IntPtr Hwnd;
    public D2DSizeU PixelSize;
    public uint PresentOptions; // D2D1_PRESENT_OPTIONS
}

public static unsafe class D2DNative
{
    public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const uint D2D1_ALPHA_MODE_PREMULTIPLIED = 1;
    public const uint D2D1_RENDER_TARGET_TYPE_DEFAULT = 0;
    public const uint D2D1_PRESENT_OPTIONS_IMMEDIATELY = 2;
    public const uint D2D1_FACTORY_TYPE_SINGLE_THREADED = 0;
    public const uint D2D1_ANTIALIAS_MODE_PER_PRIMITIVE = 0;
    public const uint D2D1_ANTIALIAS_MODE_ALIASED = 1;
    public const uint D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE = 2;
    public const uint D2D1_DRAW_TEXT_OPTIONS_NO_SNAP = 1;
    public const uint D2D1_DRAW_TEXT_OPTIONS_CLIP = 2;
    public const uint DWRITE_MEASURING_MODE_NATURAL = 0;

    public const uint DWRITE_FACTORY_TYPE_SHARED = 0;
    public const uint DWRITE_FONT_WEIGHT_NORMAL = 400;
    public const uint DWRITE_FONT_WEIGHT_SEMI_BOLD = 600;
    public const uint DWRITE_FONT_STYLE_NORMAL = 0;
    public const uint DWRITE_FONT_STRETCH_NORMAL = 5;
    public const uint DWRITE_TEXT_ALIGNMENT_LEADING = 0;
    public const uint DWRITE_TEXT_ALIGNMENT_TRAILING = 1;
    public const uint DWRITE_TEXT_ALIGNMENT_CENTER = 2;
    public const uint DWRITE_PARAGRAPH_ALIGNMENT_NEAR = 0;
    public const uint DWRITE_PARAGRAPH_ALIGNMENT_CENTER = 2;
    public const uint DWRITE_WORD_WRAPPING_NO_WRAP = 1;

    public const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);

    public static readonly Guid IID_ID2D1Factory = new("06152247-6f50-465a-9245-118bfd3b6007");
    public static readonly Guid IID_IDWriteFactory = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");

    [DllImport("d2d1.dll", ExactSpelling = true)]
    public static extern int D2D1CreateFactory(uint factoryType, in Guid riid, IntPtr factoryOptions, out IntPtr factory);

    [DllImport("dwrite.dll", ExactSpelling = true)]
    public static extern int DWriteCreateFactory(uint factoryType, in Guid iid, out IntPtr factory);

    private static void** Vtbl(IntPtr com) => *(void***)com;

    public static uint Release(IntPtr com)
    {
        if (com == IntPtr.Zero) return 0;
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(com)[2])(com);
    }

    // ---------------- ID2D1Factory
    public static int CreateHwndRenderTarget(IntPtr factory, D2DRenderTargetProperties* rtProps, D2DHwndRenderTargetProperties* hwndProps, out IntPtr target)
    {
        IntPtr t;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D2DRenderTargetProperties*, D2DHwndRenderTargetProperties*, IntPtr*, int>)Vtbl(factory)[14])(factory, rtProps, hwndProps, &t);
        target = t;
        return hr;
    }

    // ---------------- ID2D1RenderTarget
    public static int CreateSolidColorBrush(IntPtr rt, D2DColor* color, out IntPtr brush)
    {
        IntPtr b;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D2DColor*, IntPtr, IntPtr*, int>)Vtbl(rt)[8])(rt, color, IntPtr.Zero, &b);
        brush = b;
        return hr;
    }

    public static void DrawLine(IntPtr rt, D2DPoint p0, D2DPoint p1, IntPtr brush, float width) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, D2DPoint, D2DPoint, IntPtr, float, IntPtr, void>)Vtbl(rt)[15])(rt, p0, p1, brush, width, IntPtr.Zero);

    public static void DrawRectangle(IntPtr rt, D2DRect* rect, IntPtr brush, float width) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, D2DRect*, IntPtr, float, IntPtr, void>)Vtbl(rt)[16])(rt, rect, brush, width, IntPtr.Zero);

    public static void FillRectangle(IntPtr rt, D2DRect* rect, IntPtr brush) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, D2DRect*, IntPtr, void>)Vtbl(rt)[17])(rt, rect, brush);

    public static void DrawRoundedRectangle(IntPtr rt, D2DRoundedRect* rect, IntPtr brush, float width) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, D2DRoundedRect*, IntPtr, float, IntPtr, void>)Vtbl(rt)[18])(rt, rect, brush, width, IntPtr.Zero);

    public static void DrawEllipse(IntPtr rt, D2DEllipse* ellipse, IntPtr brush, float width) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, D2DEllipse*, IntPtr, float, IntPtr, void>)Vtbl(rt)[20])(rt, ellipse, brush, width, IntPtr.Zero);

    public static void FillRoundedRectangle(IntPtr rt, D2DRoundedRect* rect, IntPtr brush) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, D2DRoundedRect*, IntPtr, void>)Vtbl(rt)[19])(rt, rect, brush);

    public static void DrawText(IntPtr rt, char* text, uint length, IntPtr format, D2DRect* layout, IntPtr brush, uint options) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, char*, uint, IntPtr, D2DRect*, IntPtr, uint, uint, void>)Vtbl(rt)[27])(
            rt, text, length, format, layout, brush, options, DWRITE_MEASURING_MODE_NATURAL);

    public static void SetAntialiasMode(IntPtr rt, uint mode) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, void>)Vtbl(rt)[32])(rt, mode);

    public static void SetTextAntialiasMode(IntPtr rt, uint mode) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, void>)Vtbl(rt)[34])(rt, mode);

    public static void Clear(IntPtr rt, D2DColor* color) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, D2DColor*, void>)Vtbl(rt)[47])(rt, color);

    public static void BeginDraw(IntPtr rt) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)Vtbl(rt)[48])(rt);

    public static int EndDraw(IntPtr rt) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, ulong*, ulong*, int>)Vtbl(rt)[49])(rt, null, null);

    // ---------------- ID2D1HwndRenderTarget
    public static int Resize(IntPtr rt, D2DSizeU* size) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, D2DSizeU*, int>)Vtbl(rt)[58])(rt, size);

    // ---------------- ID2D1SolidColorBrush
    public static void SetColor(IntPtr brush, D2DColor* color) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, D2DColor*, void>)Vtbl(brush)[8])(brush, color);

    // ---------------- IDWriteFactory
    public static int CreateTextFormat(IntPtr factory, string family, uint weight, float size, out IntPtr format)
    {
        IntPtr f;
        int hr;
        fixed (char* fam = family)
        fixed (char* locale = "en-us")
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, uint, uint, uint, float, char*, IntPtr*, int>)Vtbl(factory)[15])(
                factory, fam, IntPtr.Zero, weight, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, size, locale, &f);
        }

        format = f;
        return hr;
    }

    // ---------------- IDWriteTextFormat
    public static int SetTextAlignment(IntPtr format, uint alignment) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Vtbl(format)[3])(format, alignment);

    public static int SetParagraphAlignment(IntPtr format, uint alignment) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Vtbl(format)[4])(format, alignment);

    public static int SetWordWrapping(IntPtr format, uint wrapping) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Vtbl(format)[5])(format, wrapping);
}
