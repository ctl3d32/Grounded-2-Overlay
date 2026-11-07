using G2Overlay.Config;
using G2Overlay.Rendering;
using G2Overlay.UnrealSDK;

namespace G2Overlay.ESP;

/// <summary>
/// Desenha, para cada criatura visível: retângulo 2D (WorldToScreen dos 8 cantos da AABB),
/// barra de vida proporcional acima do retângulo e textos opcionais (nome, distância, vida, time).
/// </summary>
public sealed class EspRenderer
{
    private readonly OverlayConfig _cfg;
    private D2DColor _boxColor;
    private string _boxColorHex = "";

    private static readonly D2DColor BarBackground = new(0f, 0f, 0f, 0.65f);
    private static readonly D2DColor Outline = new(0f, 0f, 0f, 0.75f);
    private static readonly D2DColor TextColor = new(1f, 1f, 1f, 0.95f);
    private static readonly D2DColor DimText = new(0.85f, 0.85f, 0.85f, 0.9f);
    private static readonly D2DColor DeadColor = new(0.55f, 0.55f, 0.55f, 0.9f);

    public int DrawnLastFrame { get; private set; }
    public int CollectiblesDrawnLastFrame { get; private set; }

    private readonly Dictionary<string, D2DColor> _colorCache = new(StringComparer.OrdinalIgnoreCase);

    public EspRenderer(OverlayConfig cfg) => _cfg = cfg;

    public void Draw(Renderer r, Camera cam, IEnumerable<Creature> creatures)
    {
        if (_boxColorHex != _cfg.BoxColor)
        {
            _boxColorHex = _cfg.BoxColor;
            _boxColor = D2DColor.FromHex(_cfg.BoxColor);
        }

        int drawn = 0;
        float thick = _cfg.BoxThickness;
        float barH = _cfg.HealthBarHeight;

        foreach (var c in creatures)
        {
            if (!c.Visible) continue;
            if (!cam.ProjectBox(c.BoxCenter, c.BoxExtent, out float l, out float t, out float rr, out float b)) continue;

            float w = rr - l;
            float h = b - t;
            var boxColor = c.IsDead ? DeadColor : _boxColor;

            // 1) Bounding box (com contorno escuro para contraste)
            if (_cfg.EspBoxes)
            {
                r.DrawRect(l - 1, t - 1, w + 2, h + 2, Outline, thick + 1f);
                r.DrawRect(l, t, w, h, boxColor, thick);
            }

            // 2) Barra de vida acima do retângulo
            float textY = t - 4;
            if (_cfg.EspHealthBars)
            {
                float barW = Math.Max(w, 30f);
                float barX = l + (w - barW) * 0.5f;
                float barY = t - barH - 4f;
                float ratio = c.HealthRatio;
                r.FillRect(barX - 1, barY - 1, barW + 2, barH + 2, BarBackground);
                r.FillRect(barX, barY, barW * ratio, barH, HealthColors.ForRatio(ratio));
                textY = barY - 2;
            }

            // 3) Textos
            float cx = l + w * 0.5f;
            if (_cfg.EspNames || _cfg.EspHealthText)
            {
                string label = _cfg.EspNames ? c.Name : "";
                if (_cfg.EspHealthText)
                    label = label.Length > 0 ? $"{label}  {c.CurrentHealth:F0}/{c.MaxHealth:F0}" : $"{c.CurrentHealth:F0}/{c.MaxHealth:F0}";
                if (c.IsTamed) label += " [pet]";
                if (c.IsIncapacitated) label += " (KO)";
                else if (c.IsDead) label += " (morto)";
                r.DrawText(label, cx, textY - 16f, c.IsDead ? DeadColor : TextColor, TextAlign.Center);
            }

            float belowY = b + 2f;
            if (_cfg.EspDistance)
            {
                r.DrawText($"{c.DistanceMeters:F0} m", cx, belowY, DimText, TextAlign.Center);
                belowY += 14f;
            }

            if (_cfg.EspTeam && c.Team.Length > 0)
                r.DrawText(c.Team, cx, belowY, DimText, TextAlign.Center);

            drawn++;
        }

        DrawnLastFrame = drawn;
    }

    /// <summary>
    /// Coletáveis: caixa de tamanho fixo (CollectibleBoxExtent) ao redor da posição, com nome e distância.
    /// Se a caixa sair da tela/ficar atrás da câmera, desenha só um marcador no ponto projetado.
    /// </summary>
    public void DrawCollectibles(Renderer r, Camera cam, IEnumerable<Collectible> items)
    {
        int drawn = 0;
        double e = _cfg.CollectibleBoxExtent;
        var extent = new FVector(e, e, e);

        foreach (var c in items)
        {
            if (!c.Visible) continue;
            if (!_colorCache.TryGetValue(c.ColorHex, out var baseColor))
                _colorCache[c.ColorHex] = baseColor = D2DColor.FromHex(c.ColorHex, 1f);
            var color = c.Collected ? DeadColor : baseColor;

            float cx, top, bottom;
            if (cam.ProjectBox(c.Location, extent, out float l, out float t, out float rr, out float b))
            {
                // Garante tamanho mínimo legível a longa distância.
                float w = Math.Max(rr - l, 8f), h = Math.Max(b - t, 8f);
                cx = (l + rr) * 0.5f;
                float cy = (t + b) * 0.5f;
                l = cx - w * 0.5f;
                t = cy - h * 0.5f;
                r.DrawRect(l - 1, t - 1, w + 2, h + 2, Outline, _cfg.BoxThickness + 1f);
                r.DrawRect(l, t, w, h, color, _cfg.BoxThickness);
                top = t;
                bottom = t + h;
            }
            else if (cam.WorldToScreen(c.Location, out var p))
            {
                cx = p.X;
                r.FillRect(p.X - 3, p.Y - 3, 6, 6, color);
                top = p.Y - 4;
                bottom = p.Y + 4;
            }
            else
            {
                continue;
            }

            string label = c.Collected ? $"{c.Name} (coletado)" : c.BossActive ? $"{c.Name} (luta ativa)" : c.Name;
            r.DrawText(label, cx, top - 17f, color, TextAlign.Center);
            if (_cfg.EspDistance)
                r.DrawText($"{c.DistanceMeters:F0} m", cx, bottom + 2f, DimText, TextAlign.Center);
            drawn++;
        }

        CollectiblesDrawnLastFrame = drawn;
    }
}
