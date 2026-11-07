using G2Overlay.Rendering;

namespace G2Overlay.ESP;

/// <summary>
/// Cor da barra de vida por faixas, com interpolação suave entre elas:
///   100% Verde · 50% Amarelo · 25% Laranja · 10% (ou menos) Vermelho
/// </summary>
public static class HealthColors
{
    public static readonly D2DColor Green = new(0.10f, 0.90f, 0.20f);
    public static readonly D2DColor Yellow = new(1.00f, 0.90f, 0.10f);
    public static readonly D2DColor Orange = new(1.00f, 0.55f, 0.05f);
    public static readonly D2DColor Red = new(0.95f, 0.10f, 0.10f);

    public static D2DColor ForRatio(float ratio)
    {
        ratio = Math.Clamp(ratio, 0f, 1f);
        if (ratio <= 0.10f) return Red;
        if (ratio <= 0.25f) return D2DColor.Lerp(Red, Orange, (ratio - 0.10f) / 0.15f);
        if (ratio <= 0.50f) return D2DColor.Lerp(Orange, Yellow, (ratio - 0.25f) / 0.25f);
        return D2DColor.Lerp(Yellow, Green, (ratio - 0.50f) / 0.50f);
    }
}
