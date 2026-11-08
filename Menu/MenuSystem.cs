using G2Overlay.Config;
using G2Overlay.Rendering;

namespace G2Overlay.Menu;

/// <summary>Ações solicitadas pelo menu e executadas pelo loop principal.</summary>
public enum MenuAction
{
    None,
    SaveConfig,
    ReloadOffsets,
    Diagnostic,
    Exit,
}

/// <summary>
/// Menu translúcido em modo imediato desenhado com Direct2D.
/// INSERT mostra/oculta; arrastável pela barra de título.
/// </summary>
public sealed class MenuSystem
{
    private const float Width = 380f;
    private const float Pad = 12f;
    private const float RowH = 24f;
    private const float TitleH = 32f;

    private static readonly D2DColor Panel = new(0.07f, 0.08f, 0.10f, 0.82f);
    private static readonly D2DColor PanelBorder = new(0.25f, 0.85f, 0.35f, 0.55f);
    private static readonly D2DColor TitleBar = new(0.10f, 0.35f, 0.15f, 0.85f);
    private static readonly D2DColor Text = new(0.94f, 0.95f, 0.96f, 1f);
    private static readonly D2DColor Muted = new(0.62f, 0.66f, 0.70f, 1f);
    private static readonly D2DColor Accent = new(0.20f, 0.85f, 0.35f, 1f);
    private static readonly D2DColor BoxOff = new(0.22f, 0.24f, 0.27f, 1f);
    private static readonly D2DColor Hover = new(1f, 1f, 1f, 0.06f);
    private static readonly D2DColor Button = new(0.18f, 0.20f, 0.23f, 0.95f);
    private static readonly D2DColor ButtonHover = new(0.24f, 0.45f, 0.28f, 0.95f);
    private static readonly D2DColor TabActive = new(0.20f, 0.85f, 0.35f, 0.95f);
    private static readonly D2DColor TabActiveText = new(0.04f, 0.10f, 0.05f, 1f);

    private readonly OverlayConfig _cfg;

    private Renderer _r = null!;
    private InputState _in = null!;
    private float _x, _y, _cursorY;
    private bool _dragging;
    private float _dragDX, _dragDY;
    private string? _activeSlider;
    private MenuAction _action;
    private float _measuredHeight;
    private int _tab;
    private static readonly string[] TabNames = { "ESP", "Coletáveis", "AimBot", "Status" };

    public bool IsOpen { get; set; }
    public bool Changed { get; private set; }

    /// <summary>Retângulo atual do painel (para a lógica de captura de cliques).</summary>
    public D2DRect Bounds { get; private set; }

    public MenuSystem(OverlayConfig cfg)
    {
        _cfg = cfg;
        IsOpen = cfg.ShowMenuOnStart;
    }

