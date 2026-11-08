using System.Numerics;
using G2Overlay.Config;
using G2Overlay.ESP;
using G2Overlay.Memory;
using G2Overlay.Rendering;
using G2Overlay.UnrealSDK;
using G2Overlay.Utils;

namespace G2Overlay.Aim;

/// <summary>
/// AimBot / AutoAim para criaturas.
///
/// Não escreve na memória do jogo: move o mouse com SendInput (movimento relativo), como o próprio usuário faria.
/// Controle em malha fechada no espaço da tela: a cada frame mede o erro (ponto de mira − centro da tela) e envia
/// uma fração dele (1/Suavização × Ganho). Como o erro é medido de novo no frame seguinte, funciona com qualquer
/// sensibilidade de mouse/FOV sem calibração; o ganho só ajusta a velocidade.
///
/// Regras de segurança do recurso:
///   • só age enquanto a tecla de mira estiver pressionada, com a janela do jogo em foco e o menu fechado;
///   • alvos: apenas criaturas (SurvivalCreature) vivas — jogadores NUNCA são alvo; pets/buggies domados são ignorados (AimIgnoreTamed);
///   • só dentro do círculo de FOV e da distância máxima.
/// </summary>
public sealed class AimAssist
{
    private const string Cat = "Aim";

    private readonly OverlayConfig _cfg;
    private Creature? _target;
    private float _remX, _remY;
    private Vector2 _aimPoint;
    private bool _hasAimPoint;
    private readonly LogThrottle _log = new(TimeSpan.FromSeconds(2));

    public Creature? Target => _target;
    public bool Active { get; private set; }

    private static readonly D2DColor FovColor = new(1f, 1f, 1f, 0.35f);
    private static readonly D2DColor FovActiveColor = new(1f, 0.35f, 0.25f, 0.6f);
    private static readonly D2DColor TargetColor = new(1f, 0.2f, 0.15f, 1f);

    /// <summary>Teclas oferecidas no menu (código VK, rótulo).</summary>
    public static readonly (int Vk, string Label)[] KeyOptions =
    {
        (0x02, "Botão direito"),
        (0x05, "Mouse 4 (lateral)"),
        (0x06, "Mouse 5 (lateral)"),
        (0x12, "Alt"),
        (0x10, "Shift"),
        (0x43, "C"),
    };

    public static string KeyLabel(int vk)
    {
        foreach (var k in KeyOptions)
            if (k.Vk == vk) return k.Label;
        return $"VK 0x{vk:X2}";
    }

    public AimAssist(OverlayConfig cfg) => _cfg = cfg;

    /// <summary>Atualiza o alvo e, se a tecla estiver pressionada, move o mouse. Chamar após EntityManager.UpdateFrame.</summary>
    public void Update(Camera cam, IReadOnlyCollection<Creature> creatures, bool gameFocused, bool menuOpen)
    {
        Active = false;
        _hasAimPoint = false;

        bool keyDown = _cfg.AimEnabled && Native.IsKeyDown(_cfg.AimKey);
        if (!_cfg.AimEnabled || !cam.IsValid || menuOpen || !gameFocused || !keyDown)
        {
            if (_target != null) Logger.Debug(Cat, "Mira liberada.");
            _target = null;
            _remX = _remY = 0;
            return;
        }

        float cx = cam.ScreenWidth * 0.5f, cy = cam.ScreenHeight * 0.5f;
        float fov = _cfg.AimFovPixels;

        // Mantém o alvo atual (com histerese de 1,5× o FOV) enquanto ele continuar válido.
        if (_target != null && !TryAimPoint(_target, cam, out _aimPoint, cx, cy, fov * 1.5f))
            _target = null;

        if (_target == null)
        {
            float best = float.MaxValue;
            foreach (var c in creatures)
            {
                if (!TryAimPoint(c, cam, out var p, cx, cy, fov)) continue;
                float d = Vector2.Distance(p, new Vector2(cx, cy));
                if (d < best)
                {
                    best = d;
                    _target = c;
                    _aimPoint = p;
                }
            }

            if (_target != null)
                Logger.Debug(Cat, $"Alvo: '{_target.Name}' a {_target.DistanceMeters:F0} m ({best:F0} px do centro).");
        }

        if (_target == null) return;
        _hasAimPoint = true;
        Active = true;

        float dx = _aimPoint.X - cx;
        float dy = _aimPoint.Y - cy;
        if (Math.Abs(dx) < 0.5f && Math.Abs(dy) < 0.5f) return;

        float k = _cfg.AimGain / Math.Max(1f, _cfg.AimSmoothing);
        float mx = Math.Clamp(dx * k, -_cfg.AimMaxStep, _cfg.AimMaxStep) + _remX;
        float my = Math.Clamp(dy * k, -_cfg.AimMaxStep, _cfg.AimMaxStep) + _remY;
        int ix = (int)MathF.Truncate(mx);
        int iy = (int)MathF.Truncate(my);
        _remX = mx - ix;
        _remY = my - iy;
        if (ix == 0 && iy == 0) return;

        if (!Native.SendMouseMove(ix, iy) && _log.Allow())
            Logger.Warn(Cat, $"SendInput falhou (erro {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}). " +
                             "Se o jogo roda como administrador, rode o overlay como administrador.");
    }

