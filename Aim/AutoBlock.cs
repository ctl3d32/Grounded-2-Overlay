using System.Diagnostics;
using G2Overlay.Config;
using G2Overlay.Core;
using G2Overlay.ESP;
using G2Overlay.Memory;
using G2Overlay.Rendering;
using G2Overlay.UnrealSDK;
using G2Overlay.Utils;

namespace G2Overlay.Aim;

/// <summary>
/// Bloqueio automático (bloqueio perfeito).
///
/// Como um ataque é detectado:
///   criatura → Mesh → AnimScriptInstance → MontageInstances[] (montagens tocando agora: posição e velocidade)
///   montagem → Notifies[] → instantes dos golpes (AnimNotify_HitFrame / AnimNotifyState_HitActive / _MultiHitFrame)
///   tempo até o golpe = (instante do golpe − posição) / PlayRate.
/// O alvo é o jogador quando SurvivalCharacter.MotionWarpingAttackTarget == pawn local; sem alvo de warp,
/// vale a criatura que está virada para o jogador dentro da distância máxima.
///
/// A tecla de bloqueio é pressionada pouco antes do golpe (metade da PerfectBlockWindow do jogador, lida do
/// BlockComponent) e solta logo depois dele. Nada é escrito na memória do jogo: a tecla é enviada por SendInput.
/// </summary>
public sealed class AutoBlock : IDisposable
{
    private const string Cat = "Block";
    private const int MaxMontageInstances = 16;
    private const double MaxHoldSeconds = 4.0;
    private const double WarningSeconds = 1.5;

    private readonly OverlayConfig _cfg;
    private GameSession? _session;

    private sealed class MontageInfo
    {
        public ulong NameRaw;
        public string Name = "";
        public float[] Hits = Array.Empty<float>();
        public float[] Ends = Array.Empty<float>();
        public long CheckedAt;
        public float RateScale = 1f;
    }

    private readonly Dictionary<ulong, MontageInfo> _montages = new(256);
    private readonly Dictionary<ulong, (ulong Anim, long ReadAt)> _animCache = new(64);
    private readonly HashSet<string> _loggedMontages = new();

    private bool _holding;
    private bool _lastWasCharge;
    private long _pressedAt;
    private long _releaseAt;
    private int _blocks;

    private float _perfectWindow = 0.2f;
    private long _windowReadAt;

    private int _detectCandidate;
    private int _detectHits;
    private long _lastDetectTry;
    private long _enabledSince;
    private bool _warnedNotDetected;
    private readonly LogThrottle _sendLog = new(TimeSpan.FromSeconds(5));

    /// <summary>Criatura cujo golpe contra o jogador é o próximo (para o aviso na tela).</summary>
    public Creature? Threat { get; private set; }

    public double ThreatSeconds { get; private set; }

    /// <summary>A tecla de bloqueio está sendo segurada pelo overlay.</summary>
    public bool Holding => _holding;

    public bool MontageLayoutDetected => Off.AnimInstance_MontageInstances != 0;

    public static readonly (int Vk, string Label)[] KeyOptions =
    {
        (0x02, "Botão direito"),
        (0x05, "Mouse 4 (lateral)"),
        (0x06, "Mouse 5 (lateral)"),
        (0x04, "Botão do meio"),
        (0x51, "Q"),
        (0x46, "F"),
        (0x10, "Shift"),
        (0x12, "Alt"),
        (0x11, "Ctrl"),
        (0x20, "Espaço"),
        (0x43, "C"),
        (0x56, "V"),
    };

    public static string KeyLabel(int vk)
    {
        foreach (var k in KeyOptions)
            if (k.Vk == vk) return k.Label;
        return $"VK 0x{vk:X2}";
    }

    public AutoBlock(OverlayConfig cfg) => _cfg = cfg;