    public MenuAction Draw(Renderer r, InputState input, float screenW, float screenH, IReadOnlyList<string> status)
    {
        _action = MenuAction.None;
        Changed = false;
        if (!IsOpen) return MenuAction.None;

        _r = r;
        _in = input;

        // ----- arraste
        if (_dragging)
        {
            if (input.MouseDown)
            {
                _cfg.MenuX = input.MouseX - _dragDX;
                _cfg.MenuY = input.MouseY - _dragDY;
            }
            else
            {
                _dragging = false;
                Changed = true;
            }
        }

        if (!input.MouseDown) _activeSlider = null;

        float height = _measuredHeight > 0 ? _measuredHeight : EstimateHeight(status.Count);
        _cfg.MenuX = Math.Clamp(_cfg.MenuX, 0, Math.Max(0, screenW - Width));
        // O painel inteiro precisa caber na tela (não só a barra de título).
        _cfg.MenuY = Math.Clamp(_cfg.MenuY, 0, Math.Max(0, screenH - height));
        _x = _cfg.MenuX;
        _y = _cfg.MenuY;
        Bounds = new D2DRect(_x, _y, _x + Width, _y + height);

        // ----- painel
        r.FillRoundedRect(_x, _y, Width, height, 8f, Panel);
        r.DrawRoundedRect(_x, _y, Width, height, 8f, PanelBorder, 1f);
        r.FillRoundedRect(_x, _y, Width, TitleH, 8f, TitleBar);
        r.FillRect(_x, _y + TitleH - 8f, Width, 8f, TitleBar);
        r.DrawText("Grounded 2 Overlay", _x + Pad, _y + 7f, Text, title: true);
        r.DrawText("INSERT menu · END sair", _x + Width - Pad - 130f, _y + 10f, Muted, shadow: false);

        var titleRect = new D2DRect(_x, _y, _x + Width, _y + TitleH);
        if (input.MouseClicked && titleRect.Contains(input.MouseX, input.MouseY))
        {
            _dragging = true;
            _dragDX = input.MouseX - _x;
            _dragDY = input.MouseY - _y;
        }

        _cursorY = _y + TitleH + 8f;

        // ----- opções obrigatórias (sempre visíveis, lado a lado)
        bool v;
        float half = (Width - Pad * 2) / 2f;
        float rowY = _cursorY;
        v = _cfg.EnableOverlay; if (CheckboxAt("Enable Overlay", ref v, _x, half + Pad)) _cfg.EnableOverlay = v;
        _cursorY = rowY;
        v = IsOpen; if (CheckboxAt("Show Menu", ref v, _x + Pad + half, half)) IsOpen = v;
        _cursorY = rowY + RowH + 4f;

        // ----- abas
        Tabs();

        switch (_tab)
        {
            case 0: // ESP
                rowY = _cursorY;
                v = _cfg.EspBoxes; if (CheckboxAt("Bounding box", ref v, _x, half + Pad)) _cfg.EspBoxes = v;
                _cursorY = rowY;
                v = _cfg.EspHealthBars; if (CheckboxAt("Barra de vida", ref v, _x + Pad + half, half)) _cfg.EspHealthBars = v;
                v = _cfg.EspWeakSpots; if (Checkbox("Pontos fracos (marcadores amarelos)", ref v)) _cfg.EspWeakSpots = v;
                rowY = _cursorY;
                v = _cfg.EspNames; if (CheckboxAt("Nome", ref v, _x, half + Pad)) _cfg.EspNames = v;
                _cursorY = rowY;
                v = _cfg.EspDistance; if (CheckboxAt("Distância", ref v, _x + Pad + half, half)) _cfg.EspDistance = v;
                rowY = _cursorY;
                v = _cfg.EspHealthText; if (CheckboxAt("Vida (números)", ref v, _x, half + Pad)) _cfg.EspHealthText = v;
                _cursorY = rowY;
                v = _cfg.EspTeam; if (CheckboxAt("Time / facção", ref v, _x + Pad + half, half)) _cfg.EspTeam = v;
                rowY = _cursorY;
                v = _cfg.ShowDeadCreatures; if (CheckboxAt("Mostrar mortos", ref v, _x, half + Pad)) _cfg.ShowDeadCreatures = v;
                _cursorY = rowY;
                v = _cfg.ShowPlayers; if (CheckboxAt("Mostrar jogadores", ref v, _x + Pad + half, half)) _cfg.ShowPlayers = v;
                v = _cfg.BoxSource == "Mesh"; if (Checkbox("Caixa pela malha (senão cápsula)", ref v)) _cfg.BoxSource = v ? "Mesh" : "Capsule";
                float dist = _cfg.MaxDistanceMeters;
                if (Slider("dist", "Distância máx. criaturas", ref dist, 10f, 500f, "{0:F0} m")) _cfg.MaxDistanceMeters = dist;
                break;

            case 1: // Coletáveis
                v = _cfg.EspCollectibles; if (Checkbox("Coletáveis e arenas de chefes", ref v)) _cfg.EspCollectibles = v;
                v = _cfg.EspArrows; if (Checkbox("Flechas no chão (Arrow)", ref v)) _cfg.EspArrows = v;
                v = _cfg.ShowCollectedCollectibles; if (Checkbox("Mostrar já coletados", ref v)) _cfg.ShowCollectedCollectibles = v;
                float cdist = _cfg.CollectibleMaxDistanceMeters;
                if (Slider("cdist", "Distância máx. coletáveis", ref cdist, 20f, 1000f, "{0:F0} m")) _cfg.CollectibleMaxDistanceMeters = cdist;
                break;

            case 2: // AimBot
                rowY = _cursorY;
                v = _cfg.AimEnabled; if (CheckboxAt("AimBot (segurar tecla)", ref v, _x, half + Pad)) _cfg.AimEnabled = v;
                _cursorY = rowY;
                v = _cfg.AimShowFov; if (CheckboxAt("Círculo de FOV", ref v, _x + Pad + half, half)) _cfg.AimShowFov = v;
                v = _cfg.AimIgnoreTamed; if (Checkbox("Ignorar pets e buggies (domados)", ref v)) _cfg.AimIgnoreTamed = v;
                v = _cfg.AimWeakSpots; if (Checkbox("Mirar no ponto fraco (se houver)", ref v)) _cfg.AimWeakSpots = v;
                if (ButtonAt($"Tecla: {G2Overlay.Aim.AimAssist.KeyLabel(_cfg.AimKey)}  (clique para trocar)", _x + Pad, Width - Pad * 2))
                {
                    var opts = G2Overlay.Aim.AimAssist.KeyOptions;
                    int idx = Array.FindIndex(opts, o => o.Vk == _cfg.AimKey);
                    _cfg.AimKey = opts[(idx + 1) % opts.Length].Vk;
                    Changed = true;
                }

                _cursorY += 32f;
                float afov = _cfg.AimFovPixels;
                if (Slider("afov", "FOV da mira", ref afov, 20f, 600f, "{0:F0} px")) _cfg.AimFovPixels = afov;
                float asmooth = _cfg.AimSmoothing;
                if (Slider("asmooth", "Suavização", ref asmooth, 1f, 20f, "{0:F0}")) _cfg.AimSmoothing = asmooth;
                float aheight = _cfg.AimHeight * 100f;
                if (Slider("aheight", "Altura do ponto de mira", ref aheight, 0f, 100f, "{0:F0}%")) _cfg.AimHeight = aheight / 100f;
                float adist = _cfg.AimMaxDistanceMeters;
                if (Slider("adist", "Distância máx. da mira", ref adist, 5f, 200f, "{0:F0} m")) _cfg.AimMaxDistanceMeters = adist;
                break;

            default: // Status
                float fps = _cfg.FpsLimit;
                if (Slider("fps", "Limite de FPS", ref fps, 30f, 144f, "{0:F0}")) _cfg.FpsLimit = (int)fps;
                foreach (var line in status)
                {
                    string shown = line.Length > 56 ? line[..55] + "…" : line;
                    r.DrawText(shown, _x + Pad, _cursorY, Muted, shadow: false);
                    _cursorY += 17f;
                }

                _cursorY += 6f;
                if (ButtonAt("Diagnóstico de objetos próximos (F9)", _x + Pad, Width - Pad * 2)) _action = MenuAction.Diagnostic;
                _cursorY += 32f;
                break;
        }

        // ----- rodapé (sempre visível)
        _cursorY += 4f;
        _r.FillRect(_x + Pad, _cursorY, Width - Pad * 2, 1f, new D2DColor(1, 1, 1, 0.08f));
        _cursorY += 8f;
        float bw = (Width - Pad * 2 - 16f) / 3f;
        if (ButtonAt("Salvar", _x + Pad, bw)) _action = MenuAction.SaveConfig;
        if (ButtonAt("Recarregar", _x + Pad + bw + 8f, bw)) _action = MenuAction.ReloadOffsets;
        if (ButtonAt("Sair", _x + Pad + (bw + 8f) * 2, bw)) _action = MenuAction.Exit;
        _measuredHeight = _cursorY + 26f + Pad - _y;

        return _action;
    }

