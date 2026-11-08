using System.Diagnostics;
using G2Overlay.Config;
using G2Overlay.Memory;
using G2Overlay.UnrealSDK;
using G2Overlay.Utils;

namespace G2Overlay.ESP;

/// <summary>
/// Descobre criaturas percorrendo UWorld.Levels[*].Actors e mantém o estado por frame.
///
/// Custo: o scan completo roda a cada EntityScanIntervalMs; atores que não são criaturas
/// ficam num cache negativo (sem nova leitura de classe) até a revalidação completa.
/// Por frame: ~3 leituras por criatura dentro do alcance (posição, vida, bounds).
/// </summary>
public sealed class EntityManager
{
    private const string Cat = "Entities";

    private readonly ProcessMemory _mem;
    private readonly ObjectReader _obj;
    private readonly FNameResolver _names;
    private readonly LayoutDetector _layout;
    private readonly GameWorld _world;
    private readonly OverlayConfig _cfg;

    private readonly Dictionary<ulong, Creature> _creatures = new(256);
    private readonly Dictionary<ulong, Collectible> _collectibles = new(64);
    private readonly Dictionary<ulong, string?> _classMatchCache = new(512);
    private string[] _filters = Array.Empty<string>();
    private string[] _itemTokens = Array.Empty<string>();

    /// <summary>Categoria das flechas no chão (controlada por EspArrows, não por CollectibleFilters).</summary>
    public const string ArrowCategory = "arrow";
    private string _filtersKey = "";
    // Cache negativo: ator → instante (Stopwatch ticks) em que deve ser reclassificado.
    // Cada entrada expira num tempo aleatório entre 0,5× e 1,5× FullRevalidateIntervalMs, para que a
    // revalidação se espalhe pelos scans em vez de acontecer toda de uma vez (evita engasgos).
    private readonly Dictionary<ulong, long> _nonCreatures = new(32768);
    private readonly Random _rng = new();
    private bool _initialScanDone;

    /// <summary>Máximo de classificações (leituras de classe/RowName) por scan depois do scan inicial.</summary>
    private const int ClassifyBudgetPerScan = 3000;
    private readonly HashSet<ulong> _seen = new(512);
    private readonly List<ulong> _levels = new(512);
    private ulong[] _actorBuffer = new ulong[4096];
    private readonly List<ulong> _toRemove = new(64);

    private long _lastScan;
    private long _lastFullRevalidate;
    private readonly LogThrottle _summaryLog = new(TimeSpan.FromSeconds(5));
    private bool _warnedHealthClass;

    public IReadOnlyCollection<Creature> Creatures => _creatures.Values;
    public IReadOnlyCollection<Collectible> Collectibles => _collectibles.Values;
    public int VisibleCollectibles { get; private set; }
    public int LevelCount { get; private set; }
    public int ActorCount { get; private set; }
    public int VisibleCount { get; private set; }
    public double LastScanMs { get; private set; }

    public EntityManager(ProcessMemory mem, ObjectReader obj, FNameResolver names, LayoutDetector layout, GameWorld world, OverlayConfig cfg)
    {
        _mem = mem;
        _obj = obj;
        _names = names;
        _layout = layout;
        _world = world;
        _cfg = cfg;
    }

    public void Clear()
    {
        _creatures.Clear();
        _collectibles.Clear();
        _classMatchCache.Clear();
        _nonCreatures.Clear();
        _initialScanDone = false;
        _lastScan = 0;
        Logger.Info(Cat, "Lista de entidades limpa.");
    }

    // ================================================================== SCAN

