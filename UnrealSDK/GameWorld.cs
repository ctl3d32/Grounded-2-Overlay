using G2Overlay.Memory;
using G2Overlay.PatternScanner;
using G2Overlay.Utils;

namespace G2Overlay.UnrealSDK;

/// <summary>
/// Cadeia principal de ponteiros:
///   GWorld → UWorld → OwningGameInstance → LocalPlayers[0] → PlayerController
///          → AcknowledgedPawn (jogador local) / PlayerCameraManager → CameraCachePrivate.POV
/// </summary>
public sealed class GameWorld
{
    private const string Cat = "World";
    private const int ChainTtlMs = 250;

    private readonly ProcessMemory _mem;
    private readonly PointerCache _cache;
    private readonly ObjectReader _obj;
    private readonly GlobalAddresses _globals;
    private readonly LogThrottle _chainLog = new(TimeSpan.FromSeconds(10));

    public ulong World { get; private set; }
    public ulong PersistentLevel { get; private set; }
    public ulong GameInstance { get; private set; }
    public ulong LocalPlayer { get; private set; }
    public ulong PlayerController { get; private set; }
    public ulong LocalPawn { get; private set; }
    public ulong CameraManager { get; private set; }

    public bool HasWorld => World != 0;
    public bool HasCamera => CameraManager != 0;

    private ulong _lastWorld;

    public event Action<ulong, ulong>? WorldChanged;

    public GameWorld(ProcessMemory mem, PointerCache cache, ObjectReader obj, GlobalAddresses globals)
    {
        _mem = mem;
        _cache = cache;
        _obj = obj;
        _globals = globals;
    }

    /// <summary>Atualiza a cadeia de ponteiros (com cache). Retorna true se houver câmera utilizável.</summary>
    public bool Update()
    {
        World = _cache.ReadPtr(_globals.GWorld, ChainTtlMs);
        if (!ProcessMemory.IsValidObjectPointer(World))
        {
            Clear();
            return false;
        }

        if (World != _lastWorld)
        {
            string cls = _obj.GetClassName(World);
            Logger.Info(Cat, $"UWorld mudou: 0x{_lastWorld:X} → 0x{World:X} (classe '{cls}', nome '{_obj.GetName(World)}').");
            var old = _lastWorld;
            _lastWorld = World;
            WorldChanged?.Invoke(old, World);
        }

        PersistentLevel = _cache.ReadPtr(World + (ulong)Off.World_PersistentLevel, ChainTtlMs);
        GameInstance = _cache.ReadPtr(World + (ulong)Off.World_OwningGameInstance, ChainTtlMs);
        if (!ProcessMemory.IsValidObjectPointer(GameInstance))
        {
            ClearPlayer();
            return false;
        }

        ulong lpData = _cache.ReadPtr(GameInstance + (ulong)Off.GameInstance_LocalPlayers, ChainTtlMs);
        LocalPlayer = ProcessMemory.IsValidObjectPointer(lpData) ? _cache.ReadPtr(lpData, ChainTtlMs) : 0;
        if (!ProcessMemory.IsValidObjectPointer(LocalPlayer))
        {
            ClearPlayer();
            return false;
        }

        PlayerController = _cache.ReadPtr(LocalPlayer + (ulong)Off.Player_PlayerController, ChainTtlMs);
        if (!ProcessMemory.IsValidObjectPointer(PlayerController))
        {
            ClearPlayer();
            return false;
        }

        LocalPawn = _cache.ReadPtr(PlayerController + (ulong)Off.PlayerController_AcknowledgedPawn, ChainTtlMs);
        CameraManager = _cache.ReadPtr(PlayerController + (ulong)Off.PlayerController_PlayerCameraManager, ChainTtlMs);
        if (!ProcessMemory.IsValidObjectPointer(LocalPawn)) LocalPawn = 0;
        if (!ProcessMemory.IsValidObjectPointer(CameraManager)) CameraManager = 0;

        if (Logger.IsEnabled(LogLevel.Debug) && _chainLog.Allow())
        {
            Logger.Debug(Cat, $"Cadeia: World=0x{World:X} GI=0x{GameInstance:X} LP=0x{LocalPlayer:X} PC=0x{PlayerController:X} " +
                              $"Pawn=0x{LocalPawn:X} Cam=0x{CameraManager:X}");
        }

        return CameraManager != 0;
    }

    public bool ReadCamera(out FMinimalViewInfoHead pov)
    {
        pov = default;
        if (CameraManager == 0) return false;
        ulong addr = CameraManager + (ulong)Off.PlayerCameraManager_CameraCachePrivate + (ulong)Off.CameraCacheEntry_POV;
        // Location/Rotation/FOV ficam no início do FMinimalViewInfo; se a ordem mudar, lê campo a campo.
        if (Off.MinimalViewInfo_Location == 0 && Off.MinimalViewInfo_Rotation == 0x18 && Off.MinimalViewInfo_FOV == 0x30)
            return _mem.TryRead(addr, out pov);

        bool ok = _mem.TryRead(addr + (ulong)Off.MinimalViewInfo_Location, out FVector loc);
        ok &= _mem.TryRead(addr + (ulong)Off.MinimalViewInfo_Rotation, out FRotator rot);
        ok &= _mem.TryRead(addr + (ulong)Off.MinimalViewInfo_FOV, out float fov);
        pov.Location = loc;
        pov.Rotation = rot;
        pov.FOV = fov;
        return ok;
    }

    /// <summary>Todos os ULevel carregados (World.Levels inclui as células de streaming / World Partition).</summary>
    public void GetLevels(List<ulong> levels)
    {
        levels.Clear();
        if (World == 0) return;

        if (_mem.TryRead(World + (ulong)Off.World_Levels, out TArrayHeader arr) && arr.Num > 0 && arr.IsPlausible(100_000))
        {
            var buf = new ulong[arr.Num];
            if (_mem.ReadArray(arr.Data, buf.AsSpan()))
            {
                foreach (var l in buf)
                    if (ProcessMemory.IsValidObjectPointer(l)) levels.Add(l);
            }
        }

        if (PersistentLevel != 0 && !levels.Contains(PersistentLevel)) levels.Insert(0, PersistentLevel);
    }

    public void InvalidateCache() => _cache.Clear();

    private void ClearPlayer()
    {
        LocalPlayer = PlayerController = LocalPawn = CameraManager = 0;
    }

    private void Clear()
    {
        World = PersistentLevel = GameInstance = 0;
        ClearPlayer();
    }
}