    private static float EstimateHeight(int statusLines) =>
        TitleH + 8f + RowH * 11 + 28f * 2 + 46f * 2 + statusLines * 17f + 6f + 30f + Pad;

    private void Section(string title)
    {
        _cursorY += 6f;
        _r.DrawText(title.ToUpperInvariant(), _x + Pad, _cursorY, Accent, shadow: false);
        _r.FillRect(_x + Pad, _cursorY + 20f, Width - Pad * 2, 1f, new D2DColor(1, 1, 1, 0.08f));
        _cursorY += 28f;
    }

    private void Tabs()
    {
        const float h = 26f, gap = 4f;
        float w = (Width - Pad * 2 - gap * (TabNames.Length - 1)) / TabNames.Length;
        for (int i = 0; i < TabNames.Length; i++)
        {
            float x = _x + Pad + i * (w + gap);
            var rect = new D2DRect(x, _cursorY, x + w, _cursorY + h);
            bool hover = rect.Contains(_in.MouseX, _in.MouseY) && !_dragging;
            bool active = i == _tab;
            _r.FillRoundedRect(x, _cursorY, w, h, 4f, active ? TabActive : hover ? ButtonHover : Button);
            _r.DrawText(TabNames[i], x + w * 0.5f, _cursorY + 5f, active ? TabActiveText : Text, TextAlign.Center, shadow: false);
            if (hover && _in.MouseClicked) _tab = i;
        }

        _cursorY += h + 8f;
    }