    public void ScanIfDue()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastScan != 0 && Stopwatch.GetElapsedTime(_lastScan, now).TotalMilliseconds < _cfg.EntityScanIntervalMs) return;
        _lastScan = now;

        if (_lastFullRevalidate == 0 || Stopwatch.GetElapsedTime(_lastFullRevalidate, now).TotalMilliseconds >= _cfg.FullRevalidateIntervalMs)
        {
            // Só faz a limpeza do cache negativo (atores destruídos); a revalidação em si é gradual.
            _lastFullRevalidate = now;
            _toRemove.Clear();
            foreach (var kv in _nonCreatures)
                if (kv.Value < now) _toRemove.Add(kv.Key);
            foreach (var k in _toRemove) _nonCreatures.Remove(k);
            _toRemove.Clear();
        }

        try
        {
            Scan();
        }
        catch (Exception ex)
        {
            Logger.Error(Cat, "Exceção durante o scan de entidades.", ex);
        }

        LastScanMs = Stopwatch.GetElapsedTime(now).TotalMilliseconds;
    }

    private void Scan()
    {
        _world.GetLevels(_levels);
        LevelCount = _levels.Count;
        if (_levels.Count == 0) return;

        if (!_layout.ActorsValidated)
        {
            foreach (var l in _levels)
                if (_layout.DetectLevelActors(l)) break;
            if (!_layout.ActorsValidated) return;
        }

        // Tenta validar ComponentToWorld com o pawn local (componente raiz sem AttachParent).
        if (!_layout.TransformValidated && _world.LocalPawn != 0)
            _layout.DetectComponentToWorld(_mem.ReadPtr(_world.LocalPawn + (ulong)Off.Actor_RootComponent));

        _seen.Clear();
        int actorTotal = 0, added = 0, addedCollectibles = 0, deferred = 0;
        long scanStart = Stopwatch.GetTimestamp();
        int budget = _initialScanDone ? ClassifyBudgetPerScan : int.MaxValue;
        RefreshFilters();

        foreach (ulong level in _levels)
        {
            if (!_mem.TryRead(level + (ulong)Off.Level_Actors, out TArrayHeader arr)) continue;
            if (arr.Num <= 0 || !arr.IsPlausible(2_000_000)) continue;

            if (_actorBuffer.Length < arr.Num) _actorBuffer = new ulong[Math.Max(arr.Num, _actorBuffer.Length * 2)];
            var span = _actorBuffer.AsSpan(0, arr.Num);
            if (!_mem.ReadArray(arr.Data, span)) continue;
            actorTotal += arr.Num;

            foreach (ulong actor in span)
            {
                if (!ProcessMemory.IsValidObjectPointer(actor) || actor == _world.LocalPawn) continue;
                if (_nonCreatures.TryGetValue(actor, out long expires) && expires > scanStart) continue;

                if (_creatures.TryGetValue(actor, out var existing))
                {
                    // Garante que o endereço não foi reaproveitado por outro objeto.
                    if (_obj.GetClass(actor) == existing.ClassPtr)
                    {
                        _seen.Add(actor);
                        continue;
                    }

                    _creatures.Remove(actor);
                }

                if (_collectibles.TryGetValue(actor, out var existingCol))
                {
                    if ((_cfg.EspCollectibles || _cfg.EspArrows) && _obj.GetClass(actor) == existingCol.ClassPtr)
                    {
                        _seen.Add(actor);
                        continue;
                    }

                    _collectibles.Remove(actor);
                }

                // Orçamento de classificação: depois do primeiro scan, no máximo N atores novos/expirados por scan.
                if (budget <= 0)
                {
                    deferred++;
                    continue;
                }

                budget--;
                ulong cls = _obj.GetClass(actor);
                var ci = _obj.GetClassInfo(cls);
                if (ci == null) continue; // leitura falhou: tenta no próximo scan
                if (!ci.IsSurvivalCharacter)
                {
                    var col = (_cfg.EspCollectibles || _cfg.EspArrows) && ci.IsActor ? TryBuildCollectible(actor, cls, ci) : null;
                    if (col != null)
                    {
                        _collectibles[actor] = col;
                        _seen.Add(actor);
                        addedCollectibles++;
                    }
                    else
                    {
                        _nonCreatures[actor] = scanStart + NextExpiry();
                    }

                    continue;
                }

                var c = Build(actor, cls, ci);
                if (c == null) continue;
                _creatures[actor] = c;
                _seen.Add(actor);
                added++;
            }
        }

        ActorCount = actorTotal;
        _initialScanDone = true;
        if (deferred > 0) Logger.Debug(Cat, $"Scan: {deferred} atores adiados para o próximo scan (orçamento {ClassifyBudgetPerScan}).");

        _toRemove.Clear();
        foreach (var k in _creatures.Keys)
            if (!_seen.Contains(k)) _toRemove.Add(k);
        foreach (var k in _toRemove) _creatures.Remove(k);
        int removedCreatures = _toRemove.Count;

        _toRemove.Clear();
        foreach (var k in _collectibles.Keys)
            if (!_seen.Contains(k)) _toRemove.Add(k);
        foreach (var k in _toRemove) _collectibles.Remove(k);
        if (addedCollectibles > 0 || _toRemove.Count > 0)
            Logger.Debug(Cat, $"Scan coletáveis: +{addedCollectibles} -{_toRemove.Count} → {_collectibles.Count}.");
        _toRemove.Clear();

        if (added > 0 || removedCreatures > 0)
            Logger.Debug(Cat, $"Scan: +{added} -{removedCreatures} → {_creatures.Count} criaturas ({actorTotal} atores em {_levels.Count} níveis).");

        if (_summaryLog.Allow())
            Logger.Info(Cat, $"Entidades: {_creatures.Count} criaturas ({VisibleCount} visíveis), {_collectibles.Count} coletáveis ({VisibleCollectibles} visíveis), {actorTotal} atores, {_levels.Count} níveis, " +
                             $"cache negativo={_nonCreatures.Count}, classes={_obj.CachedClasses}, nomes={_names.CacheSize}, scan={LastScanMs:F1} ms.");
    }

    private Creature? Build(ulong actor, ulong cls, ClassInfo ci)
    {
        var c = new Creature
        {
            Actor = actor,
            ClassPtr = cls,
            Class = ci,
            IsPlayer = ci.IsPlayerCharacter,
            TameableComponent = _mem.ReadPtr(actor + (ulong)Off.SurvivalCharacter_TameableComponent),
            IsTamedByName = IsPetOrBuggyClass(ci),
            RootComponent = _mem.ReadPtr(actor + (ulong)Off.Actor_RootComponent),
            Mesh = _mem.ReadPtr(actor + (ulong)Off.Character_Mesh),
            Capsule = _mem.ReadPtr(actor + (ulong)Off.Character_CapsuleComponent),
            HealthComponent = _mem.ReadPtr(actor + (ulong)Off.SurvivalCharacter_HealthComponent),
            TeamComponent = _mem.ReadPtr(actor + (ulong)Off.SurvivalCharacter_TeamComponent),
        };

        if (!ProcessMemory.IsValidObjectPointer(c.RootComponent) || !ProcessMemory.IsValidObjectPointer(c.HealthComponent))
            return null;

        // Validação dos offsets: o componente de vida precisa ser um UHealthComponent.
        var hci = _obj.GetObjectClassInfo(c.HealthComponent);
        if (hci == null || Array.IndexOf(hci.Chain, "HealthComponent") < 0)
        {
            if (!_warnedHealthClass)
            {
                _warnedHealthClass = true;
                Logger.Error(Cat, $"SurvivalCharacter.HealthComponent (0x{Off.SurvivalCharacter_HealthComponent:X}) aponta para '{hci?.Name ?? "?"}', " +
                                  "não UHealthComponent. Offsets desatualizados — gere um novo dump.");
            }

            return null;
        }

        // Nome: CharacterData.RowName (linha da DataTable da espécie) ou o nome da classe.
        var rowName = _mem.Read<FName>(actor + (ulong)Off.SurvivalCharacter_CharacterData + (ulong)Off.DataTableRowHandle_RowName);
        string row = rowName.ComparisonIndex > 0 ? _names.GetName(rowName) : "";
        c.Name = row.Length > 0 && row != "None" ? row.Replace('_', ' ') : ci.FriendlyName;

        if (ProcessMemory.IsValidObjectPointer(c.TeamComponent))
        {
            var teamRow = _mem.Read<FName>(c.TeamComponent + (ulong)Off.TeamComponent_TeamDataTable + (ulong)Off.DataTableRowHandle_RowName);
            string t = teamRow.ComparisonIndex > 0 ? _names.GetName(teamRow) : "";
            c.Team = t == "None" ? "" : t;
        }

        if (_cfg.ApplyHealthMultipliers)
        {
            float m1 = _mem.Read<float>(c.HealthComponent + (ulong)Off.HealthComponent_NewGamePlusHealthMultiplier);
            float m2 = _mem.Read<float>(c.HealthComponent + (ulong)Off.HealthComponent_CustomGameModeHealthMultiplier);
            float m3 = _mem.Read<float>(c.HealthComponent + (ulong)Off.HealthComponent_CustomPropertyHealthMultiplier);
            c.HealthMultiplier = SaneMultiplier(m1) * SaneMultiplier(m2) * SaneMultiplier(m3);
        }

        if (ProcessMemory.IsValidObjectPointer(c.Capsule))
        {
            c.CapsuleHalfHeight = _mem.Read<float>(c.Capsule + (ulong)Off.CapsuleComponent_CapsuleHalfHeight);
            c.CapsuleRadius = _mem.Read<float>(c.Capsule + (ulong)Off.CapsuleComponent_CapsuleRadius);
        }

        if (!(c.CapsuleHalfHeight > 0 && c.CapsuleHalfHeight < 20000)) c.CapsuleHalfHeight = 50f;
        if (!(c.CapsuleRadius > 0 && c.CapsuleRadius < 20000)) c.CapsuleRadius = 30f;

        // Validações de layout oportunistas.
        if (!_layout.TransformValidated) _layout.DetectComponentToWorld(c.RootComponent);
        if (!_layout.BoundsValidated && ProcessMemory.IsValidObjectPointer(c.Mesh))
            _layout.DetectBounds(c.Mesh, ReadLocation(c.RootComponent));

        FindWeakSpots(c, ci);

        Logger.Debug(Cat, $"Nova criatura: '{c.Name}' [{ci.Name}] time='{c.Team}' actor=0x{actor:X} health=0x{c.HealthComponent:X} mult={c.HealthMultiplier:F2} pontos fracos={c.WeakSpots.Length}");
        return c;
    }

    private long NextExpiry() =>
        (long)(_cfg.FullRevalidateIntervalMs * (0.5 + _rng.NextDouble()) * Stopwatch.Frequency / 1000.0);

    // ================================================================== COLETÁVEIS

    private void RefreshFilters()
    {
        string key = string.Join("|", _cfg.CollectibleFilters) + "#" + string.Join("|", _cfg.CollectibleItemTokens);
        if (key == _filtersKey) return;
        _filtersKey = key;
        _filters = _cfg.CollectibleFilters.Select(ObjectReader.Normalize).Where(f => f.Length > 0).Distinct().ToArray();
        _itemTokens = _cfg.CollectibleItemTokens.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _classMatchCache.Clear();
        _nonCreatures.Clear();
        Logger.Info(Cat, $"Filtros de coletáveis: {string.Join(", ", _filters)} · palavras de item: {string.Join(", ", _itemTokens)}");
    }

    /// <summary>"BP_Key_CoaltanaMats_C" → "key" se "Key" estiver em CollectibleItemTokens (comparação por palavra entre "_").</summary>
    private string? MatchItemToken(string className)
    {
        foreach (var part in className.Split('_', StringSplitOptions.RemoveEmptyEntries))
            foreach (var t in _itemTokens)
                if (part.Equals(t, StringComparison.OrdinalIgnoreCase)) return t.ToLowerInvariant();
        return null;
    }

    private string? MatchFilter(string normalized)
    {
        foreach (var f in _filters)
            if (normalized.Contains(f, StringComparison.Ordinal)) return f;
        return null;
    }

    /// <summary>
    /// Ator é coletável se um termo de CollectibleFilters aparece (normalizado) em:
    ///   1. o nome de alguma classe da cadeia (ex.: BP_Milk_Nugget_Personal_C → "milknugget");
    ///   2. HarvestNode.NodeData.RowName;
    ///   3. SpawnedItem.PlacedItemData.RowName ou SpawnedItem.Item→ItemDataRowHandle.RowName
    ///      (itens no chão cuja classe é genérica, ex.: BP_WorldItem_C).
    /// Tech Chips casam já no passo 1: BP_SuperTechChip_Ominent_Stolen_C ► … ► BP_TechChip_Base_C ► BP_WorldItem_C.
    /// </summary>
    private Collectible? TryBuildCollectible(ulong actor, ulong cls, ClassInfo ci)
    {
        if (_filters.Length == 0 && _itemTokens.Length == 0 && !_cfg.EspArrows) return null;

        string? filter = null, matchedBy = null, rowName = "";
        if (!_classMatchCache.TryGetValue(cls, out string? classFilter))
        {
            classFilter = null;
            foreach (var n in ci.Chain)
            {
                if (n == "Actor") break;
                classFilter = MatchFilter(ObjectReader.Normalize(n));
                if (classFilter != null) break;
            }

            // Flechas no chão: "Arrow" como palavra inteira (BP_World_Arrow_C, BP_World_Arrow_Mint_C…), só em itens.
            if (ci.IsSpawnedItem && classFilter == null)
            {
                foreach (var n in ci.Chain)
                {
                    if (n == "BP_WorldItem_C" || n == "SpawnedItem") break;
                    if (n.Split('_').Any(part => part.Equals("Arrow", StringComparison.OrdinalIgnoreCase)))
                    {
                        classFilter = ArrowCategory;
                        break;
                    }
                }
            }

            // Palavras de item (Key, Recipe): palavra inteira do nome da classe, só em itens no chão.
            if (classFilter == null && ci.IsSpawnedItem && _itemTokens.Length > 0)
            {
                foreach (var n in ci.Chain)
                {
                    if (n == "BP_WorldItem_C" || n == "SpawnedItem") break;
                    classFilter = MatchItemToken(n);
                    if (classFilter != null) break;
                }
            }

            _classMatchCache[cls] = classFilter;
            if (classFilter != null) Logger.Info(Cat, $"Classe coletável reconhecida ({classFilter}): {ci}");
        }

        if (classFilter != null)
        {
            filter = classFilter;
            matchedBy = "classe";
            // Itens no chão: o RowName dá o nome exato da variante (ex.: "TechChip_Ominent_Super_Stolen").
            if (ci.IsSpawnedItem)
                rowName = ReadRowName(actor + (ulong)Off.SpawnedItem_PlacedItemData + (ulong)Off.DataTableRowHandle_RowName);
        }
        else if (ci.IsHarvestNode)
        {
            rowName = ReadRowName(actor + (ulong)Off.HarvestNode_NodeData + (ulong)Off.DataTableRowHandle_RowName);
            filter = MatchFilter(ObjectReader.Normalize(rowName));
            if (filter != null) matchedBy = "HarvestNode.NodeData";
        }
        else if (ci.IsSpawnedItem)
        {
            rowName = ReadRowName(actor + (ulong)Off.SpawnedItem_PlacedItemData + (ulong)Off.DataTableRowHandle_RowName);
            filter = MatchFilter(ObjectReader.Normalize(rowName));
            if (filter != null)
            {
                matchedBy = "SpawnedItem.PlacedItemData";
            }
            else
            {
                ulong item = _mem.ReadPtr(actor + (ulong)Off.SpawnedItem_Item);
                if (ProcessMemory.IsValidObjectPointer(item))
                {
                    string itemRow = ReadRowName(item + (ulong)Off.Item_ItemDataRowHandle + (ulong)Off.DataTableRowHandle_RowName);
                    filter = MatchFilter(ObjectReader.Normalize(itemRow));
                    if (filter != null)
                    {
                        rowName = itemRow;
                        matchedBy = "Item.ItemDataRowHandle";
                    }
                }
            }
        }

        // 4. Nome do objeto no mapa (ex.: "BP_SuperTechChip_Ominent_Stolen_C_2147228128"), para qualquer ator.
        if (filter == null)
        {
            string objName = _names.GetObjectName(actor);
            filter = MatchFilter(ObjectReader.Normalize(objName));
            if (filter != null)
            {
                matchedBy = "nome do objeto";
                if (rowName.Length == 0) rowName = objName;
            }
        }

        if (filter == null || matchedBy == null) return null;

        var c = new Collectible
        {
            Actor = actor,
            ClassPtr = cls,
            Class = ci,
            Filter = filter,
            MatchedBy = matchedBy,
            ColorHex = ColorFor(filter),
            RootComponent = _mem.ReadPtr(actor + (ulong)Off.Actor_RootComponent),
        };
        if (!ProcessMemory.IsValidObjectPointer(c.RootComponent)) return null;

        if (ci.IsHarvestNode)
        {
            ulong hc = _mem.ReadPtr(actor + (ulong)Off.HarvestNode_HealthComponent);
            if (ProcessMemory.IsValidObjectPointer(hc)) c.HealthComponent = hc;
        }

        string fallback = rowName.Length > 0 ? Collectible.Prettify(rowName) : ci.FriendlyName;
        if (Array.IndexOf(ci.Chain, "BossManager") >= 0)
        {
            c.IsBossManager = true;
            c.Name = Collectible.BossName(ci.Name);
        }
        else if (Array.IndexOf(ci.Chain, "BP_World_Buried_UniqueChest_Base_C") >= 0)
        {
            c.Name = Collectible.UniqueChestName(ci.Name);
        }
        else if (Array.IndexOf(ci.Chain, "BP_TechChip_Base_C") >= 0)
        {
            c.Name = Collectible.TechChipName(rowName.Length > 0 ? rowName : ci.Name);
        }
        else if (Array.IndexOf(ci.Chain, "BP_Milk_Nugget_Personal_C") >= 0)
        {
            byte type = _mem.Read<byte>(actor + (ulong)Off.MilkNugget_MolarType);
            c.Name = Collectible.MolarName(type, fallback);
        }
        else
        {
            c.Name = fallback;
        }

        Logger.Info(Cat, $"Novo coletável: '{c.Name}' [{ci.Name}] filtro='{filter}' por {matchedBy} row='{rowName}' actor=0x{actor:X}");
        return c;
    }

    private string ReadRowName(ulong fnameAddress)
    {
        var n = _mem.Read<FName>(fnameAddress);
        if (n.ComparisonIndex <= 0) return "";
        string s = _names.GetName(n);
        return s == "None" ? "" : s;
    }

    private string ColorFor(string filter)
    {
        foreach (var kv in _cfg.CollectibleColors)
            if (ObjectReader.Normalize(kv.Key) == filter) return kv.Value;
        return _cfg.CollectibleColor;
    }

    /// <summary>Lê TameableComponent → TameData → TameState (2 = Tame). O estado muda ao domar, então é lido por frame.</summary>
    private bool ReadIsTamed(Creature c)
    {
        if (!ProcessMemory.IsValidObjectPointer(c.TameableComponent)) return false;
        ulong data = _mem.ReadPtr(c.TameableComponent + (ulong)Off.TameableComponent_TameData);
        if (!ProcessMemory.IsValidObjectPointer(data)) return false;
        return _mem.TryRead(data + (ulong)Off.TamedCreatureData_TameState, out byte state) && state == 2;
    }

    /// <summary>"Buggy" ou "Pet" como palavra do nome de alguma classe da cadeia (separada por "_").</summary>
    private static bool IsPetOrBuggyClass(ClassInfo ci)
    {
        foreach (var n in ci.Chain)
        {
            if (n == "SurvivalCreature") break;
            foreach (var part in n.Split('_'))
                if (part.Equals("Buggy", StringComparison.OrdinalIgnoreCase) || part.Equals("Pet", StringComparison.OrdinalIgnoreCase))
                    return true;
        }

        return false;
    }

    // ================================================================== PONTOS FRACOS

    private readonly HashSet<ulong> _weakSpotClassLogged = new();

    /// <summary>
    /// Percorre BlueprintCreatedComponents + InstanceComponents da criatura e guarda os componentes de cena cujo
    /// nome contém um termo de WeakSpotTerms (padrão "weak": LeftEyeWeakspot, RightLegWeakpoint1…).
    /// Feito uma vez por criatura (no scan); as posições são lidas por frame.
    /// </summary>
    private void FindWeakSpots(Creature c, ClassInfo ci)
    {
        var found = new List<ulong>(4);
        var names = new List<string>(4);
        foreach (int arrOff in new[] { Off.Actor_BlueprintCreatedComponents, Off.Actor_InstanceComponents })
        {
            if (!_mem.TryRead(c.Actor + (ulong)arrOff, out TArrayHeader arr)) continue;
            if (arr.Num <= 0 || !arr.IsPlausible(1024)) continue;
            var comps = new ulong[arr.Num];
            if (!_mem.ReadArray(arr.Data, comps.AsSpan())) continue;

            foreach (ulong comp in comps)
            {
                if (!ProcessMemory.IsValidObjectPointer(comp) || found.Contains(comp)) continue;
                string name = _names.GetObjectName(comp);
                if (name.Length == 0 || !_cfg.WeakSpotTerms.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
                var cci = _obj.GetObjectClassInfo(comp);
                if (cci == null || Array.IndexOf(cci.Chain, "SceneComponent") < 0) continue; // precisa ter posição
                found.Add(comp);
                names.Add(name);
            }
        }

        c.WeakSpots = found.ToArray();
        c.WeakSpotNames = names.ToArray();
        c.WeakSpotPos = new FVector[found.Count];
        c.WeakSpotValid = new bool[found.Count];

        if (_weakSpotClassLogged.Add(c.ClassPtr))
        {
            Logger.Info(Cat, found.Count > 0
                ? $"Pontos fracos de {ci.Name}: {string.Join(", ", names)}"
                : $"Pontos fracos de {ci.Name}: nenhum componente com {string.Join("/", _cfg.WeakSpotTerms)} no nome (mira no corpo).");
        }
    }

    private void UpdateWeakSpots(Creature c)
    {
        for (int i = 0; i < c.WeakSpots.Length; i++)
        {
            c.WeakSpotValid[i] = _mem.TryRead(c.WeakSpots[i] + (ulong)Off.SceneComponent_ComponentToWorld + 0x20, out FVector p)
                                 && p.IsFinite && !p.IsZero && p.DistanceTo(c.Location) < 5000;
            if (c.WeakSpotValid[i]) c.WeakSpotPos[i] = p;
        }
    }

    private static float SaneMultiplier(float m) => m > 0.01f && m < 100f && float.IsFinite(m) ? m : 1f;

    private FVector ReadLocation(ulong root)
    {
        if (_layout.TransformValidated &&
            _mem.TryRead(root + (ulong)Off.SceneComponent_ComponentToWorld + 0x20, out FVector t) && t.IsFinite)
            return t;
        return _mem.Read<FVector>(root + (ulong)Off.SceneComponent_RelativeLocation);
    }

    // ================================================================== FRAME

    public void UpdateFrame(Camera cam)
    {
        int visible = 0;
        double maxDist = _cfg.MaxDistanceMeters;
        bool useMesh = _cfg.BoxSource == "Mesh" && _layout.BoundsValidated;

        foreach (var c in _creatures.Values)
        {
            c.Visible = false;
            if (c.IsPlayer && !_cfg.ShowPlayers) continue;

            c.Location = ReadLocation(c.RootComponent);
            if (!c.Location.IsFinite || c.Location.IsZero) continue;

            c.DistanceMeters = c.Location.DistanceTo(cam.Location) / 100.0;
            if (c.DistanceMeters > maxDist) continue;

            if (!ReadHealth(c)) continue;
            c.IsTamed = c.IsTamedByName || ReadIsTamed(c);
            if (c.IsDead && !_cfg.ShowDeadCreatures) continue;

            bool haveBox = false;
            if (useMesh && c.Mesh != 0 &&
                _mem.TryRead(c.Mesh + (ulong)Off.SceneComponent_Bounds, out FBoxSphereBounds b) &&
                LayoutDetector.IsPlausibleBounds(b, c.Location))
            {
                c.BoxCenter = b.Origin;
                c.BoxExtent = b.BoxExtent;
                haveBox = true;
            }

            if (!haveBox)
            {
                c.BoxCenter = c.Location;
                c.BoxExtent = new FVector(c.CapsuleRadius, c.CapsuleRadius, c.CapsuleHalfHeight);
            }

            if (c.HasWeakSpots && _layout.TransformValidated && (_cfg.EspWeakSpots || (_cfg.AimEnabled && _cfg.AimWeakSpots)))
                UpdateWeakSpots(c);

            c.Visible = true;
            visible++;
        }

        VisibleCount = visible;
        UpdateCollectibles(cam);
    }

    // ================================================================== DIAGNÓSTICO (F9)

    /// <summary>
    /// Grava no log todos os atores num raio de <paramref name="radiusMeters"/> da câmera, com cadeia de classes,
    /// nome do objeto e os RowNames de item / nó de coleta. Serve para descobrir como um objeto do jogo é
    /// representado (ex.: qual termo usar em CollectibleFilters). Ignora todos os caches.
    /// </summary>
    public void DumpNearby(Camera cam, double radiusMeters)
    {
        var sw = Stopwatch.StartNew();
        var rows = new List<(double dist, string line)>(256);
        var levels = new List<ulong>(512);
        _world.GetLevels(levels);
        int total = 0;

        foreach (ulong level in levels)
        {
            if (!_mem.TryRead(level + (ulong)Off.Level_Actors, out TArrayHeader arr)) continue;
            if (arr.Num <= 0 || !arr.IsPlausible(2_000_000)) continue;
            var buf = new ulong[arr.Num];
            if (!_mem.ReadArray(arr.Data, buf.AsSpan())) continue;

            foreach (ulong actor in buf)
            {
                if (!ProcessMemory.IsValidObjectPointer(actor)) continue;
                total++;
                ulong root = _mem.ReadPtr(actor + (ulong)Off.Actor_RootComponent);
                if (!ProcessMemory.IsValidObjectPointer(root)) continue;
                var loc = ReadLocation(root);
                if (!loc.IsFinite || loc.IsZero) continue;
                double d = loc.DistanceTo(cam.Location) / 100.0;
                if (d > radiusMeters) continue;

                var ci = _obj.GetObjectClassInfo(actor);
                if (ci == null) continue;
                var sb = new System.Text.StringBuilder(256);
                sb.Append($"{d,6:F1} m  {string.Join(" > ", ci.Chain.Take(5))}  obj='{_names.GetObjectName(actor)}'");

                if (ci.IsSpawnedItem)
                {
                    sb.Append($"  PlacedItemData='{ReadRowName(actor + (ulong)Off.SpawnedItem_PlacedItemData + (ulong)Off.DataTableRowHandle_RowName)}'");
                    ulong item = _mem.ReadPtr(actor + (ulong)Off.SpawnedItem_Item);
                    if (ProcessMemory.IsValidObjectPointer(item))
                        sb.Append($"  Item.ItemData='{ReadRowName(item + (ulong)Off.Item_ItemDataRowHandle + (ulong)Off.DataTableRowHandle_RowName)}'");
                }

                if (ci.IsHarvestNode)
                    sb.Append($"  NodeData='{ReadRowName(actor + (ulong)Off.HarvestNode_NodeData + (ulong)Off.DataTableRowHandle_RowName)}'");

                rows.Add((d, sb.ToString()));
            }
        }

        rows.Sort((a, b) => a.dist.CompareTo(b.dist));
        Logger.Info("Diag", $"===== Diagnóstico F9: {rows.Count} atores a até {radiusMeters:F0} m (de {total} no mundo), {sw.ElapsedMilliseconds} ms =====");
        foreach (var r in rows.Take(300)) Logger.Info("Diag", r.line);
        if (rows.Count > 300) Logger.Info("Diag", $"… {rows.Count - 300} atores omitidos.");
        Logger.Info("Diag", "===== fim do diagnóstico =====");
    }

    private void UpdateCollectibles(Camera cam)
    {
        int visible = 0;
        double maxDist = _cfg.CollectibleMaxDistanceMeters;
        foreach (var c in _collectibles.Values)
        {
            c.Visible = false;
            if (!(c.Filter == ArrowCategory ? _cfg.EspArrows : _cfg.EspCollectibles)) continue;

            c.Location = ReadLocation(c.RootComponent);
            if (!c.Location.IsFinite || c.Location.IsZero) continue;
            c.DistanceMeters = c.Location.DistanceTo(cam.Location) / 100.0;
            if (c.DistanceMeters > maxDist) continue;

            // HealthState == Dead → nó já quebrado (molar coletado).
            c.Collected = c.HealthComponent != 0 &&
                          _mem.TryRead(c.HealthComponent + (ulong)Off.HealthComponent_HealthState, out byte st) && st == 2;
            if (c.Collected && !_cfg.ShowCollectedCollectibles) continue;

            if (c.IsBossManager)
                c.BossActive = _mem.TryRead(c.Actor + (ulong)Off.BossManager_bBossActive, out byte active) && active != 0;

            c.Visible = true;
            visible++;
        }

        VisibleCollectibles = visible;
    }

    private unsafe bool ReadHealth(Creature c)
    {
        int a = Off.HealthComponent_HealthState, b = Off.HealthComponent_MaxHealth, d = Off.HealthComponent_CurrentDamage;
        int lo = Math.Min(a, Math.Min(b, d));
        int hi = Math.Max(a + 1, Math.Max(b + 4, d + 4));
        float max, dmg;
        byte state;

        if (hi - lo <= 64)
        {
            byte* buf = stackalloc byte[64];
            if (!_mem.Read(c.HealthComponent + (ulong)lo, buf, hi - lo)) return false;
            state = buf[a - lo];
            max = *(float*)(buf + (b - lo));
            dmg = *(float*)(buf + (d - lo));
        }
        else
        {
            if (!_mem.TryRead(c.HealthComponent + (ulong)a, out state)) return false;
            if (!_mem.TryRead(c.HealthComponent + (ulong)b, out max)) return false;
            if (!_mem.TryRead(c.HealthComponent + (ulong)d, out dmg)) return false;
        }

        if (!float.IsFinite(max) || max <= 0 || max > 1e7f || !float.IsFinite(dmg)) return false;
        float effMax = max * c.HealthMultiplier;
        c.HealthState = state;
        c.MaxHealth = effMax;
        c.CurrentHealth = Math.Clamp(effMax - dmg, 0f, effMax);
        return true;
    }
}