    /// <summary>Ponto de mira na tela: centro da caixa da criatura, deslocado verticalmente por AimHeight.</summary>
    private bool TryAimPoint(Creature c, Camera cam, out Vector2 p, float cx, float cy, float radius)
    {
        p = default;
        if (!c.Visible || c.IsPlayer || c.IsDead || (c.IsIncapacitated && !_cfg.AimTargetIncapacitated)) return false;
        if (c.IsTamed && _cfg.AimIgnoreTamed) return false; // pets / buggies aliados
        if (c.DistanceMeters > _cfg.AimMaxDistanceMeters) return false;

        var center = new Vector2(cx, cy);

        // Ponto fraco: o mais próximo da mira entre os visíveis na tela.
        if (_cfg.AimWeakSpots && c.HasWeakSpots)
        {
            float best = float.MaxValue;
            bool any = false;
            for (int i = 0; i < c.WeakSpots.Length; i++)
            {
                if (!c.WeakSpotValid[i] || !cam.WorldToScreen(c.WeakSpotPos[i], out var ws)) continue;
                float d = Vector2.Distance(ws, center);
                if (d < best)
                {
                    best = d;
                    p = ws;
                    any = true;
                }
            }

            if (any) return best <= radius;
        }

        // Corpo: AimHeight 0 = base da caixa, 0,5 = centro, 1 = topo.
        double z = c.BoxExtent.Z * (_cfg.AimHeight * 2.0 - 1.0);
        var world = new FVector(c.BoxCenter.X, c.BoxCenter.Y, c.BoxCenter.Z + z);
        if (!cam.WorldToScreen(world, out p)) return false;
        return Vector2.Distance(p, center) <= radius;
    }

    /// <summary>Desenha o círculo de FOV e destaca o alvo atual.</summary>
    public void Draw(Renderer r, Camera cam)
    {
        if (!_cfg.AimEnabled) return;
        float cx = cam.ScreenWidth * 0.5f, cy = cam.ScreenHeight * 0.5f;

        if (_cfg.AimShowFov)
            r.DrawEllipse(cx, cy, _cfg.AimFovPixels, _cfg.AimFovPixels, Active ? FovActiveColor : FovColor, 1.2f);

        if (_target == null || !_hasAimPoint) return;
        if (cam.ProjectBox(_target.BoxCenter, _target.BoxExtent, out float l, out float t, out float rr, out float b))
            r.DrawRect(l - 3, t - 3, rr - l + 6, b - t + 6, TargetColor, 2f);
        r.DrawLine(_aimPoint.X - 6, _aimPoint.Y, _aimPoint.X + 6, _aimPoint.Y, TargetColor, 2f);
        r.DrawLine(_aimPoint.X, _aimPoint.Y - 6, _aimPoint.X, _aimPoint.Y + 6, TargetColor, 2f);
    }
}