    private bool Checkbox(string label, ref bool value) => CheckboxAt(label, ref value, _x, Width);

    /// <summary>Checkbox numa coluna [x, x+w) — permite duas por linha.</summary>
    private bool CheckboxAt(string label, ref bool value, float colX, float colW)
    {
        var row = new D2DRect(colX + 4f, _cursorY, colX + colW - 4f, _cursorY + RowH);
        bool hover = row.Contains(_in.MouseX, _in.MouseY) && !_dragging;
        if (hover) _r.FillRect(row.Left, row.Top, row.Right - row.Left, RowH, Hover);

        float bx = colX + Pad, by = _cursorY + 5f, bs = 14f;
        _r.FillRoundedRect(bx, by, bs, bs, 3f, value ? Accent : BoxOff);
        if (value)
        {
            _r.DrawLine(bx + 3f, by + 7.5f, bx + 6f, by + 10.5f, new D2DColor(0.05f, 0.1f, 0.05f), 2f);
            _r.DrawLine(bx + 6f, by + 10.5f, bx + 11.5f, by + 3.5f, new D2DColor(0.05f, 0.1f, 0.05f), 2f);
        }

        _r.DrawText(label, bx + bs + 10f, _cursorY + 3f, Text, shadow: false);
        _cursorY += RowH;

        if (hover && _in.MouseClicked)
        {
            value = !value;
            Changed = true;
            return true;
        }

        return false;
    }

    private bool Slider(string id, string label, ref float value, float min, float max, string format)
    {
        _r.DrawText(label, _x + Pad, _cursorY, Text, shadow: false);
        _r.DrawText(string.Format(format, value), _x + Width - Pad - 60f, _cursorY, Muted, shadow: false);
        _cursorY += 20f;

        float trackX = _x + Pad, trackW = Width - Pad * 2, trackY = _cursorY + 8f;
        var hit = new D2DRect(trackX - 4f, _cursorY, trackX + trackW + 4f, _cursorY + 22f);
        if (_in.MouseClicked && hit.Contains(_in.MouseX, _in.MouseY) && !_dragging) _activeSlider = id;

        bool changed = false;
        if (_activeSlider == id && _in.MouseDown)
        {
            float t = Math.Clamp((_in.MouseX - trackX) / trackW, 0f, 1f);
            float nv = MathF.Round(min + (max - min) * t);
            if (Math.Abs(nv - value) > 0.001f)
            {
                value = nv;
                changed = true;
                Changed = true;
            }
        }

        float frac = Math.Clamp((value - min) / (max - min), 0f, 1f);
        _r.FillRoundedRect(trackX, trackY, trackW, 4f, 2f, BoxOff);
        _r.FillRoundedRect(trackX, trackY, trackW * frac, 4f, 2f, Accent);
        _r.FillRoundedRect(trackX + trackW * frac - 6f, trackY - 5f, 12f, 14f, 3f, Text);
        _cursorY += 26f;
        return changed;
    }

    private bool ButtonAt(string label, float x, float w)
    {
        var rect = new D2DRect(x, _cursorY, x + w, _cursorY + 26f);
        bool hover = rect.Contains(_in.MouseX, _in.MouseY) && !_dragging;
        _r.FillRoundedRect(x, _cursorY, w, 26f, 4f, hover ? ButtonHover : Button);
        _r.DrawText(label, x + w * 0.5f, _cursorY + 5f, Text, TextAlign.Center, shadow: false);
        return hover && _in.MouseClicked;
    }
}
