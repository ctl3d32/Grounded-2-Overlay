using System.Diagnostics;
using G2Overlay.Config;
using G2Overlay.ESP;
using G2Overlay.Memory;
using G2Overlay.PatternScanner;
using G2Overlay.UnrealSDK;
using G2Overlay.Utils;

namespace G2Overlay.Core;

public enum SessionState
{
    Scanning,
    WaitingForNames,
    WaitingForWorld,
    Running,
    Failed,
}

/// <summary>
/// Uma conexão com uma instância do jogo: abre o processo, resolve as globais por AOB,
/// valida o layout e mantém mundo + entidades. Descartada quando o processo fecha.
/// </summary>
public sealed class GameSession : IDisposable
{
    private const string Cat = "Session";

    private readonly OverlayConfig _cfg;
    private readonly Stopwatch _retry = Stopwatch.StartNew();
    private readonly LogThrottle _stateLog = new(TimeSpan.FromSeconds(15));
    private Dictionary<string, List<ulong>>? _candidates;
    private bool _candidatesFromCache;
    private bool _runtimeOffsetsSaved;
    private PeInfo? _pe;
    private SignatureResolver? _resolver;

    public ProcessMemory Mem { get; }
    public SessionState State { get; private set; } = SessionState.Scanning;
    public string StatusText { get; private set; } = "Iniciando…";
    public GlobalAddresses Globals { get; } = new();
    public PointerCache Cache { get; }
    public FNameResolver Names { get; }
    public ObjectReader Objects { get; }
    public LayoutDetector Layout { get; }
    public GameWorld? World { get; private set; }
    public EntityManager? Entities { get; private set; }
    public Camera Camera { get; } = new();
    public IntPtr GameWindow { get; set; }

    private GameSession(ProcessMemory mem, OverlayConfig cfg)
    {
        Mem = mem;
        _cfg = cfg;
        Cache = new PointerCache(mem);
        Names = new FNameResolver(mem);
        Objects = new ObjectReader(mem, Names);
        Layout = new LayoutDetector(mem, Objects);
    }

    public static GameSession? TryAttach(Process process, OverlayConfig cfg)
    {
        string module = cfg.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? cfg.ProcessName : cfg.ProcessName + ".exe";
        var mem = ProcessMemory.Open(process.Id, module);
        if (mem == null) return null;
        Logger.Info(Cat, $"Jogo detectado: {process.ProcessName} (PID {process.Id}).");
        return new GameSession(mem, cfg);
    }