    public void Update(GameSession session, bool gameFocused, bool menuOpen)
    {
        if (!ReferenceEquals(session, _session))
        {
            Release("nova sessão");
            ReleaseDodge();
            _scanner.Reset();
            _unblockableCache.Clear();
            _session = session;
            _montages.Clear();
            _positions.Clear();
            _sequences.Clear();
            _animCache.Clear();
        }

        Threat = null;
        long now = Stopwatch.GetTimestamp();
        var world = session.World;
        var entities = session.Entities;

        if (!_cfg.AutoBlockEnabled || !gameFocused || menuOpen || world == null || entities == null || world.LocalPawn == 0)
        {
            if (_holding) Release(!_cfg.AutoBlockEnabled ? "desativado" : "sem foco / menu aberto");
            ReleaseDodge();
            if (!_cfg.AutoBlockEnabled) _enabledSince = 0;
            return;
        }

        if (_enabledSince == 0) _enabledSince = now;
        var mem = session.Mem;
        ulong pawn = world.LocalPawn;
        if (!TryReadLocation(mem, pawn, out var me))
        {
            ReleaseIfDue(now);
            return;
        }

        UpdatePerfectWindow(mem, pawn, now);
        _attackers.Clear();
        if (_cfg.AutoDodgeEnabled) _scanner.Step(session, 3000);
        if (_dodgeKeyDown && now >= _dodgeUpAt) ReleaseDodge();
        if (!MontageLayoutDetected) TryDetectFrom(session, GetAnimInstance(mem, pawn, ReadMesh(mem, pawn), now), now);

        double best = double.MaxValue;
        Creature? bestC = null;
        bool bestCharge = false;
        bool bestDodge = false;
        double bestEnd = 0;
        string bestMontage = "";
        foreach (var c in entities.Creatures)
        {
            if (c.IsPlayer || c.IsDead || c.IsIncapacitated || c.IsTamed) continue;
            if (!c.Location.IsFinite || c.Location.IsZero) continue;

            double reachCm = Math.Max(c.CapsuleRadius, Math.Max(c.BoxExtent.X, c.BoxExtent.Y));
            double distM = Math.Max(0, (c.Location - me).Length - reachCm) / 100.0;
            // Até DiagnosticRangeMeters as montagens também são lidas (só para o log de diagnóstico).
            if (distM > Math.Max(_cfg.AutoBlockMaxDistanceMeters, DiagnosticRangeMeters)) continue;

            ulong anim = GetAnimInstance(mem, c.Actor, c.Mesh, now);
            DiagnoseClass(session, c, anim, distM);
            if (anim == 0) continue;
            if (!MontageLayoutDetected)
            {
                TryDetectFrom(session, anim, now);
                if (!MontageLayoutDetected) continue;
            }

            double closing = ClosingSpeed(c, me, now); // m/s em direção ao jogador (investidas, mergulhos)
            double t = NextHitSeconds(session, anim, out bool attacking, out string montageName, out double tEnd);
            if (!attacking) continue;
            _rejectMontage = montageName;
            if (_attackers.Count < 6) _attackers.Add($"{c.Name}/{montageName} golpe={(t < 0 ? "passou" : $"{t * 1000:F0}ms")} dist={distM:F1}m");

            // Investida (abelha, vespa, joaninha…): a criatura recua e dispara; a montagem fica em loop até ela chegar
            // (o instante do golpe na montagem pode até já ter passado). O golpe acontece no contato: usa o tempo até ele.
            // Se o golpe da montagem já passou, só uma arrancada rápida conta (evita bloquear quem só está andando).
            // Golpes com avanço (combos do grilo, saltos): a criatura se aproxima rápido, mas chega a tempo do golpe
            // marcado na montagem — aí o instante da montagem é o certo. Só vale o tempo até o contato quando, no
            // instante da montagem, ela ainda estaria longe (montagem em loop esperando a chegada) ou o golpe já passou.
            bool charging = false;

            // Janela de golpe longa (HitActive de 0,3 s ou mais — investida da joaninha: 2,00–4,00 s): o dano vem no
            // CONTATO, em qualquer ponto da janela. Antes da janela, o golpe não vem antes do início nem do contato;
            // dentro dela, o tempo até o golpe é o tempo até o contato (ou já, se estiver colada no jogador).
            bool longWindow = t >= 0 && tEnd - t >= LongWindowSeconds;
            if (longWindow)
            {
                double tContact = closing > 1.0 ? Math.Max(0, distM - 0.3) / closing
                                : distM <= 0.5 ? 0
                                : WarningSeconds + 1; // parada e longe: nada iminente
                t = Math.Max(t, tContact);
                charging = true; // hold curto em volta do contato, não a janela inteira
            }
            else if (distM > 0.5 && closing > (t < 0 ? FastChargeSpeedMps : ChargeSpeedMps))
            {
                double distAtHit = t < 0 ? double.MaxValue : distM - closing * t;
                if (distAtHit > ReachToleranceMeters)
                {
                    charging = true;
                    double tContact = Math.Max(0, distM - 0.3) / closing;
                    // Com o golpe da montagem ainda à frente, vale o que vier PRIMEIRO: no salto do grilo a velocidade
                    // medida fica abaixo da real (suavização), o contato parecia mais tarde que o golpe e a tecla
                    // saía atrasada (76–83 ms). Na investida em loop da abelha, o tempo da montagem fica parado e o
                    // contato decide.
                    t = t < 0 ? tContact : Math.Min(t, tContact);
                }
            }
            if (t < 0) continue;

            // Distância prevista no instante do golpe (saltos e investidas fecham a distância no caminho).
            double distAtHitM = distM - Math.Max(0, closing) * t;
            // Ataques que viajam até o jogador por baixo da terra (escorpião: Burrow) valem de qualquer distância lida.
            bool homing = IsHomingAttack(montageName);
            if (!homing && distM > _cfg.AutoBlockMaxDistanceMeters && distAtHitM > _cfg.AutoBlockMaxDistanceMeters)
            {
                if (t <= WarningSeconds) LogRejected(c, $"fora da distância máxima ({distM:F1} m > {_cfg.AutoBlockMaxDistanceMeters:F0} m, aproximando a {closing:F1} m/s)", now);
                continue;
            }

            if (t >= best) continue;
            if (_cfg.AutoBlockIgnoreRanged && mem.TryRead(c.Actor + (ulong)Off.SurvivalCharacter_CurrentAttackIsRanged, out byte ranged) && ranged != 0)
            {
                LogRejected(c, "ataque à distância (opção 'Ignorar ataques à distância')", now);
                continue;
            }

            if (!IsTargetingMe(mem, c, pawn, me, montageName, out string why))
            {
                LogRejected(c, why, now);
                continue;
            }

            best = t;
            bestC = c;
            bestCharge = charging;
            bestDodge = _cfg.AutoDodgeEnabled && IsUnblockable(montageName);
            bestEnd = charging ? t : Math.Max(t, tEnd);
            bestMontage = charging ? "" : montageName;
        }

        if (bestC != null && best <= WarningSeconds)
        {
            Threat = bestC;
            ThreatSeconds = best;
            _lastThreatForLog = bestC;
        }

        // O bloqueio só protege pela frente: vira a câmera para o atacante antes do golpe (opcional).
        if (_cfg.AutoBlockTurnToAttacker && bestC != null && best <= TurnSeconds) TurnToward(session.Camera, bestC);
        else _lastTurnPixels = 0;

        ThreatIsDodge = bestDodge;
        if (bestDodge && bestC != null)
        {
            // Ataque imbloqueável: esquiva perfeita em vez de bloqueio (a tecla de bloqueio fica solta).
            double dodgeLead = _cfg.AutoDodgeLeadMs > 0 ? _cfg.AutoDodgeLeadMs / 1000.0 : _perfectDodgeWindow * 0.5 + 0.02;
            if (best <= dodgeLead)
            {
                if (_holding) Release("ataque imbloqueável: esquiva");
                if (!_dodgeKeyDown && Stopwatch.GetElapsedTime(_lastDodgeAt, now).TotalSeconds > DodgeCooldownSeconds)
                    Dodge(bestC, best, now);
            }

            ReleaseIfDue(now);
            WarnIfNotDetected(now);
            WatchDamage(mem, pawn, now);
            return;
        }

        double lead = _cfg.AutoBlockLeadMs > 0 ? _cfg.AutoBlockLeadMs / 1000.0 : _perfectWindow * 0.5 + 0.02;
        if (bestC != null && best <= lead)
        {
            if (!_holding)
            {
                // Se o jogador já está segurando a tecla, não interfere.
                _lastWasCharge = bestCharge;
                if (!Native.IsKeyDown(_cfg.AutoBlockKey))
                {
                    Press(bestC, best, now);
                    if (_holding)
                    {
                        _pressMontage = bestMontage;
                        _pressHitAt = now + Ticks(best);
                    }
                }
            }

            if (_holding)
            {
                // A janela perfeita conta do instante em que o bloqueio começa. Se a tecla já está segurada há tempo
                // demais (golpe anterior, outra criatura), no impacto seria só um bloqueio comum: solta agora e
                // aperta de novo no próximo frame para renovar a janela.
                double heldAtHit = Stopwatch.GetElapsedTime(_pressedAt, now).TotalSeconds + best;
                if (heldAtHit > _perfectWindow && best >= MinRepressSeconds)
                {
                    Release($"renovar janela perfeita: '{bestC.Name}' golpe em {best * 1000:F0} ms");
                }
                else
                {
                    // Janelas de golpe (HitActive) mantêm a tecla até o fim da janela.
                    long until = now + Ticks(Math.Max(best, bestEnd) + _cfg.AutoBlockHoldMs / 1000.0);
                    if (until > _releaseAt) _releaseAt = until;
                }
            }
        }

        ReleaseIfDue(now);
        WarnIfNotDetected(now);
        WatchDamage(mem, pawn, now);
    }

    // ================================================================== esquiva (ataques imbloqueáveis)

    private const double DodgeCooldownSeconds = 0.6;
    private const double DodgeTapSeconds = 0.05;
    private readonly UnblockableScanner _scanner = new();
    private readonly Dictionary<string, bool> _unblockableCache = new();
    private int _unblockableVersion = -1;
    private bool _dodgeKeyDown;
    private long _dodgeUpAt;
    private long _lastDodgeAt;
    private float _perfectDodgeWindow = 0.2f;
    private int _dodges;

