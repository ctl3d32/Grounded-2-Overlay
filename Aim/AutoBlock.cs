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
            _session = session;
            _montages.Clear();
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
        if (!MontageLayoutDetected) TryDetectFrom(session, GetAnimInstance(mem, pawn, ReadMesh(mem, pawn), now), now);

        double best = double.MaxValue;
        Creature? bestC = null;
        bool bestCharge = false;
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
            double t = NextHitSeconds(session, anim, out bool attacking);
            if (!attacking) continue;

            // Investida (abelha, vespa, joaninha…): a criatura recua e dispara; a montagem fica em loop até ela chegar
            // (o instante do golpe na montagem pode até já ter passado). O golpe acontece no contato: usa o tempo até ele.
            // Se o golpe da montagem já passou, só uma arrancada rápida conta (evita bloquear quem só está andando).
            bool charging = closing > (t < 0 ? FastChargeSpeedMps : ChargeSpeedMps) && distM > 0.5;
            if (charging) t = Math.Max(0, distM - 0.3) / closing;
            if (t < 0) continue;

            if (distM > _cfg.AutoBlockMaxDistanceMeters && !(charging && distM - closing * t <= _cfg.AutoBlockMaxDistanceMeters))
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

            if (!IsTargetingMe(mem, c, pawn, me, out string why))
            {
                LogRejected(c, why, now);
                continue;
            }

            best = t;
            bestC = c;
            bestCharge = charging;
        }

        if (bestC != null && best <= WarningSeconds)
        {
            Threat = bestC;
            ThreatSeconds = best;
        }

        double lead = _cfg.AutoBlockLeadMs > 0 ? _cfg.AutoBlockLeadMs / 1000.0 : _perfectWindow * 0.5 + 0.02;
        if (bestC != null && best <= lead)
        {
            if (!_holding)
            {
                // Se o jogador já está segurando a tecla, não interfere.
                _lastWasCharge = bestCharge;
                if (!Native.IsKeyDown(_cfg.AutoBlockKey)) Press(bestC, best, now);
            }

            if (_holding)
            {
                long until = now + Ticks(best + _cfg.AutoBlockHoldMs / 1000.0);
                if (until > _releaseAt) _releaseAt = until;
            }
        }

        ReleaseIfDue(now);
        WarnIfNotDetected(now);
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
        ulong bc = mem.ReadPtr(pawn + (ulong)Off.SurvivalCharacter_BlockComponent);
        if (ProcessMemory.IsValidObjectPointer(bc) &&
            mem.TryRead(bc + (ulong)Off.BlockComponent_PerfectBlockWindow, out float w) && w > 0.03f && w < 2f)
        {
            if (Math.Abs(w - _perfectWindow) > 0.001f) Logger.Debug(Cat, $"Janela do bloqueio perfeito: {w * 1000:F0} ms.");
            _perfectWindow = w;
        }
    }

    /// <summary>Alvo de warp do ataque == jogador; sem alvo, a criatura precisa estar virada para o jogador.</summary>
    private bool IsTargetingMe(ProcessMemory mem, Creature c, ulong pawn, FVector me, out string why)
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
        // Bem perto (≤ 2 m), golpes laterais também acertam: tolera até 100°.
        double distCm = Math.Max(0, to.Length - Math.Max(c.CapsuleRadius, Math.Max(c.BoxExtent.X, c.BoxExtent.Y)));
        double limitDeg = distCm <= 200 ? Math.Max(_cfg.AutoBlockFacingDegrees, 100) : _cfg.AutoBlockFacingDegrees;
        double cosLimit = Math.Cos(limitDeg * Math.PI / 180.0);
        double cos2 = lf2 > 1e-6 && lt2 > 1e-6 ? (fx * to.X + fy * to.Y) / (lf2 * lt2) : 1;
        double cos3 = lf3 > 1e-6 ? (fx * to.X + fy * to.Y + fz * to.Z) / (lf3 * lt3) : 1;
        if (cos2 >= cosLimit || cos3 >= cosLimit) return true;
        why = $"criatura não está virada para você ({Math.Acos(Math.Clamp(Math.Max(cos2, cos3), -1, 1)) * 180 / Math.PI:F0}° > {limitDeg:F0}°)";
        return false;
    }

    private const double DiagnosticRangeMeters = 20;

    /// <summary>Velocidade de aproximação (m/s) acima da qual a criatura é tratada como em investida.</summary>
    private const double ChargeSpeedMps = 3.0;
    private const double FastChargeSpeedMps = 6.0;

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

    private void LogRejected(Creature c, string reason, long now)
    {
        var key = (c.Actor, reason.Length > 12 ? reason[..12] : reason);
        if (_rejectLog.TryGetValue(key, out long last) && Stopwatch.GetElapsedTime(last, now).TotalSeconds < 1) return;
        if (_rejectLog.Count > 1024) _rejectLog.Clear();
        _rejectLog[key] = now;
        Logger.Debug(Cat, $"Golpe de '{c.Name}' ignorado: {reason}.");
    }

    /// <summary>Segundos até o próximo golpe entre as montagens em execução (-1 = nenhum).</summary>
    private double NextHitSeconds(GameSession s, ulong anim) => NextHitSeconds(s, anim, out _);

    /// <param name="attacking">alguma montagem de ataque (com golpes) está tocando, mesmo que o golpe já tenha passado</param>
    private double NextHitSeconds(GameSession s, ulong anim, out bool attacking)
    {
        attacking = false;
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
            foreach (float h in mi.Hits)
            {
                if (h < pos - 0.02f) continue;
                double t = Math.Max(0, (h - pos) / (rate * mi.RateScale));
                if (best < 0 || t < best) best = t;
                break; // Hits em ordem: o primeiro à frente é o próximo
            }
        }

        return best;
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
        if (Array.IndexOf(ci.Chain, "AnimMontage") >= 0) mi.Hits = ReadHitTimes(s, montage, mi.Name, out mi.RateScale);
        if (_montages.Count > 4096) _montages.Clear();
        _montages[montage] = mi;
        return mi;
    }

    private sealed class SequenceInfo
    {
        public ulong NameRaw;
        public float[] Hits = Array.Empty<float>();
        public float RateScale = 1f;
    }

    private readonly Dictionary<ulong, SequenceInfo> _sequences = new(512);

    /// <summary>
    /// Instantes dos golpes na linha do tempo da montagem. No Grounded 2 os AnimNotify_HitFrame ficam nas
    /// AnimSequences (AS_*) usadas pelos segmentos da montagem (AM_*), não na montagem em si; por isso:
    ///   notifies da própria montagem + para cada SlotAnimTracks[].AnimTrack.AnimSegments[]:
    ///   tempo_montagem = StartPos + (tempo_na_sequência − AnimStartTime) / (AnimPlayRate × RateScale da sequência).
    /// </summary>
    private float[] ReadHitTimes(GameSession s, ulong montage, string name, out float rateScale)
    {
        var mem = s.Mem;
        var hits = new List<float>(4);
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
                            foreach (float ts in si.Hits)
                                if (ts >= animStart - 0.001f && ts <= animEnd + 0.001f)
                                    hits.Add(startPos + k * segLen + (ts - animStart) / rate);
                    }
                }
            }
        }

        hits.Sort();
        if (_loggedMontages.Add(name))
        {
            if (hits.Count > 0)
                Logger.Debug(Cat, $"Montagem '{name}': golpes em {string.Join(", ", hits.Select(h => $"{h:F2}s"))} ({segCount} segmentos).");
            else
                Logger.Debug(Cat, $"Montagem '{name}': nenhum golpe ({segCount} segmentos; notifies: {string.Join(", ", others.Take(8))}).");
        }

        return hits.ToArray();
    }

    private SequenceInfo? GetSequence(GameSession s, ulong seq, HashSet<string> others)
    {
        var mem = s.Mem;
        if (!mem.TryRead(seq + (ulong)Off.UObject_NamePrivate, out ulong nameRaw)) return null;
        if (_sequences.TryGetValue(seq, out var si) && si.NameRaw == nameRaw) return si;

        var ci = s.Objects.GetObjectClassInfo(seq);
        if (ci == null || Array.IndexOf(ci.Chain, "AnimSequenceBase") < 0) return null;
        var hits = new List<float>(2);
        ReadNotifyHits(s, seq, hits, others);
        hits.Sort();
        float rs = mem.Read<float>(seq + (ulong)Off.AnimSequenceBase_RateScale);
        si = new SequenceInfo { NameRaw = nameRaw, Hits = hits.ToArray(), RateScale = rs > 0.01f && rs < 20f ? rs : 1f };
        if (_sequences.Count > 8192) _sequences.Clear();
        _sequences[seq] = si;
        return si;
    }

    /// <summary>Notifies de golpe de uma animação (montagem ou sequência), no tempo da própria animação.</summary>
    private static void ReadNotifyHits(GameSession s, ulong asset, List<float> hits, HashSet<string> others)
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

            bool isHit = false;
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
                                    Array.IndexOf(sci.Chain, "AnimNotifyState_MultiHitFrame") >= 0)) isHit = true;
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
            if (float.IsFinite(time) && time >= 0 && time <= length + 0.5f) hits.Add(time);
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
        string text = _holding ? $"BLOQUEANDO · {c.Name}" : $"Ataque de {c.Name} em {ThreatSeconds:F2} s";
        r.DrawText(text, cx, y, _holding ? HoldColor : WarnColor, TextAlign.Center, title: true);
    }

    /// <summary>Frame sem leitura do jogo: solta a tecla se estiver segurando.</summary>
    public void Suspend()
    {
        Threat = null;
        Release("sem leitura do jogo");
    }

    public void Dispose() => Release("encerrando");
}