    /// <summary>Avança a máquina de estados de inicialização. Chamado a cada frame.</summary>
    public void Tick()
    {
        try
        {
            switch (State)
            {
                case SessionState.Scanning:
                    DoScan();
                    break;
                case SessionState.WaitingForNames:
                    if (_retry.ElapsedMilliseconds > 1000) DoNames();
                    break;
                case SessionState.WaitingForWorld:
                    if (_retry.ElapsedMilliseconds > 1000) DoWorld();
                    break;
                case SessionState.Running:
                    if (!_runtimeOffsetsSaved && Layout.ActorsValidated && Layout.TransformValidated && Layout.BoundsValidated)
                    {
                        _runtimeOffsetsSaved = true;
                        Logger.Info(Cat, "Todos os offsets não refletidos foram validados em tempo de execução.");
                        Off.Save(ConfigManager.OffsetsPath, "dump + validação em tempo de execução");
                    }

                    break;
                case SessionState.Failed:
                    if (_retry.ElapsedMilliseconds > 10000)
                    {
                        Logger.Info(Cat, "Nova tentativa de inicialização após falha.");
                        State = SessionState.Scanning;
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(Cat, $"Exceção no estado {State}.", ex);
            Fail("Erro interno (ver log)");
        }
    }

    private void DoScan()
    {
        StatusText = "Pattern scanning…";
        _pe ??= new ModuleScanner(Mem).ReadPeInfo();
        if (_pe == null)
        {
            Fail("Cabeçalho PE ilegível");
            return;
        }

        _resolver ??= new SignatureResolver(Mem, ConfigManager.ConfigDirectory);
        _candidates = _resolver.FindCandidates(_pe, allowCache: !_candidatesFromCache, out _candidatesFromCache);

        foreach (var key in new[] { "GWorld", "GNames", "GObjects" })
        {
            if (!_candidates.ContainsKey(key) || _candidates[key].Count == 0)
            {
                if (_candidatesFromCache)
                {
                    // força varredura real
                    State = SessionState.Scanning;
                    return;
                }

                Logger.Error(Cat, $"Nenhuma assinatura válida para {key}. Atualize Config/signatures.json (ver docs/ATUALIZACAO.md).");
                Fail($"AOB de {key} não encontrado");
                return;
            }
        }

        State = SessionState.WaitingForNames;
        _retry.Restart();
        DoNames();
    }

    private void DoNames()
    {
        _retry.Restart();
        StatusText = "Validando GNames…";
        foreach (ulong cand in _candidates!["GNames"])
        {
            if (Names.Initialize(cand))
            {
                Globals.GNames = cand;
                break;
            }
        }

        if (!Names.IsReady)
        {
            if (_candidatesFromCache) { InvalidateCacheAndRescan(); return; }
            if (_stateLog.Allow()) Logger.Warn(Cat, "GNames ainda não validado (jogo carregando?). Tentando novamente…");
            return;
        }

        // GObjects: NumElements plausível.
        foreach (ulong cand in _candidates["GObjects"])
        {
            int num = Mem.Read<int>(cand + (ulong)Off.FUObjectArray_NumElements);
            if (num > 1000 && num < 50_000_000)
            {
                Globals.GObjects = cand;
                Logger.Info(Cat, $"GObjects validado: 0x{cand:X} (RVA 0x{cand - Mem.ModuleBase:X}), {num} objetos.");
                break;
            }

            Logger.Warn(Cat, $"GObjects candidato 0x{cand:X} rejeitado (NumElements={num}).");
        }

        if (Globals.GObjects == 0)
        {
            // GObjects é usado apenas para diagnóstico: aceita o primeiro candidato.
            Globals.GObjects = _candidates["GObjects"][0];
            Logger.Warn(Cat, "GObjects não validado; seguindo (não é essencial para o ESP).");
        }

        State = SessionState.WaitingForWorld;
        DoWorld();
    }

    private void DoWorld()
    {
        _retry.Restart();
        StatusText = "Aguardando UWorld…";
        bool anyRejected = false;
        foreach (ulong cand in _candidates!["GWorld"])
        {
            ulong world = Mem.ReadPtr(cand);
            if (!ProcessMemory.IsValidObjectPointer(world)) continue;
            anyRejected = true;
            string cls = Objects.GetClassName(world);
            if (cls != "World")
            {
                Logger.Warn(Cat, $"GWorld candidato 0x{cand:X} → objeto de classe '{cls}', rejeitado.");
                continue;
            }

            if (!Objects.DetectSuperStruct(world)) continue;

            Globals.GWorld = cand;
            Globals.Source = _candidatesFromCache ? "cache de scan" : "pattern scan";
            Logger.Info(Cat, $"GWorld validado: 0x{cand:X} (RVA 0x{cand - Mem.ModuleBase:X}) → UWorld '{Objects.GetName(world)}'.");
            Logger.Info(Cat, $"Globais: {Globals}");
            _resolver!.SaveCache(_pe!, Globals);

            World = new GameWorld(Mem, Cache, Objects, Globals);
            Entities = new EntityManager(Mem, Objects, Names, Layout, World, _cfg);
            World.WorldChanged += (_, _) =>
            {
                Entities.Clear();
                Cache.Clear();
            };
            State = SessionState.Running;
            StatusText = "Conectado";
            return;
        }

        if (_candidatesFromCache && anyRejected) { InvalidateCacheAndRescan(); return; }
        if (_stateLog.Allow()) Logger.Info(Cat, "UWorld ainda não disponível (tela de carregamento / menu inicial).");
    }

    private void InvalidateCacheAndRescan()
    {
        Logger.Warn(Cat, "Endereços do cache não validaram; refazendo o pattern scan.");
        State = SessionState.Scanning; // _candidatesFromCache=true → próxima varredura ignora o cache
    }

    private void Fail(string reason)
    {
        StatusText = "Falha: " + reason;
        State = SessionState.Failed;
        _retry.Restart();
        Logger.Error(Cat, $"Inicialização falhou: {reason}. Nova tentativa em 10 s.");
    }

    /// <summary>"Recarregar" no menu: limpa caches e revalida o layout com os offsets atuais.</summary>
    public void ResetCaches()
    {
        Cache.Clear();
        Objects.ClearCache();
        Layout.Reset();
        Entities?.Clear();
        _runtimeOffsetsSaved = false;
        Logger.Info(Cat, "Caches e validações de layout reiniciados.");
    }

    /// <summary>Atualiza câmera e entidades (fase de leitura do frame).</summary>
    public bool UpdateFrame(float screenW, float screenH)
    {
        if (State != SessionState.Running || World == null || Entities == null) return false;

        if (!World.Update())
        {
            StatusText = World.HasWorld ? "Sem jogador local (menu?)" : "Sem UWorld";
            return false;
        }

        if (!World.ReadCamera(out var pov))
        {
            StatusText = "Câmera ilegível";
            return false;
        }

        Camera.Update(pov, screenW, screenH);
        if (!Camera.IsValid)
        {
            StatusText = "Câmera inválida";
            return false;
        }

        Entities.ScanIfDue();
        Entities.UpdateFrame(Camera);
        StatusText = "Conectado";
        return true;
    }

    public void Dispose()
    {
        Mem.Dispose();
    }
}