    /// <summary>O próximo golpe é imbloqueável e será esquivado (aviso na tela).</summary>
    public bool ThreatIsDodge { get; private set; }

    /// <summary>Montagem de ataque imbloqueável: lista manual (AutoDodgeMontages) ou tipo de dano lido do jogo.</summary>
    private bool IsUnblockable(string montage)
    {
        if (montage.Length == 0) return false;
        if (_unblockableVersion != _scanner.Version)
        {
            _unblockableCache.Clear();
            _unblockableVersion = _scanner.Version;
        }

        if (_unblockableCache.TryGetValue(montage, out bool v)) return v;
        string source = "";
        foreach (var term in _cfg.AutoDodgeMontages)
        {
            if (term.Length > 0 && montage.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                source = $"lista AutoDodgeMontages ('{term}')";
                break;
            }
        }

        if (source.Length == 0 && _scanner.MatchesMontage(montage, out string dmg)) source = $"tipo de dano {dmg}";
        v = source.Length > 0;
        _unblockableCache[montage] = v;
        if (v) Logger.Info(Cat, $"Ataque imbloqueável: '{montage}' ({source}) → esquiva automática.");
        return v;
    }

    private void Dodge(Creature c, double t, long now)
    {
        if (!Native.SendKey(_cfg.AutoDodgeKey, true))
        {
            if (_sendLog.Allow()) Logger.Warn(Cat, "SendInput falhou ao esquivar.");
            return;
        }

        _dodgeKeyDown = true;
        _dodgeUpAt = now + Ticks(DodgeTapSeconds);
        _lastDodgeAt = now;
        _dodges++;
        Logger.Debug(Cat, $"Esquiva: '{c.Name}' golpe em {t * 1000:F0} ms (janela perfeita {_perfectDodgeWindow * 1000:F0} ms, tecla {AutoBlock.KeyLabel(_cfg.AutoDodgeKey)}).");
    }

    private void ReleaseDodge()
    {
        if (!_dodgeKeyDown) return;
        _dodgeKeyDown = false;
        Native.SendKey(_cfg.AutoDodgeKey, false);
    }

    // ================================================================== diagnóstico de dano

    private readonly List<string> _attackers = new(8);
    private Creature? _lastThreatForLog;
    private float _lastDamage = float.NaN;
    private long _lastReleaseAt;

    /// <summary>
    /// Quando a vida do jogador cai, registra o que estava acontecendo: tecla segurada ou não, há quanto tempo foi
    /// apertada/solta e quais criaturas estavam no meio de um ataque. É o que mostra qual golpe escapou e por quê.
    /// </summary>
    private void WatchDamage(ProcessMemory mem, ulong pawn, long now)
    {
        ulong hc = mem.ReadPtr(pawn + (ulong)Off.SurvivalCharacter_HealthComponent);
        if (!ProcessMemory.IsValidObjectPointer(hc) || !mem.TryRead(hc + (ulong)Off.HealthComponent_CurrentDamage, out float dmg) || !float.IsFinite(dmg))
            return;
        if (float.IsNaN(_lastDamage) || dmg < _lastDamage - 0.01f)
        {
            _lastDamage = dmg; // primeira leitura ou cura
            return;
        }

        float delta = dmg - _lastDamage;
        _lastDamage = dmg;
        if (delta < 0.5f) return;

        string key = _holding
            ? $"segurando há {Stopwatch.GetElapsedTime(_pressedAt, now).TotalMilliseconds:F0} ms"
            : _lastReleaseAt != 0 ? $"solta há {Stopwatch.GetElapsedTime(_lastReleaseAt, now).TotalMilliseconds:F0} ms" : "nunca apertada";
        string who = _attackers.Count > 0 ? string.Join("; ", _attackers) : "nenhuma criatura em ataque detectada";
        string facing = "";
        var cam = _session?.Camera;
        var threat = Threat ?? _lastThreatForLog;
        if (cam != null && cam.IsValid && threat != null)
            facing = $" · atacante a {Math.Abs(YawError(cam, threat.Location)):F0}° da direção da câmera";
        // O próprio jogo diz o tipo de dano e se houve bloqueio (FDamageInfo.BlockType).
        string dmgType = "?";
        byte blockType = 255;
        ulong info = hc + (ulong)Off.HealthComponent_LastDamageInfo;
        ulong dmgClass = mem.ReadPtr(info + (ulong)Off.DamageInfo_DamageType);
        if (ProcessMemory.IsValidObjectPointer(dmgClass) && _session != null) dmgType = _session.Names.GetObjectName(dmgClass);
        mem.TryRead(info + (ulong)Off.DamageInfo_BlockType, out blockType);
        string blockText = blockType switch
        {
            0 => "sem bloqueio",
            1 => "bloqueio comum",
            2 => "bloqueio perfeito",
            3 => "guarda quebrada",
            _ => "?",
        };
        string unblockable = _scanner.IsUnblockableType(dmgType) ? " (imbloqueável)" : "";
        Logger.Info(Cat, $"Dano recebido: {delta:F1} · {dmgType}{unblockable} · resultado: {blockText} · tecla {key}{facing} · {who}");
        LearnFromDamage(delta, now, dmgType, blockType);
    }

    // ================================================================== ajuste aprendido por montagem

    private string _pressMontage = "";
    private long _pressHitAt;

    private double LearnedOffsetSeconds(string montage) =>
        montage.Length > 0 && _cfg.AutoBlockMontageOffsetsMs.TryGetValue(montage, out int ms) ? ms / 1000.0 : 0;

    /// <summary>
    /// Alguns golpes causam dano depois do notify de golpe (ex.: rabo do escorpião, ~220 ms depois). Quando o dano
    /// chega com a tecla apertada para aquela montagem, a diferença entre o golpe previsto e o dano real vira um
    /// ajuste daquela montagem (salvo em AutoBlockMontageOffsetsMs), e os próximos bloqueios saem no tempo certo.
    /// </summary>
    private void LearnFromDamage(float delta, long now, string dmgType, byte blockType)
    {
        if (delta < 5f || _pressMontage.Length == 0 || _pressHitAt == 0) return; // ignora dano contínuo (veneno etc.)
        double delay = (now - _pressHitAt) / (double)Stopwatch.Frequency;     // > 0: dano depois do previsto
        string montage = _pressMontage;
        _pressMontage = "";
        if (delay < -0.4 || delay > 0.8) return;
        if (!_attackers.Any(a => a.Contains(montage, StringComparison.Ordinal))) return;

        // Dano de um tipo imbloqueável vindo do ataque para o qual a tecla foi apertada: esse ataque passa a ser
        // esquivado (entra em AutoDodgeMontages e fica salvo).
        if (_scanner.IsUnblockableType(dmgType))
        {
            if (!_cfg.AutoDodgeMontages.Any(m => montage.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                _cfg.AutoDodgeMontages.Add(montage);
                _unblockableCache.Clear();
                Logger.Info(Cat, $"Aprendido: '{montage}' causa {dmgType}, que não pode ser bloqueado → passa a ser esquivado (AutoDodgeMontages).");
                SaveConfig();
            }

            return;
        }

        // Ajuste de tempo quando o jogo registrou bloqueio comum (tecla valendo, mas fora da janela perfeita) ou quando
        // a tecla já tinha sido solta antes do dano (golpe chegou depois do previsto, ex.: onda do GroundSlash).
        bool timing = blockType == 1 || (blockType == 0 && !_holding && delay > 0);
        if (!timing || Math.Abs(delay) < 0.04) return;

        int old = _cfg.AutoBlockMontageOffsetsMs.TryGetValue(montage, out int o) ? o : 0;
        int updated = Math.Clamp((int)Math.Round(old + delay * 1000 * 0.8), -300, 600);
        if (updated == old) return;
        _cfg.AutoBlockMontageOffsetsMs[montage] = updated;
        Logger.Info(Cat, $"Ajuste aprendido para '{montage}': dano {delay * 1000:+0;-0} ms em relação ao golpe previsto → ajuste {old} → {updated} ms.");
        SaveConfig();
    }

    private void SaveConfig()
    {
        try
        {
            G2Overlay.Config.ConfigManager.Save(_cfg);
        }
        catch
        {
            // o aprendizado continua valendo nesta sessão
        }
    }

    // ================================================================== virar para o atacante

    private const double TurnSeconds = 0.8;
    private const double TurnToleranceDeg = 25;
    private double _pixelsPerDegree = 8;   // estimado em malha fechada (sensibilidade do mouse do jogo)
    private int _lastTurnPixels;
    private double _lastTurnYaw;

    /// <summary>Diferença (graus, −180…180) entre a direção da câmera e a direção até o ponto, no plano horizontal.</summary>
    private static double YawError(Camera cam, FVector target)
    {
        double want = Math.Atan2(target.Y - cam.Location.Y, target.X - cam.Location.X) * 180 / Math.PI;
        double err = want - cam.Rotation.Yaw;
        while (err > 180) err -= 360;
        while (err < -180) err += 360;
        return err;
    }

    /// <summary>
    /// Move o mouse na horizontal até o atacante ficar à frente (± 25°). A relação pixels→graus é aprendida
    /// observando quanto a câmera girou com o movimento do frame anterior.
    /// </summary>
    private void TurnToward(Camera cam, Creature c)
    {
        if (!cam.IsValid) return;
        _lastThreatForLog = c;
        double yaw = cam.Rotation.Yaw;
        if (_lastTurnPixels != 0)
        {
            double turned = yaw - _lastTurnYaw;
            while (turned > 180) turned -= 360;
            while (turned < -180) turned += 360;
            if (Math.Abs(_lastTurnPixels) >= 4 && Math.Abs(turned) > 0.05)
            {
                double ppd = Math.Abs(_lastTurnPixels / turned);
                if (ppd is > 0.5 and < 200) _pixelsPerDegree = _pixelsPerDegree * 0.6 + ppd * 0.4;
            }
        }

        double err = YawError(cam, c.Location);
        if (Math.Abs(err) <= TurnToleranceDeg * 0.5)
        {
            _lastTurnPixels = 0;
            return;
        }

        int px = (int)Math.Round(Math.Clamp(err * _pixelsPerDegree * 0.6, -_cfg.AimMaxStep * 3, _cfg.AimMaxStep * 3));
        if (px == 0) return;
        if (Native.SendMouseMove(px, 0))
        {
            _lastTurnPixels = px;
            _lastTurnYaw = yaw;
        }
    }

    // ================================================================== tecla

    private void Press(Creature c, double t, long now)
    {
        if (!Native.SendKey(_cfg.AutoBlockKey, true))
        {
            if (_sendLog.Allow())
                Logger.Warn(Cat, $"SendInput falhou (erro {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}). " +
                                 "Se o jogo roda como administrador, rode o overlay como administrador.");
            return;
        }

        _holding = true;
        _pressedAt = now;
        _releaseAt = now + Ticks(t + _cfg.AutoBlockHoldMs / 1000.0);
        _blocks++;
        if (_blocks == 1)
            Logger.Info(Cat, $"Primeiro bloqueio automático: '{c.Name}' golpe em {t * 1000:F0} ms (janela perfeita {_perfectWindow * 1000:F0} ms, tecla {KeyLabel(_cfg.AutoBlockKey)}).");
        else
            Logger.Debug(Cat, $"Bloqueio: '{c.Name}' golpe em {t * 1000:F0} ms{(_lastWasCharge ? " (investida)" : "")}.");
    }

    private void ReleaseIfDue(long now)
    {
        if (!_holding) return;
        if (now >= _releaseAt) Release(null);
        else if (Stopwatch.GetElapsedTime(_pressedAt, now).TotalSeconds > MaxHoldSeconds) Release("tempo máximo");
    }

    private void Release(string? reason)
    {
        if (!_holding) return;
        _holding = false;
        _lastReleaseAt = Stopwatch.GetTimestamp();
        Native.SendKey(_cfg.AutoBlockKey, false);
        if (reason != null) Logger.Debug(Cat, $"Bloqueio solto ({reason}).");
    }

    private static long Ticks(double seconds) => (long)(Math.Max(0, seconds) * Stopwatch.Frequency);

    // ================================================================== leitura

    private static ulong ReadMesh(ProcessMemory mem, ulong actor) => mem.ReadPtr(actor + (ulong)Off.Character_Mesh);

    private ulong GetAnimInstance(ProcessMemory mem, ulong actor, ulong mesh, long now)
    {
        if (_animCache.TryGetValue(actor, out var e) && Stopwatch.GetElapsedTime(e.ReadAt, now).TotalMilliseconds < 500)
            return e.Anim;

        ulong anim = ProcessMemory.IsValidObjectPointer(mesh) ? mem.ReadPtr(mesh + (ulong)Off.SkeletalMeshComponent_AnimScriptInstance) : 0;
        if (!ProcessMemory.IsValidObjectPointer(anim)) anim = 0;
        if (_animCache.Count > 2048) _animCache.Clear();
        _animCache[actor] = (anim, now);
        return anim;
    }

    private static bool TryReadLocation(ProcessMemory mem, ulong actor, out FVector p)
    {
        p = default;
        ulong root = mem.ReadPtr(actor + (ulong)Off.Actor_RootComponent);
        return ProcessMemory.IsValidObjectPointer(root) &&
               mem.TryRead(root + (ulong)Off.SceneComponent_ComponentToWorld + 0x20, out p) && p.IsFinite && !p.IsZero;
    }

    private void UpdatePerfectWindow(ProcessMemory mem, ulong pawn, long now)
    {
        if (_windowReadAt != 0 && Stopwatch.GetElapsedTime(_windowReadAt, now).TotalSeconds < 2) return;
        _windowReadAt = now;
        ulong dc = mem.ReadPtr(pawn + (ulong)Off.SurvivalCharacter_DodgeComponent);
        if (ProcessMemory.IsValidObjectPointer(dc) &&
            mem.TryRead(dc + (ulong)Off.DodgeComponent_PerfectDodgeWindow, out float dw) && dw > 0.03f && dw < 2f)
        {
            if (Math.Abs(dw - _perfectDodgeWindow) > 0.001f) Logger.Debug(Cat, $"Janela da esquiva perfeita: {dw * 1000:F0} ms.");
            _perfectDodgeWindow = dw;
        }

        ulong bc = mem.ReadPtr(pawn + (ulong)Off.SurvivalCharacter_BlockComponent);
        if (ProcessMemory.IsValidObjectPointer(bc) &&
            mem.TryRead(bc + (ulong)Off.BlockComponent_PerfectBlockWindow, out float w) && w > 0.03f && w < 2f)
        {
            if (Math.Abs(w - _perfectWindow) > 0.001f) Logger.Debug(Cat, $"Janela do bloqueio perfeito: {w * 1000:F0} ms.");
            _perfectWindow = w;
        }
    }

    /// <summary>Ataques desferidos de costas (coice do grilo, jato do besouro-bombardeiro, rabo…).</summary>
    /// <summary>Ataques que se deslocam até o jogador sem estar virados para ele (escorpião enterrado).</summary>
    private static bool IsHomingAttack(string montage) =>
        montage.Contains("Burrow", StringComparison.OrdinalIgnoreCase) || montage.Contains("Underground", StringComparison.OrdinalIgnoreCase);

    private static bool IsRearAttack(string montage) =>
        montage.Contains("Back", StringComparison.OrdinalIgnoreCase) || montage.Contains("Rear", StringComparison.OrdinalIgnoreCase) ||
        montage.Contains("Spray", StringComparison.OrdinalIgnoreCase) || montage.Contains("Butt", StringComparison.OrdinalIgnoreCase) ||
        montage.Contains("Tail", StringComparison.OrdinalIgnoreCase) || montage.Contains("Kick", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Alvo de warp do ataque == jogador; sem alvo: a até 3 m vale qualquer direção; mais longe, a criatura precisa
    /// estar virada para o jogador (ou de costas, em ataques traseiros como o coice do grilo).
    /// </summary>
    private bool IsTargetingMe(ProcessMemory mem, Creature c, ulong pawn, FVector me, string montage, out string why)
    {
        why = "";
        ulong target = mem.ReadPtr(c.Actor + (ulong)Off.SurvivalCharacter_MotionWarpingAttackTarget);
        if (target == pawn) return true;
        if (ProcessMemory.IsValidObjectPointer(target))
        {
            why = $"alvo do ataque é outro ator ({_session?.Names.GetObjectName(target)})";
            return false; // atacando outro (pet, outro jogador…)
        }

        if (!mem.TryRead(c.RootComponent + (ulong)Off.SceneComponent_ComponentToWorld, out FQuat q))
        {
            why = "rotação ilegível";
            return false;
        }

        // Criaturas voadoras mergulham: compara a direção em 3D (inclui a inclinação), além da horizontal.
        double fx = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        double fy = 2 * (q.X * q.Y + q.W * q.Z);
        double fz = 2 * (q.X * q.Z - q.W * q.Y);
        var to = me - c.Location;
        double lf2 = Math.Sqrt(fx * fx + fy * fy), lt2 = Math.Sqrt(to.X * to.X + to.Y * to.Y);
        double lf3 = Math.Sqrt(fx * fx + fy * fy + fz * fz), lt3 = to.Length;
        if (lt3 < 1e-6) return true; // em cima do jogador
        double distCm = Math.Max(0, to.Length - Math.Max(c.CapsuleRadius, Math.Max(c.BoxExtent.X, c.BoxExtent.Y)));
        // Bem perto (≤ 3 m) qualquer direção vale: golpes laterais, coices e giros também acertam.
        if (distCm <= 300 || IsHomingAttack(montage)) return true;
        double limitDeg = _cfg.AutoBlockFacingDegrees;
        double cosLimit = Math.Cos(limitDeg * Math.PI / 180.0);
        double cos2 = lf2 > 1e-6 && lt2 > 1e-6 ? (fx * to.X + fy * to.Y) / (lf2 * lt2) : 1;
        double cos3 = lf3 > 1e-6 ? (fx * to.X + fy * to.Y + fz * to.Z) / (lf3 * lt3) : 1;
        if (cos2 >= cosLimit || cos3 >= cosLimit) return true;
        if (IsRearAttack(montage) && (cos2 <= 0 || cos3 <= 0)) return true; // ataque traseiro: de lado/costas para você (≥ 90°)
        why = $"criatura não está virada para você ({Math.Acos(Math.Clamp(Math.Max(cos2, cos3), -1, 1)) * 180 / Math.PI:F0}° > {limitDeg:F0}°)";
        return false;
    }

    private const double DiagnosticRangeMeters = 20;

    /// <summary>Velocidade de aproximação (m/s) acima da qual a criatura é tratada como em investida.</summary>
    private const double ChargeSpeedMps = 3.0;
    private const double FastChargeSpeedMps = 6.0;

    /// <summary>Janela de golpe a partir da qual o instante do dano é decidido pelo contato.</summary>
    private const double LongWindowSeconds = 0.3;

    /// <summary>Tempo mínimo até o golpe para soltar e reapertar a tecla (renovar a janela perfeita).</summary>
    private const double MinRepressSeconds = 0.06;

    /// <summary>Distância (m, da borda da criatura) em que o golpe da montagem ainda alcança o jogador.</summary>
    private const double ReachToleranceMeters = 1.5;

    private readonly Dictionary<ulong, (FVector Pos, long At, double Closing)> _motion = new(128);

    /// <summary>Velocidade com que a criatura se aproxima do jogador (m/s, suavizada entre frames).</summary>
    private double ClosingSpeed(Creature c, FVector me, long now)
    {
        double closing = 0;
        if (_motion.TryGetValue(c.Actor, out var m))
        {
            double dt = Stopwatch.GetElapsedTime(m.At, now).TotalSeconds;
            if (dt < 0.004) return m.Closing;
            if (dt < 0.25)
            {
                double before = (m.Pos - me).Length, after = (c.Location - me).Length;
                double inst = (before - after) / 100.0 / dt;
                closing = double.IsFinite(inst) ? m.Closing * 0.5 + inst * 0.5 : 0;
            }
        }

        if (_motion.Count > 2048) _motion.Clear();
        _motion[c.Actor] = (c.Location, now, closing);
        return closing;
    }
    private readonly HashSet<ulong> _diagClasses = new();
    private readonly Dictionary<(ulong, string), long> _rejectLog = new();

    /// <summary>Uma linha por classe de criatura: AnimInstance e montagens em execução (para entender criaturas novas).</summary>
    private void DiagnoseClass(GameSession s, Creature c, ulong anim, double distM)
    {
        if (!_diagClasses.Add(c.ClassPtr)) return;
        string animName = anim != 0 ? s.Objects.GetObjectClassInfo(anim)?.Name ?? "?" : "nenhum";
        int count = -1;
        if (anim != 0 && MontageLayoutDetected && s.Mem.TryRead(anim + (ulong)Off.AnimInstance_MontageInstances, out TArrayHeader arr))
            count = arr.Num;
        Logger.Debug(Cat, $"Diag: {c.Class.Name} a {distM:F1} m · Mesh=0x{c.Mesh:X} AnimInstance={animName} · montagens tocando={(count < 0 ? "?" : count.ToString())}");
    }

    private string _rejectMontage = "";

    private void LogRejected(Creature c, string reason, long now)
    {
        var key = (c.Actor, reason.Length > 12 ? reason[..12] : reason);
        if (_rejectLog.TryGetValue(key, out long last) && Stopwatch.GetElapsedTime(last, now).TotalSeconds < 1) return;
        if (_rejectLog.Count > 1024) _rejectLog.Clear();
        _rejectLog[key] = now;
        Logger.Debug(Cat, $"Golpe de '{c.Name}'{(_rejectMontage.Length > 0 ? $" ({_rejectMontage})" : "")} ignorado: {reason}.");
    }

    /// <summary>Segundos até o próximo golpe entre as montagens em execução (-1 = nenhum).</summary>
    private double NextHitSeconds(GameSession s, ulong anim) => NextHitSeconds(s, anim, out _, out _, out _);

    /// <param name="attacking">alguma montagem de ataque (com golpes) está tocando, mesmo que o golpe já tenha passado</param>
    /// <param name="windowEnd">segundos até o fim da janela do golpe escolhido (= início, para HitFrame)</param>
    private double NextHitSeconds(GameSession s, ulong anim, out bool attacking, out string montageName, out double windowEnd)
    {
        attacking = false;
        montageName = "";
        windowEnd = -1;
        var mem = s.Mem;
        if (!mem.TryRead(anim + (ulong)Off.AnimInstance_MontageInstances, out TArrayHeader arr)) return -1;
        if (arr.Num <= 0 || !arr.IsPlausible(256)) return -1;
        int n = Math.Min(arr.Num, MaxMontageInstances);
        Span<ulong> insts = stackalloc ulong[MaxMontageInstances];
        if (!mem.ReadArray(arr.Data, insts[..n])) return -1;

        double best = -1;
        for (int i = 0; i < n; i++)
        {
            ulong inst = insts[i];
            if (!ProcessMemory.IsValidObjectPointer(inst)) continue;
            if (!mem.TryRead(inst + (ulong)Off.AnimMontageInstance_bPlaying, out byte playing) || playing == 0) continue;
            ulong montage = mem.ReadPtr(inst + (ulong)Off.AnimMontageInstance_Montage);
            if (!ProcessMemory.IsValidObjectPointer(montage)) continue;
            if (!mem.TryRead(inst + (ulong)Off.AnimMontageInstance_Position, out float pos) ||
                !mem.TryRead(inst + (ulong)Off.AnimMontageInstance_PlayRate, out float rate)) continue;
            if (!float.IsFinite(pos) || !float.IsFinite(rate) || rate <= 0.01f) continue;

            var mi = GetMontage(s, montage);
            if (mi == null || mi.Hits.Length == 0) continue;
            attacking = true;
            pos = ExtrapolatePosition(inst, montage, pos, rate * mi.RateScale);
            if (montageName.Length == 0) montageName = mi.Name;
            double r = rate * mi.RateScale;
            float learned = (float)(LearnedOffsetSeconds(mi.Name) * r); // ajuste aprendido, no tempo da montagem
            for (int j = 0; j < mi.Hits.Length; j++)
            {
                float rawEnd = j < mi.Ends.Length ? mi.Ends[j] : mi.Hits[j];
                // O ajuste aprendido só vale para golpes pontuais; janelas longas usam o contato.
                float shift = rawEnd - mi.Hits[j] >= LongWindowSeconds * r ? 0f : learned;
                float hs = mi.Hits[j] + shift, he = rawEnd + shift;
                if (he < pos - 0.02f) continue; // janela já terminou
                double t = Math.Max(0, (hs - pos) / r);   // dentro de uma janela ativa: golpe pode vir agora (0)
                if (best < 0 || t < best)
                {
                    best = t;
                    windowEnd = Math.Max(t, (he - pos) / r);
                    montageName = mi.Name;
                }
                break; // em ordem: a primeira janela não terminada é a próxima
            }
        }

        return best;
    }

    private readonly Dictionary<(ulong Inst, ulong Montage), (float Pos, long ChangedAt)> _positions = new(64);

    /// <summary>
    /// Criaturas longe da câmera ou fora da tela têm a animação atualizada só a cada 2–4 frames (otimização de
    /// taxa de atualização da engine): a posição lida fica parada e depois salta, e o golpe parecia mais distante
    /// do que estava — a tecla saía atrasada (golpes em 50–85 ms). Estima a posição real a partir da última mudança.
    /// </summary>
    private float ExtrapolatePosition(ulong inst, ulong montage, float pos, float rate)
    {
        long now = Stopwatch.GetTimestamp();
        var key = (inst, montage);
        if (!_positions.TryGetValue(key, out var e) || Math.Abs(e.Pos - pos) > 0.0001f)
        {
            if (_positions.Count > 512) _positions.Clear();
            _positions[key] = (pos, now);
            return pos;
        }

        // Mesma posição desde a última leitura: avança pelo tempo decorrido (no máximo 70 ms ≈ 4 frames, para não adiantar demais em pausas).
        double elapsed = Math.Min(0.07, Stopwatch.GetElapsedTime(e.ChangedAt, now).TotalSeconds);
        return pos + (float)(elapsed * rate);
    }

    private MontageInfo? GetMontage(GameSession s, ulong montage)
    {
        var mem = s.Mem;
        long now = Stopwatch.GetTimestamp();
        if (_montages.TryGetValue(montage, out var mi))
        {
            if (Stopwatch.GetElapsedTime(mi.CheckedAt, now).TotalSeconds < 5) return mi;
            if (mem.TryRead(montage + (ulong)Off.UObject_NamePrivate, out ulong nr) && nr == mi.NameRaw)
            {
                mi.CheckedAt = now;
                return mi;
            }

            _montages.Remove(montage); // endereço reaproveitado por outra montagem
        }

        if (!mem.TryRead(montage + (ulong)Off.UObject_NamePrivate, out ulong nameRaw)) return null;
        var ci = s.Objects.GetObjectClassInfo(montage);
        if (ci == null) return null; // tenta de novo depois
        mi = new MontageInfo { NameRaw = nameRaw, CheckedAt = now, Name = s.Names.GetObjectName(montage) };
        if (Array.IndexOf(ci.Chain, "AnimMontage") >= 0) mi.Hits = ReadHitTimes(s, montage, mi.Name, out mi.RateScale, out mi.Ends);
        if (_montages.Count > 4096) _montages.Clear();
        _montages[montage] = mi;
        return mi;
    }

    private sealed class SequenceInfo
    {
        public ulong NameRaw;
        public float[] Hits = Array.Empty<float>();
        public float[] Ends = Array.Empty<float>();
        public float RateScale = 1f;
    }

    private readonly Dictionary<ulong, SequenceInfo> _sequences = new(512);

    /// <summary>
    /// Instantes dos golpes na linha do tempo da montagem. No Grounded 2 os AnimNotify_HitFrame ficam nas
    /// AnimSequences (AS_*) usadas pelos segmentos da montagem (AM_*), não na montagem em si; por isso:
    ///   notifies da própria montagem + para cada SlotAnimTracks[].AnimTrack.AnimSegments[]:
    ///   tempo_montagem = StartPos + (tempo_na_sequência − AnimStartTime) / (AnimPlayRate × RateScale da sequência).
    /// </summary>
    private float[] ReadHitTimes(GameSession s, ulong montage, string name, out float rateScale, out float[] ends)
    {
        var mem = s.Mem;
        var hits = new List<(float S, float E)>(4);
        var others = new HashSet<string>();
        ReadNotifyHits(s, montage, hits, others);
        rateScale = mem.Read<float>(montage + (ulong)Off.AnimSequenceBase_RateScale);
        if (!(rateScale > 0.01f && rateScale < 20f)) rateScale = 1f;

        int segCount = 0;
        if (mem.TryRead(montage + (ulong)Off.AnimMontage_SlotAnimTracks, out TArrayHeader slots) && slots.Num > 0 && slots.IsPlausible(32))
        {
            int slotSize = Off.SlotAnimationTrack_Size;
            var slotBuf = new byte[slots.Num * slotSize];
            if (mem.ReadBytes(slots.Data, slotBuf))
            {
                for (int i = 0; i < slots.Num; i++)
                {
                    var arr = MemoryMarshalRead<TArrayHeader>(slotBuf, i * slotSize + Off.SlotAnimationTrack_AnimTrack + Off.AnimTrack_AnimSegments);
                    if (arr.Num <= 0 || !arr.IsPlausible(256)) continue;
                    int segSize = Off.AnimSegment_Size;
                    var segBuf = new byte[arr.Num * segSize];
                    if (!mem.ReadBytes(arr.Data, segBuf)) continue;

                    for (int j = 0; j < arr.Num; j++)
                    {
                        int b = j * segSize;
                        ulong seq = BitConverter.ToUInt64(segBuf, b + Off.AnimSegment_AnimReference);
                        float startPos = BitConverter.ToSingle(segBuf, b + Off.AnimSegment_StartPos);
                        float animStart = BitConverter.ToSingle(segBuf, b + Off.AnimSegment_AnimStartTime);
                        float animEnd = BitConverter.ToSingle(segBuf, b + Off.AnimSegment_AnimEndTime);
                        float playRate = BitConverter.ToSingle(segBuf, b + Off.AnimSegment_AnimPlayRate);
                        int loops = BitConverter.ToInt32(segBuf, b + Off.AnimSegment_LoopingCount);
                        if (!ProcessMemory.IsValidObjectPointer(seq) || !float.IsFinite(startPos) || !float.IsFinite(animStart) ||
                            !float.IsFinite(animEnd) || animEnd < animStart) continue;
                        segCount++;

                        var si = GetSequence(s, seq, others);
                        if (si == null || si.Hits.Length == 0) continue;
                        float rate = Math.Abs(playRate * si.RateScale);
                        if (!(rate > 0.01f && rate < 20f)) rate = 1f;
                        float segLen = (animEnd - animStart) / rate;
                        loops = Math.Clamp(loops, 1, 8);
                        for (int k = 0; k < loops; k++)
                            for (int h = 0; h < si.Hits.Length; h++)
                            {
                                float ts = si.Hits[h], te = Math.Min(si.Ends[h], animEnd);
                                if (ts >= animStart - 0.001f && ts <= animEnd + 0.001f)
                                    hits.Add((startPos + k * segLen + (ts - animStart) / rate,
                                              startPos + k * segLen + (Math.Max(te, ts) - animStart) / rate));
                            }
                    }
                }
            }
        }

        hits.Sort();
        if (_loggedMontages.Add(name))
        {
            static string W((float S, float E) h) => h.E - h.S > 0.01f ? $"{h.S:F2}–{h.E:F2}s" : $"{h.S:F2}s";
            if (hits.Count > 0)
                Logger.Debug(Cat, $"Montagem '{name}': golpes em {string.Join(", ", hits.Select(W))} ({segCount} segmentos; outros notifies: {string.Join(", ", others.Take(8))}).");
            else
                Logger.Debug(Cat, $"Montagem '{name}': nenhum golpe ({segCount} segmentos; notifies: {string.Join(", ", others.Take(8))}).");
        }

        ends = hits.Select(h => h.E).ToArray();
        return hits.Select(h => h.S).ToArray();
    }

    private SequenceInfo? GetSequence(GameSession s, ulong seq, HashSet<string> others)
    {
        var mem = s.Mem;
        if (!mem.TryRead(seq + (ulong)Off.UObject_NamePrivate, out ulong nameRaw)) return null;
        if (_sequences.TryGetValue(seq, out var si) && si.NameRaw == nameRaw) return si;

        var ci = s.Objects.GetObjectClassInfo(seq);
        if (ci == null || Array.IndexOf(ci.Chain, "AnimSequenceBase") < 0) return null;
        var hits = new List<(float S, float E)>(2);
        ReadNotifyHits(s, seq, hits, others);
        hits.Sort();
        float rs = mem.Read<float>(seq + (ulong)Off.AnimSequenceBase_RateScale);
        si = new SequenceInfo
        {
            NameRaw = nameRaw,
            Hits = hits.Select(h => h.S).ToArray(),
            Ends = hits.Select(h => h.E).ToArray(),
            RateScale = rs > 0.01f && rs < 20f ? rs : 1f,
        };
        if (_sequences.Count > 8192) _sequences.Clear();
        _sequences[seq] = si;
        return si;
    }

    /// <summary>Notifies de golpe de uma animação (montagem ou sequência), no tempo da própria animação.</summary>
    /// <summary>Golpes como janelas (início, fim): HitFrame = instante; HitActive/MultiHitFrame = início + Duration.</summary>
    private static void ReadNotifyHits(GameSession s, ulong asset, List<(float S, float E)> hits, HashSet<string> others)
    {
        var mem = s.Mem;
        if (!mem.TryRead(asset + (ulong)Off.AnimSequenceBase_Notifies, out TArrayHeader arr)) return;
        if (arr.Num <= 0 || !arr.IsPlausible(1024)) return;
        float length = mem.Read<float>(asset + (ulong)Off.AnimSequenceBase_SequenceLength);
        if (!(length > 0 && length < 600)) length = 600;

        int size = Off.AnimNotifyEvent_Size;
        var buf = new byte[arr.Num * size];
        if (!mem.ReadBytes(arr.Data, buf)) return;

        for (int i = 0; i < arr.Num; i++)
        {
            var e = buf.AsSpan(i * size, size);
            ulong notify = BitConverter.ToUInt64(e[Off.AnimNotifyEvent_Notify..]);
            ulong state = BitConverter.ToUInt64(e[Off.AnimNotifyEvent_NotifyStateClass..]);

            bool isHit = false, isState = false;
            if (ProcessMemory.IsValidObjectPointer(notify))
            {
                var nci = s.Objects.GetObjectClassInfo(notify);
                if (nci != null && Array.IndexOf(nci.Chain, "AnimNotify_HitFrame") >= 0) isHit = true;
                else if (nci != null && others.Count < 32) others.Add(nci.Name);
            }

            if (!isHit && ProcessMemory.IsValidObjectPointer(state))
            {
                var sci = s.Objects.GetObjectClassInfo(state);
                if (sci != null && (Array.IndexOf(sci.Chain, "AnimNotifyState_HitActive") >= 0 ||
                                    Array.IndexOf(sci.Chain, "AnimNotifyState_MultiHitFrame") >= 0)) isHit = isState = true;
                else if (sci != null && others.Count < 32) others.Add(sci.Name);
            }

            if (!isHit) continue;

            byte method = e[Off.AnimNotifyEvent_LinkMethod];
            float segBegin = BitConverter.ToSingle(e[Off.AnimNotifyEvent_SegmentBeginTime..]);
            float segLen = BitConverter.ToSingle(e[Off.AnimNotifyEvent_SegmentLength..]);
            float value = BitConverter.ToSingle(e[Off.AnimNotifyEvent_LinkValue..]);
            float offset = BitConverter.ToSingle(e[Off.AnimNotifyEvent_TriggerTimeOffset..]);
            float time = method switch
            {
                1 => segBegin + value,           // Relative
                2 => segBegin + value * segLen,  // Proportional
                _ => value,                      // Absolute
            } + offset;
            float dur = isState ? BitConverter.ToSingle(e[Off.AnimNotifyEvent_Duration..]) : 0f;
            if (!float.IsFinite(dur) || dur < 0 || dur > 10) dur = 0;
            if (float.IsFinite(time) && time >= 0 && time <= length + 0.5f) hits.Add((time, time + dur));
        }
    }

    private static T MemoryMarshalRead<T>(byte[] buf, int offset) where T : unmanaged =>
        System.Runtime.InteropServices.MemoryMarshal.Read<T>(buf.AsSpan(offset));

    // ================================================================== detecção de AnimInstance.MontageInstances

    /// <summary>
    /// MontageInstances não é refletido. Procura em UAnimInstance um TArray cujo primeiro elemento aponta para
    /// um FAnimMontageInstance (Montage = UAnimMontage, posição e velocidade plausíveis). Duas confirmações bastam.
    /// </summary>
    private void TryDetectFrom(GameSession s, ulong anim, long now)
    {
        if (anim == 0 || MontageLayoutDetected) return;
        if (_lastDetectTry != 0 && Stopwatch.GetElapsedTime(_lastDetectTry, now).TotalMilliseconds < 50) return;
        _lastDetectTry = now;
        var mem = s.Mem;

        for (int off = 0x30; off < 0x800; off += 8)
        {
            if (!mem.TryRead(anim + (ulong)off, out TArrayHeader arr)) continue;
            if (arr.Num <= 0 || arr.Num > 8 || arr.Max < arr.Num || arr.Max > 64 || !ProcessMemory.IsValidObjectPointer(arr.Data)) continue;
            ulong e0 = mem.ReadPtr(arr.Data);
            if (!ProcessMemory.IsValidObjectPointer(e0)) continue;
            ulong montage = mem.ReadPtr(e0 + (ulong)Off.AnimMontageInstance_Montage);
            if (!ProcessMemory.IsValidObjectPointer(montage)) continue;
            var ci = s.Objects.GetObjectClassInfo(montage);
            if (ci == null || Array.IndexOf(ci.Chain, "AnimMontage") < 0) continue;
            if (!mem.TryRead(e0 + (ulong)Off.AnimMontageInstance_Position, out float pos) ||
                !mem.TryRead(e0 + (ulong)Off.AnimMontageInstance_PlayRate, out float rate)) continue;
            if (!(pos >= 0 && pos < 600) || !(rate > -20 && rate < 20)) continue;

            if (off == _detectCandidate) _detectHits++;
            else
            {
                _detectCandidate = off;
                _detectHits = 1;
            }

            if (_detectHits >= 2)
            {
                Off.Set("AnimInstance.MontageInstances", off);
                Logger.Info(Cat, $"UAnimInstance.MontageInstances detectado em 0x{off:X} (montagem '{s.Names.GetObjectName(montage)}', posição {pos:F2}s).");
            }

            return;
        }
    }

    private void WarnIfNotDetected(long now)
    {
        if (MontageLayoutDetected || _warnedNotDetected || _enabledSince == 0) return;
        if (Stopwatch.GetElapsedTime(_enabledSince, now).TotalSeconds < 60) return;
        _warnedNotDetected = true;
        Logger.Warn(Cat, "Bloqueio automático: ainda não foi possível localizar as montagens em execução (AnimInstance.MontageInstances). " +
                         "Ataque algo ou deixe uma criatura atacar para a detecção acontecer; se persistir, envie o log.");
    }

    // ================================================================== desenho

    private static readonly D2DColor WarnColor = new(1f, 0.6f, 0.1f, 1f);
    private static readonly D2DColor HoldColor = new(0.3f, 0.85f, 1f, 1f);

    public void Draw(Renderer r, Camera cam)
    {
        if (!_cfg.AutoBlockEnabled || !_cfg.AutoBlockShowWarning) return;
        float cx = cam.ScreenWidth * 0.5f, y = cam.ScreenHeight * 0.5f + 70f;

        if (!MontageLayoutDetected)
        {
            r.DrawText("Bloqueio automático: aguardando primeiro ataque para calibrar", cx, y, new D2DColor(1, 1, 1, 0.5f), TextAlign.Center);
            return;
        }

        var c = Threat;
        if (c == null) return;
        if (cam.ProjectBox(c.BoxCenter, c.BoxExtent, out float l, out float t, out float rr, out float b))
            r.DrawRect(l - 4, t - 4, rr - l + 8, b - t + 8, _holding ? HoldColor : WarnColor, 2f);
        string text = ThreatIsDodge
            ? (_dodgeKeyDown ? $"ESQUIVANDO · {c.Name}" : $"Ataque imbloqueável de {c.Name} em {ThreatSeconds:F2} s")
            : _holding ? $"BLOQUEANDO · {c.Name}" : $"Ataque de {c.Name} em {ThreatSeconds:F2} s";
        r.DrawText(text, cx, y, _holding ? HoldColor : WarnColor, TextAlign.Center, title: true);
    }

    /// <summary>Frame sem leitura do jogo: solta a tecla se estiver segurando.</summary>
    public void Suspend()
    {
        Threat = null;
        ReleaseDodge();
        Release("sem leitura do jogo");
    }

    public void Dispose()
    {
        ReleaseDodge();
        Release("encerrando");
    }
}
