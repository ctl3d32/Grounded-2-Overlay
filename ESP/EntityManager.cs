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
    // Versão dos filtros: o resultado do filtro de classe fica no próprio ClassInfo (ClassInfo.MatchVersion);
    // mudar filtros/opções incrementa a versão e invalida todos de uma vez. Como o ClassInfo é recriado quando
    // o endereço da classe é reaproveitado, um resultado antigo nunca vale para outra classe.
    private int _filterVersion;
    private string[] _filters = Array.Empty<string>();
    private string[] _itemTokens = Array.Empty<string>();

    /// <summary>Categoria das flechas no chão (controlada por EspArrows, não por CollectibleFilters).</summary>
    public const string ArrowCategory = "arrow";

    /// <summary>Categoria das gotas no chão (controlada por EspDroplets).</summary>
    public const string DropletCategory = "droplet";

    /// <summary>Raw Science (classes derivadas de ScienceCollectable; controlado por EspRawScience).</summary>
    public const string RawScienceCategory = "rawscience";

    /// <summary>Ovos para chocar/domar (BP_World_Hatchable_*, RowName "Taming_Egg…" / "Hatching…Egg"; controlado por EspEggs).</summary>
    public const string EggCategory = "egg";

    /// <summary>
    /// Transmissor ORC = BP_Burgle_Quest_Flag_C (ScanSwitch; missões do Burgle). Não tem versão leve: os 12 do mapa ficam no
    /// PersistentLevel e estão sempre carregados, então aparecem a qualquer distância (slider da versão leve).
    /// </summary>
    public const string OrcTransmitterCategory = "orctransmitter";

    /// <summary>
    /// Arenas de chefes (BP_BossManager_*; controlado por EspBossArenas). Também não têm versão leve: ficam no
    /// PersistentLevel, sempre carregadas, e usam a distância da versão leve. Mesmo nome do termo antigo de CollectibleFilters.
    /// </summary>
    public const string BossCategory = "bossmanager";

    /// <summary>Receitas no chão (BP_Recipe_*_C, RowName "Recipe…"; controlado por EspRecipes). Têm versão leve.</summary>
    public const string RecipeCategory = "recipe";

    /// <summary>Tech Chips (BP_TechChip_Base_C, RowName "TechChip_…"; controlado por EspTechChips). Têm versão leve.</summary>
    public const string TechChipCategory = "techchip";

    /// <summary>Chaves no chão (palavra "Key" na classe ou no RowName; controlado por EspKeys). Têm versão leve.</summary>
    public const string KeyCategory = "key";

    /// <summary>
    /// Baús únicos enterrados (BP_World_Buried_UniqueChest_* → HarvestNode; controlado por EspUniqueChests). Têm versão
    /// leve (HarvestNodeLODActor), identificada pelo LiteData (LD_World_Buried_UniqueChest_IceCart).
    /// </summary>
    public const string UniqueChestCategory = "uniquechest";

    /// <summary>Corpo (LootBag) com a Tirolesa no inventário (controlado por EspZipR). Some quando a Tirolesa é retirada.</summary>
    public const string ZipCategory = "zipr";

    private static bool IsUniqueChestName(string s)
    {
        string n = ObjectReader.Normalize(s);
        return n.Contains("uniquechest", StringComparison.Ordinal) || n.Contains("uniqueominentchest", StringComparison.Ordinal);
    }

    /// <summary>
    /// "BP_Key_OminentLab_C" / "KeyOminentLab" / "Keycard…" → é chave? Separa por "_" e por maiúsculas; exige a palavra
    /// key/keys/keycard e descarta "KeyLime" (sementes e árvores de lima).
    /// </summary>
    private static bool IsKeyName(string s)
    {
        var words = new List<string>();
        var sb = new System.Text.StringBuilder();
        void Flush() { if (sb.Length > 0) words.Add(sb.ToString().ToLowerInvariant()); sb.Clear(); }
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (!char.IsLetterOrDigit(ch)) { Flush(); continue; }
            if (i > 0 && char.IsUpper(ch) && char.IsLower(s[i - 1])) Flush();
            sb.Append(ch);
        }

        Flush();
        return words.Any(w => w is "key" or "keys" or "keycard") && !words.Contains("lime");
    }

    /// <summary>"TechChip_Ominent_Super_Stolen" / "BP_SuperTechChip_Ominent_Stolen_C" → é Tech Chip?</summary>
    private static bool IsTechChipName(string s) => ObjectReader.Normalize(s).Contains("techchip", StringComparison.Ordinal);

    /// <summary>"RecipeFistsNorthern" / "BP_Recipe_Statue_Weevil_C" → é receita?</summary>
    private static bool IsRecipeName(string s) =>
        s.StartsWith("Recipe", StringComparison.OrdinalIgnoreCase) ||
        s.Split('_').Any(part => part.Equals("Recipe", StringComparison.OrdinalIgnoreCase));

    /// <summary>"Taming_EggAnt" / "HatchingEgg_X" / "BP_World_Hatchable_Ant_Red_Egg_C" → é ovo de chocar/domar?</summary>
    private static bool IsEggName(string s)
    {
        string n = ObjectReader.Normalize(s);
        return n.Contains("tamingegg", StringComparison.Ordinal) || n.Contains("hatchingegg", StringComparison.Ordinal) ||
               n.Contains("worldhatchable", StringComparison.Ordinal);
    }
    private string _filtersKey = "";
    // Cache negativo: ator → instante (Stopwatch ticks) em que deve ser reclassificado.
    // Cada entrada expira num tempo aleatório entre 0,5× e 1,5× FullRevalidateIntervalMs, para que a
    // revalidação se espalhe pelos scans em vez de acontecer toda de uma vez (evita engasgos).
    // Cada entrada guarda também a posição (nível + índice) em que o ator estava: se o mesmo endereço aparecer
    // noutra posição, é um ator novo num endereço reaproveitado e é reclassificado na hora.
    private readonly Dictionary<ulong, NegEntry> _nonCreatures = new(32768);

    private readonly struct NegEntry
    {
        public readonly long Expires;
        public readonly ulong Level;
        public readonly int Index;

        public NegEntry(long expires, ulong level, int index)
        {
            Expires = expires;
            Level = level;
            Index = index;
        }
    }

    /// <summary>Tentativas extras de achar pontos fracos (os componentes podem não existir logo no spawn).</summary>
    private const int WeakSpotRetries = 5;
    private const double WeakSpotRetryMs = 2000;
    private int _slotReuses;
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

    /// <summary>A lista de versões leves (LODActorManagerComponent.LODActors) foi lida neste scan.</summary>
    public bool LodActive { get; private set; }
    public int LodActorCount { get; private set; }
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
        _weaknessCache.Clear();
        _creatures.Clear();
        _collectibles.Clear();
        _filterVersion++;
        _nonCreatures.Clear();
        _lodNeg.Clear();
        _lodCandidateClass.Clear();
        _lodClassCount.Clear();
        _lodDiagDone = false;
        _lodLayoutValidated = false;
        _lodLayoutBroken = false;
        _proxyItemsOff = -1;
        _lodLiteDataOff = -1;
        _lodManager = 0;
        LodActive = false;
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
                if (kv.Value.Expires < now) _toRemove.Add(kv.Key);
            foreach (var k in _toRemove) _nonCreatures.Remove(k);
            _toRemove.Clear();
            foreach (var kv in _lodNeg)
                if (kv.Value < now) _toRemove.Add(kv.Key);
            foreach (var k in _toRemove) _lodNeg.Remove(k);
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

            for (int index = 0; index < span.Length; index++)
            {
                ulong actor = span[index];
                if (!ProcessMemory.IsValidObjectPointer(actor) || actor == _world.LocalPawn) continue;
                if (_nonCreatures.TryGetValue(actor, out var neg))
                {
                    if (neg.Level == level && neg.Index == index)
                    {
                        if (neg.Expires > scanStart) continue;
                    }
                    else
                    {
                        // Mesmo endereço em outra posição: o ator antigo foi destruído e outro nasceu no lugar.
                        _nonCreatures.Remove(actor);
                        _slotReuses++;
                    }
                }

                if (_creatures.TryGetValue(actor, out var existing))
                {
                    // Garante que o endereço não foi reaproveitado por outro objeto (mesma posição e mesma classe).
                    if (existing.ScanLevel == level && existing.ScanIndex == index && _obj.GetClass(actor) == existing.ClassPtr)
                    {
                        _seen.Add(actor);
                        if (existing.WeakSpots.Length == 0 && existing.WeakSpotRetriesLeft > 0 &&
                            Stopwatch.GetElapsedTime(existing.LastWeakSpotSearch, scanStart).TotalMilliseconds >= WeakSpotRetryMs)
                        {
                            existing.WeakSpotRetriesLeft--;
                            FindWeakSpots(existing, existing.Class);
                        }

                        if (existing.WeaknessPending && existing.WeakSpotRetriesLeft >= 0 &&
                            Stopwatch.GetElapsedTime(existing.LastWeaknessRead, scanStart).TotalMilliseconds >= WeakSpotRetryMs)
                            UpdateWeakness(existing, existing.Class);

                        continue;
                    }

                    _creatures.Remove(actor);
                }

                if (_collectibles.TryGetValue(actor, out var existingCol))
                {
                    if ((_cfg.AnyCollectibleEsp) &&
                        existingCol.ScanLevel == level && existingCol.ScanIndex == index && _obj.GetClass(actor) == existingCol.ClassPtr)
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
                    var col = (_cfg.AnyCollectibleEsp) && ci.IsActor ? TryBuildCollectible(actor, cls, ci) : null;
                    if (col != null)
                    {
                        col.ScanLevel = level;
                        col.ScanIndex = index;
                        _collectibles[actor] = col;
                        _seen.Add(actor);
                        addedCollectibles++;
                    }
                    else
                    {
                        _nonCreatures[actor] = new NegEntry(scanStart + NextExpiry(), level, index);
                    }

                    continue;
                }

                var c = Build(actor, cls, ci);
                if (c == null) continue;
                c.ScanLevel = level;
                c.ScanIndex = index;
                _creatures[actor] = c;
                _seen.Add(actor);
                added++;
            }
        }

        ActorCount = actorTotal;
        int addedLod = ScanLod(scanStart, ref deferred);
        addedCollectibles += addedLod;
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
                             $"versões leves={(LodActive ? LodActorCount.ToString() : "-")}, " +
                             $"cache negativo={_nonCreatures.Count}, classes={_obj.CachedClasses}, nomes={_names.CacheSize}, scan={LastScanMs:F1} ms; " +
                             $"autocorreção: classes trocadas={_obj.StaleClassesDetected}, cadeias incompletas={_obj.IncompleteChains}, endereços reaproveitados={_slotReuses}.");
    }

    // Chave = ClassInfo (recriado quando o endereço da classe é reaproveitado), não o ponteiro.
    private readonly Dictionary<ClassInfo, Weaknesses.Result> _weaknessCache = new();

    /// <summary>
    /// Fraquezas/resistências por espécie. Os efeitos de status permanentes podem ainda não existir logo no spawn:
    /// enquanto nada for encontrado, tenta de novo (até 5 vezes por criatura) e só então guarda no cache da classe.
    /// </summary>
    private void UpdateWeakness(Creature c, ClassInfo ci)
    {
        c.LastWeaknessRead = Stopwatch.GetTimestamp();
        if (_weaknessCache.TryGetValue(ci, out var cached))
        {
            c.Weakness = cached.Weak;
            c.Resistance = cached.Resist;
            c.WeaknessPending = false;
            return;
        }

        var wk = Weaknesses.Read(_mem, _names, c.Actor, c.HealthComponent, ci.Name);
        c.Weakness = wk.Weak;
        c.Resistance = wk.Resist;
        c.WeaknessTries++;
        bool found = wk.Weak.Length > 0 || wk.Resist.Length > 0;
        c.WeaknessPending = !found && c.WeaknessTries < 5;
        if (found || !c.WeaknessPending)
        {
            if (_weaknessCache.Count > 1024) _weaknessCache.Clear();
            _weaknessCache[ci] = wk;
        }
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

        UpdateWeakness(c, ci);

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
        string key = string.Join("|", _cfg.CollectibleFilters) + "#" + string.Join("|", _cfg.CollectibleItemTokens) +
                     $"#{_cfg.EspCollectibles}{_cfg.EspArrows}{_cfg.EspDroplets}{_cfg.EspRawScience}{_cfg.EspEggs}{_cfg.EspOrcTransmitters}{_cfg.EspBossArenas}{_cfg.EspRecipes}{_cfg.EspTechChips}{_cfg.EspKeys}{_cfg.EspUniqueChests}{_cfg.EspZipR}";
        if (key == _filtersKey) return;
        _filtersKey = key;
        _filters = _cfg.CollectibleFilters.Select(ObjectReader.Normalize).Where(f => f.Length > 0).Distinct().ToArray();
        _itemTokens = _cfg.CollectibleItemTokens.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _filterVersion++;
        _nonCreatures.Clear();
        _lodNeg.Clear();
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
        if (_filters.Length == 0 && _itemTokens.Length == 0 && !_cfg.EspArrows && !_cfg.EspDroplets && !_cfg.EspRawScience && !_cfg.EspEggs && !_cfg.EspOrcTransmitters && !_cfg.EspBossArenas && !_cfg.EspRecipes && !_cfg.EspTechChips && !_cfg.EspKeys && !_cfg.EspUniqueChests && !_cfg.EspZipR) return null;

        // Corpo com a Tirolesa no inventário: decidido por ator (o inventário), não pela classe.
        if (_cfg.EspZipR && Array.IndexOf(ci.Chain, "LootBag") >= 0)
        {
            string zip = FirstZipRow(InventoryItems(actor));
            if (zip.Length > 0)
            {
                var zc = new Collectible
                {
                    Actor = actor,
                    ClassPtr = cls,
                    Class = ci,
                    Filter = ZipCategory,
                    MatchedBy = "inventário",
                    ColorHex = ColorFor(ZipCategory),
                    RootComponent = _mem.ReadPtr(actor + (ulong)Off.Actor_RootComponent),
                    IsLootBody = true,
                    CollectedLabel = "sem Tirolesa",
                    Name = ZipBodyName,
                };
                if (!ProcessMemory.IsValidObjectPointer(zc.RootComponent)) return null;
                Logger.Info(Cat, $"Novo coletável: '{zc.Name}' [{ci.Name}] item='{zip}' actor=0x{actor:X}");
                return zc;
            }
        }

        string? filter = null, matchedBy = null, rowName = "";
        string? classFilter = ci.CollectibleMatch;
        if (ci.MatchVersion != _filterVersion)
        {
            classFilter = null;
            foreach (var n in ci.Chain)
            {
                if (n == "Actor") break;
                classFilter = MatchFilter(ObjectReader.Normalize(n));
                if (classFilter != null) break;
            }

            // Transmissor ORC: BP_Burgle_Quest_Flag_C → BP_Quest_Flag_C → ScanSwitch (o BP_Quest_Flag_C sozinho é o construível).
            if (Array.IndexOf(ci.Chain, "BP_Burgle_Quest_Flag_C") >= 0)
                classFilter = OrcTransmitterCategory;

            // Tech Chips: BP_SuperTechChip_Ominent_Stolen_C ► … ► BP_TechChip_Base_C (independe de CollectibleFilters);
            // corpos com chip: BP_DeadBody_ToxixAnthill_RangerTechChip_ID119_C ► … ► LootBag (chip no inventário).
            if (Array.IndexOf(ci.Chain, "BP_TechChip_Base_C") >= 0 ||
                (Array.IndexOf(ci.Chain, "LootBag") >= 0 && IsTechChipName(ci.Name)))
                classFilter = TechChipCategory;

            // Baús únicos: BP_World_Buried_UniqueChest_IceCart_C → BP_World_Buried_UniqueChest_Base_C → … → HarvestNode.
            if (Array.IndexOf(ci.Chain, "HarvestNode") >= 0 && ci.Chain.TakeWhile(n => n != "HarvestNode").Any(IsUniqueChestName))
                classFilter = UniqueChestCategory;

            // Chaves: palavra "Key" numa classe do item no chão (independe de CollectibleItemTokens).
            if (classFilter == null && ci.IsSpawnedItem &&
                ci.Chain.TakeWhile(n => n != "BP_WorldItem_C" && n != "SpawnedItem").Any(IsKeyName))
                classFilter = KeyCategory;

            // Receitas: BP_Recipe_Statue_Weevil_C → BP_WorldNote_C → BP_WorldItem_C (independe de CollectibleItemTokens).
            if (classFilter == null && ci.IsSpawnedItem &&
                ci.Chain.TakeWhile(n => n != "BP_WorldItem_C" && n != "SpawnedItem").Any(IsRecipeName))
                classFilter = RecipeCategory;

            // Arenas de chefes: BP_BossManager_Tarantula_C → BP_BossManager_C → BossManager (independe de CollectibleFilters).
            if (Array.IndexOf(ci.Chain, "BossManager") >= 0)
                classFilter = BossCategory;

            // Raw Science: BP_RawScience_A_C / _B_C / _DefenseReward_* → ScienceCollectable → PlaceableActor.
            if (Array.IndexOf(ci.Chain, "ScienceCollectable") >= 0)
                classFilter = RawScienceCategory;

            // Ovos de chocar/domar: BP_World_Hatchable_Ant_Red_Egg_C → BP_World_Hatchable_C → BP_WorldItem_C.
            if (classFilter == null && ci.IsSpawnedItem)
            {
                foreach (var n in ci.Chain)
                {
                    if (n == "BP_WorldItem_C" || n == "SpawnedItem") break;
                    if (IsEggName(n))
                    {
                        classFilter = EggCategory;
                        break;
                    }
                }
            }

            // Gotas no chão: classe com "Droplet" (só itens; exclui BP_DropletInstanceGroup_C) e sem "Dirty" na cadeia.
            if (ci.IsSpawnedItem && classFilter == null &&
                ci.Name.Contains("Droplet", StringComparison.OrdinalIgnoreCase) &&
                !ci.Chain.Any(n => n.Contains("Dirty", StringComparison.OrdinalIgnoreCase)))
            {
                classFilter = DropletCategory;
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

            ci.CollectibleMatch = classFilter;
            ci.MatchVersion = _filterVersion;
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
            if (filter == null && IsEggName(rowName)) filter = EggCategory;
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
                    if (filter == null && IsEggName(itemRow)) filter = EggCategory;
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
        if (filter == OrcTransmitterCategory)
        {
            c.Name = "Transmissor ORC";
            c.CollectedLabel = "ativado";
        }
        else if (filter == TechChipCategory && Array.IndexOf(ci.Chain, "LootBag") >= 0)
        {
            c.IsLootBody = true;
            c.CollectedLabel = "vazio";
            string chip = FirstTechChipRow(InventoryItems(actor));
            c.Name = BodyName(chip);
        }
        else if (filter == RecipeCategory)
        {
            c.Name = Collectible.RecipeName(rowName.Length > 0 ? rowName : ci.Name);
        }
        else if (filter == EggCategory)
        {
            c.Name = Collectible.EggName(rowName.Length > 0 ? rowName : ci.Name);
        }
        else if (filter == RawScienceCategory)
        {
            c.ScienceAmount = ReadScienceAmount(actor);
            c.Name = c.ScienceAmount > 0 ? $"Raw Science (+{c.ScienceAmount})" : "Raw Science";
        }
        else if (filter == DropletCategory)
        {
            // Segurança extra: o RowName do item também não pode conter "Dirty".
            if (rowName.Contains("Dirty", StringComparison.OrdinalIgnoreCase)) return null;
            c.Name = Collectible.DropletName(ci.Name);
        }
        else if (Array.IndexOf(ci.Chain, "BossManager") >= 0)
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

    /// <summary>ScienceCollectable.ScienceAmount do ator; se vier 0, o valor padrão em LiteData.</summary>
    private int ReadScienceAmount(ulong actor)
    {
        static bool Plausible(int v) => v > 0 && v < 1_000_000;
        if (_mem.TryRead(actor + (ulong)Off.ScienceCollectable_ScienceAmount, out int amount) && Plausible(amount)) return amount;
        ulong lite = _mem.ReadPtr(actor + (ulong)Off.ScienceCollectable_LiteData);
        if (ProcessMemory.IsValidObjectPointer(lite) &&
            _mem.TryRead(lite + (ulong)Off.ScienceLiteData_ScienceAmount, out int liteAmount) && Plausible(liteAmount))
            return liteAmount;
        return 0;
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

        c.LastWeakSpotSearch = Stopwatch.GetTimestamp();
        if (found.Count == 0 && c.WeakSpots.Length == 0 && c.WeakSpotRetriesLeft < WeakSpotRetries) return; // nova tentativa sem nada novo
        c.WeakSpots = found.ToArray();
        c.WeakSpotNames = names.ToArray();
        c.WeakSpotPos = new FVector[found.Count];
        c.WeakSpotValid = new bool[found.Count];
        if (found.Count > 0 && c.WeakSpotRetriesLeft < WeakSpotRetries)
            Logger.Debug(Cat, $"Pontos fracos de {ci.Name} encontrados numa nova tentativa: {string.Join(", ", names)}");

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

    // ================================================================== VERSÃO LEVE (LOD)

    // Cache negativo das versões leves: objeto → instante em que deve ser reclassificado.
    private readonly Dictionary<ulong, long> _lodNeg = new(65536);
    private ulong[] _lodBuffer = new ulong[16384];
    private ulong _lodManager;

    /// <summary>Classificações de versões leves por scan (a lista tem dezenas de milhares; espalha o custo inicial).</summary>
    private const int LodClassifyBudgetPerScan = 15000;
    private bool _lodWarned;
    private bool _lodLocValidated;
    private readonly Dictionary<int, int> _lodLocVotes = new();
    private readonly Dictionary<ClassInfo, bool> _lodCandidateClass = new();
    private readonly Dictionary<string, int> _lodClassCount = new();
    private bool _lodDiagDone;
    private bool _lodLayoutValidated;
    private bool _lodLayoutBroken;

    /// <summary>
    /// Ovos pela versão leve: UWorld.GameState → LODActorManagerComponent → LODActors[] (UBaseLODActor*).
    /// A versão leve existe a qualquer distância; a completa (ator em Level.Actors) só perto do jogador.
    /// Ovo = SpawnedItemLODActor cujo item (ProxySpawnedItemActor.Item ou o da versão completa) é Taming/Hatching Egg,
    /// ou que tenha um EggLODComponent.
    /// </summary>
    private int ScanLod(long scanStart, ref int deferred)
    {
        int budget = LodClassifyBudgetPerScan;
        LodActive = false;
        if ((!_cfg.EspEggs && !_cfg.EspRecipes && !_cfg.EspTechChips && !_cfg.EspKeys && !_cfg.EspUniqueChests && !_cfg.EspZipR) || _world.World == 0) return 0;

        ulong gs = _mem.ReadPtr(_world.World + (ulong)Off.World_GameState);
        ulong mgr = ProcessMemory.IsValidObjectPointer(gs) ? _mem.ReadPtr(gs + (ulong)Off.SurvivalGameState_LODActorManagerComponent) : 0;
        if (!ProcessMemory.IsValidObjectPointer(mgr) ||
            !_mem.TryRead(mgr + (ulong)Off.LODActorManagerComponent_LODActors, out TArrayHeader arr) || arr.Num <= 0 || !arr.IsPlausible(4_000_000))
        {
            if (!_lodWarned && _initialScanDone)
            {
                _lodWarned = true;
                Logger.Warn(Cat, $"Versões leves indisponíveis (GameState=0x{gs:X}, LODActorManager=0x{mgr:X}); ovos usam a versão completa.");
            }

            return 0;
        }

        if (mgr != _lodManager)
        {
            _lodManager = mgr;
            _lodNeg.Clear();
            Logger.Info(Cat, $"LODActorManagerComponent: 0x{mgr:X} ({arr.Num} versões leves).");
        }

        if (_lodBuffer.Length < arr.Num) _lodBuffer = new ulong[Math.Max(arr.Num, _lodBuffer.Length * 2)];
        var span = _lodBuffer.AsSpan(0, arr.Num);
        if (!_mem.ReadArray(arr.Data, span)) return 0;
        if (_lodLayoutBroken || (!_lodLayoutValidated && !DetectLodLayout(span))) return 0;
        if (_cfg.EspUniqueChests && _lodLiteDataOff == -1) DetectLiteDataOffset(span);
        LodActive = true;
        LodActorCount = arr.Num;

        int added = 0;
        foreach (ulong lod in span)
        {
            if (!ProcessMemory.IsValidObjectPointer(lod)) continue;
            if (_collectibles.TryGetValue(lod, out var existing))
            {
                if (existing.IsLod && _obj.GetClass(lod) == existing.ClassPtr)
                {
                    _seen.Add(lod);
                    continue;
                }

                _collectibles.Remove(lod);
            }

            if (_lodNeg.TryGetValue(lod, out long expires) && expires > scanStart) continue;
            if (budget <= 0)
            {
                deferred++;
                continue;
            }

            budget--;
            var col = TryBuildLodItem(lod);
            if (col == null)
            {
                _lodNeg[lod] = scanStart + NextExpiry();
                continue;
            }

            _lodNeg.Remove(lod);
            _collectibles[lod] = col;
            _seen.Add(lod);
            added++;
        }

        if (!_lodDiagDone && budget > 0 && !_collectibles.Values.Any(c => c.IsLod))
        {
            _lodDiagDone = true;
            try { LogLodDiagnostics(span); }
            catch (Exception ex) { Logger.Error(Cat, "Falha no diagnóstico de versões leves.", ex); }
        }

        return added;
    }

    /// <summary>
    /// Confere FullActor / ProxyActor / LODComponents do BaseLODActor nesta build: o dump diz 0xB0/0xB8/0xC0, mas no
    /// jogo os três estão 0x10 adiante (0xC0/0xC8/0xD0). Testa deslocamentos de −0x20 a +0x20 em até 400 versões leves de
    /// itens: vale o que tiver mais FullActor apontando para um AActor e ProxyActor apontando para um UProxyActor.
    /// </summary>
    private bool DetectLodLayout(ReadOnlySpan<ulong> lods)
    {
        int baseFull = Off.BaseLODActor_FullActor, baseProxy = Off.BaseLODActor_ProxyActor;
        var samples = new List<ulong>(400);
        foreach (ulong lod in lods)
        {
            if (!ProcessMemory.IsValidObjectPointer(lod)) continue;
            var ci = _obj.GetObjectClassInfo(lod);
            if (ci == null || Array.IndexOf(ci.Chain, "SpawnedItemLODActor") < 0) continue;
            samples.Add(lod);
            if (samples.Count >= 400) break;
        }

        if (samples.Count < 10) return false;
        int bestDelta = 0, bestScore = -1;
        for (int delta = -0x20; delta <= 0x20; delta += 8)
        {
            int score = 0;
            foreach (ulong lod in samples)
            {
                ulong full = _mem.ReadPtr(lod + (ulong)(baseFull + delta));
                ulong proxy = _mem.ReadPtr(lod + (ulong)(baseProxy + delta));
                if (ProcessMemory.IsValidObjectPointer(full) && _obj.GetObjectClassInfo(full)?.IsActor == true) score++;
                else if (ProcessMemory.IsValidObjectPointer(proxy) && _obj.GetObjectClassInfo(proxy)?.Chain.Contains("ProxyActor") == true) score++;
            }

            if (score > bestScore) (bestScore, bestDelta) = (score, delta);
        }

        if (bestScore < samples.Count / 2)
        {
            Logger.Warn(Cat, $"BaseLODActor: FullActor/ProxyActor não reconhecidos ({bestScore}/{samples.Count}); ovos usam a versão completa.");
            _lodLayoutValidated = true; // não repete o teste a cada scan
            _lodLayoutBroken = true;
            return false;
        }

        _lodLayoutValidated = true;
        if (bestDelta != 0)
        {
            Off.Set("BaseLODActor.FullActor", baseFull + bestDelta);
            Off.Set("BaseLODActor.ProxyActor", baseProxy + bestDelta);
            Off.Set("BaseLODActor.LODComponents", Off.BaseLODActor_LODComponents + bestDelta);
            Logger.Warn(Cat, $"BaseLODActor corrigido em runtime (deslocamento {(bestDelta > 0 ? "+" : "-")}0x{Math.Abs(bestDelta):X}): " +
                             $"FullActor=0x{Off.BaseLODActor_FullActor:X}, ProxyActor=0x{Off.BaseLODActor_ProxyActor:X}, LODComponents=0x{Off.BaseLODActor_LODComponents:X} ({bestScore}/{samples.Count}).");
        }
        else
        {
            Logger.Info(Cat, $"BaseLODActor validado: FullActor=0x{baseFull:X}, ProxyActor=0x{baseProxy:X} ({bestScore}/{samples.Count}).");
        }

        return true;
    }

    private Collectible? TryBuildLodItem(ulong lod)
    {
        ulong cls = _obj.GetClass(lod);
        var ci = _obj.GetClassInfo(cls);
        if (ci == null) return null;
        _lodClassCount[ci.Name] = _lodClassCount.GetValueOrDefault(ci.Name) + 1;
        if (!_lodCandidateClass.TryGetValue(ci, out bool candidate))
        {
            // Versões leves que nunca são itens no chão (criaturas, construções, nós de coleta…).
            candidate = !ci.Chain.Any(n => n is "CharacterLODActor" or "BuildingLODActor" or "PersistentHazardLODActor");
            _lodCandidateClass[ci] = candidate;
            Logger.Info(Cat, $"Classe de versão leve: {ci}{(candidate ? "" : " (ignorada)")}");
        }

        if (!candidate) return null;
        if (Array.IndexOf(ci.Chain, "LootBagLODActor") >= 0) return _cfg.EspTechChips || _cfg.EspZipR ? TryBuildLodBody(lod, cls, ci) : null;
        if (Array.IndexOf(ci.Chain, "HarvestNodeLODActor") >= 0) return _cfg.EspUniqueChests ? TryBuildLodChest(lod, cls, ci) : null;

        // Nome do item: versão completa (se existir) ou o UItem guardado na versão leve.
        bool Wanted(string r) => (_cfg.EspEggs && IsEggName(r)) || (_cfg.EspRecipes && IsRecipeName(r)) ||
                                 (_cfg.EspTechChips && IsTechChipName(r)) || (_cfg.EspKeys && IsKeyName(r));
        string row = "";
        ulong full = _mem.ReadPtr(lod + (ulong)Off.BaseLODActor_FullActor);
        if (ProcessMemory.IsValidObjectPointer(full))
        {
            var fci = _obj.GetObjectClassInfo(full);
            if (fci != null)
            {
                string? wantedClass = fci.Chain.TakeWhile(n => n != "BP_WorldItem_C" && n != "SpawnedItem").FirstOrDefault(Wanted);
                if (fci.IsSpawnedItem)
                {
                    row = ReadRowName(full + (ulong)Off.SpawnedItem_PlacedItemData + (ulong)Off.DataTableRowHandle_RowName);
                    if (!Wanted(row))
                    {
                        ulong it = _mem.ReadPtr(full + (ulong)Off.SpawnedItem_Item);
                        if (ProcessMemory.IsValidObjectPointer(it))
                            row = ReadRowName(it + (ulong)Off.Item_ItemDataRowHandle + (ulong)Off.DataTableRowHandle_RowName);
                    }
                }

                if (!Wanted(row) && wantedClass != null) row = wantedClass;
            }
        }

        if (!Wanted(row))
        {
            ulong proxy = _mem.ReadPtr(lod + (ulong)Off.BaseLODActor_ProxyActor);
            ulong it = FindProxyItem(proxy);
            if (it != 0) row = ReadRowName(it + (ulong)Off.Item_ItemDataRowHandle + (ulong)Off.DataTableRowHandle_RowName);
        }

        string category;
        // EggLODComponent também existe em ovos comuns de formiga (RowName "AntEgg", item de colônia/comida): só vale
        // como critério quando o nome do item não pôde ser lido.
        if (_cfg.EspEggs && (IsEggName(row) || (row.Length == 0 && HasLodComponent(lod, "EggLODComponent")))) category = EggCategory;
        else if (_cfg.EspRecipes && IsRecipeName(row)) category = RecipeCategory;
        else if (_cfg.EspTechChips && IsTechChipName(row)) category = TechChipCategory;
        else if (_cfg.EspKeys && IsKeyName(row)) category = KeyCategory;
        else return null;

        var c = new Collectible
        {
            Actor = lod,
            ClassPtr = cls,
            Class = ci,
            Filter = category,
            MatchedBy = "versão leve",
            ColorHex = ColorFor(category),
            IsLod = true,
            Name = category switch
            {
                EggCategory => Collectible.EggName(row.Length > 0 ? row : "Egg"),
                TechChipCategory => Collectible.TechChipName(row),
                KeyCategory => Collectible.Prettify(row),
                _ => Collectible.RecipeName(row),
            },
        };
        Logger.Info(Cat, $"Novo item pela versão leve ({category}): '{c.Name}' [{ci.Name}] row='{row}' lod=0x{lod:X} completa=0x{full:X}");
        return c;
    }

    /// <summary>
    /// UItem da versão leve: ProxySpawnedItemActor.Item (+0x1F0); para outras classes de proxy, o primeiro ponteiro
    /// entre +0x40 e +0x230 que aponte para um UItem.
    /// </summary>
    private ulong FindProxyItem(ulong proxy)
    {
        if (!ProcessMemory.IsValidObjectPointer(proxy)) return 0;
        var pci = _obj.GetObjectClassInfo(proxy);
        if (pci == null) return 0;
        if (Array.IndexOf(pci.Chain, "ProxySpawnedItemActor") >= 0)
        {
            ulong it = _mem.ReadPtr(proxy + (ulong)Off.ProxySpawnedItemActor_Item);
            if (IsItem(it)) return it;
        }

        Span<ulong> fields = stackalloc ulong[(0x230 - 0x40) / 8];
        if (!_mem.ReadArray(proxy + 0x40, fields)) return 0;
        foreach (ulong p in fields)
            if (IsItem(p)) return p;
        return 0;
    }

    private bool IsItem(ulong p)
    {
        if (!ProcessMemory.IsValidObjectPointer(p)) return false;
        var ci = _obj.GetObjectClassInfo(p);
        return ci != null && Array.IndexOf(ci.Chain, "Item") >= 0 && Array.IndexOf(ci.Chain, "Actor") < 0;
    }

    /// <summary>
    /// Diagnóstico único (log): classes das versões leves e, para cada ovo da versão completa, qual versão leve aponta
    /// para ele (FullActor) e o que ela contém.
    /// </summary>
    private void LogLodDiagnostics(ReadOnlySpan<ulong> lods)
    {
        Logger.Info(Cat, "Versões leves por classe: " +
                         string.Join(", ", _lodClassCount.OrderByDescending(kv => kv.Value).Take(40).Select(kv => $"{kv.Key}={kv.Value}")));
        var fullEggs = _collectibles.Values.Where(c => c.Filter == EggCategory && !c.IsLod).Select(c => c.Actor).ToHashSet();
        if (fullEggs.Count == 0) return;
        var matched = new HashSet<ulong>();
        foreach (ulong lod in lods)
        {
            if (!ProcessMemory.IsValidObjectPointer(lod)) continue;
            ulong full = _mem.ReadPtr(lod + (ulong)Off.BaseLODActor_FullActor);
            if (!fullEggs.Contains(full)) continue;
            matched.Add(full);
            ulong proxy = _mem.ReadPtr(lod + (ulong)Off.BaseLODActor_ProxyActor);
            var comps = new List<string>();
            if (_mem.TryRead(lod + (ulong)Off.BaseLODActor_LODComponents, out TArrayHeader arr) && arr.Num > 0 && arr.IsPlausible(64))
            {
                var buf = new ulong[arr.Num];
                if (_mem.ReadArray(arr.Data, buf.AsSpan()))
                    comps.AddRange(buf.Select(b => _obj.GetObjectClassInfo(b)?.Name ?? $"0x{b:X}"));
            }

            _mem.TryRead(lod + (ulong)Off.BaseLODActor_Location, out FVector lp);
            Logger.Info(Cat, $"Ovo completo 0x{full:X} ← versão leve 0x{lod:X} [{_obj.GetObjectClassInfo(lod)}] proxy=0x{proxy:X} " +
                             $"[{(ProcessMemory.IsValidObjectPointer(proxy) ? _obj.GetObjectClassInfo(proxy)?.ToString() : "-")}] " +
                             $"componentes=[{string.Join(", ", comps)}] pos@0x{Off.BaseLODActor_Location:X}=({lp.X:F0}, {lp.Y:F0}, {lp.Z:F0}) " +
                             $"neg={_lodNeg.ContainsKey(lod)} ovo={_collectibles.ContainsKey(lod)}");
        }

        foreach (ulong egg in fullEggs.Where(e => !matched.Contains(e)))
            Logger.Info(Cat, $"Ovo completo 0x{egg:X}: nenhuma versão leve com FullActor apontando para ele.");

        // FullActors (+0x100): os ovos completos estão nessa lista?
        if (_mem.TryRead(_lodManager + 0x100, out TArrayHeader fa) && fa.Num > 0 && fa.IsPlausible(4_000_000))
        {
            var buf = new ulong[fa.Num];
            if (_mem.ReadArray(fa.Data, buf.AsSpan()))
                foreach (ulong egg in fullEggs)
                    Logger.Info(Cat, $"Ovo completo 0x{egg:X}: índice em LODActorManager.FullActors = {Array.IndexOf(buf, egg)} (de {fa.Num}).");
        }

        // Versões leves cuja memória contém a posição do ovo completo (FVector double a menos de 2 m) ou um ponteiro para ele.
        const int start = 0x28, end = 0x1C8;
        var bytes = new byte[end - start];
        var eggPos = _collectibles.Values.Where(c => c.Filter == EggCategory && !c.IsLod)
                                  .Select(c => (c.Actor, Pos: ReadLocation(c.RootComponent))).ToList();
        int dumped = 0, sampleSpawned = 0;
        foreach (ulong lod in lods)
        {
            if (!ProcessMemory.IsValidObjectPointer(lod) || !_mem.ReadBytes(lod + start, bytes)) continue;
            bool isSpawned = _obj.GetObjectClassInfo(lod)?.Name == "SpawnedItemLODActor";
            string? hit = null;
            for (int off = 0; off + 24 <= bytes.Length && hit == null; off += 8)
            {
                ulong q = BitConverter.ToUInt64(bytes, off);
                foreach (var e in eggPos)
                {
                    if (q == e.Actor) { hit = $"ponteiro para o ovo 0x{e.Actor:X} em +0x{off + start:X}"; break; }
                    var v = new FVector(BitConverter.ToDouble(bytes, off), BitConverter.ToDouble(bytes, off + 8), BitConverter.ToDouble(bytes, off + 16));
                    if (PlausiblePos(v) && v.DistanceTo(e.Pos) < 200) { hit = $"posição do ovo 0x{e.Actor:X} em +0x{off + start:X} (dist {v.DistanceTo(e.Pos):F0} cm)"; break; }
                }
            }

            bool sample = hit == null && isSpawned && sampleSpawned < 3;
            if (hit == null && !sample) continue;
            if (sample) sampleSpawned++;
            if (hit != null && ++dumped > 12) continue;
            Logger.Info(Cat, $"Versão leve 0x{lod:X} [{_obj.GetObjectClassInfo(lod)}] {(hit ?? "(amostra de SpawnedItemLODActor)")}:");
            for (int off = 0; off + 8 <= bytes.Length; off += 8)
            {
                string d = DescribeQword(BitConverter.ToUInt64(bytes, off), bytes, off);
                if (d.Length > 0) Logger.Info(Cat, $"    +0x{off + start:X3}: {d}");
            }
        }

        Logger.Info(Cat, "===== fim do diagnóstico de versões leves =====");
    }

    /// <summary>Interpreta 8 bytes: ponteiro para UObject (classe e nome), FName, ou double/float plausível.</summary>
    private string DescribeQword(ulong q, byte[] bytes, int off)
    {
        if (q == 0) return "";
        if (ProcessMemory.IsValidObjectPointer(q))
        {
            var ci = _obj.GetObjectClassInfo(q);
            if (ci != null) return $"0x{q:X} → {ci.Name} '{_names.GetObjectName(q)}'";
            return $"0x{q:X} (ponteiro)";
        }

        var parts = new List<string> { $"0x{q:X16}" };
        int idx = (int)(q & 0xFFFFFFFF), num = (int)(q >> 32);
        if (idx > 0 && num >= 0 && num < 100000)
        try
        {
            string n = _names.GetName(new FName { ComparisonIndex = idx, Number = num });
            if (n.Length > 1 && n != "None" && n.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or ' ' or '-' or '/' or '.')) parts.Add($"FName '{n}'");
        }
        catch
        {
            // índice fora do pool de nomes
        }

        double dv = BitConverter.ToDouble(bytes, off);
        if (double.IsFinite(dv) && Math.Abs(dv) > 1e-3 && Math.Abs(dv) < 1e8) parts.Add($"double {dv:F2}");
        float f0 = BitConverter.ToSingle(bytes, off), f1 = BitConverter.ToSingle(bytes, off + 4);
        if (float.IsFinite(f0) && float.IsFinite(f1) && (Math.Abs(f0) is > 1e-3f and < 1e7f || Math.Abs(f1) is > 1e-3f and < 1e7f))
            parts.Add($"floats {f0:G5} / {f1:G5}");
        return string.Join("  ", parts);
    }

    // ------------------------------------------------------------------ baús únicos (HarvestNode) pela versão leve

    // -1 = ainda não procurado; -2 = não encontrado.
    private int _lodLiteDataOff = -1;

    /// <summary>
    /// Offset do ponteiro UActorLiteData* dentro do BaseLODActor (não refletido): em até 64 versões leves de HarvestNode,
    /// procura entre 0x28 e 0x298 um ponteiro para um objeto de classe *LiteData; vale o offset mais votado.
    /// </summary>
    private void DetectLiteDataOffset(ReadOnlySpan<ulong> lods)
    {
        var votes = new Dictionary<int, int>();
        var buf = new byte[0x298 - 0x28 + 8];
        int samples = 0;
        foreach (ulong lod in lods)
        {
            if (samples >= 64) break;
            if (!ProcessMemory.IsValidObjectPointer(lod)) continue;
            var ci = _obj.GetObjectClassInfo(lod);
            if (ci == null || Array.IndexOf(ci.Chain, "HarvestNodeLODActor") < 0 || !_mem.ReadBytes(lod + 0x28, buf)) continue;
            samples++;
            for (int off = 0; off + 8 <= buf.Length; off += 8)
            {
                ulong p = BitConverter.ToUInt64(buf, off);
                if (!ProcessMemory.IsValidObjectPointer(p)) continue;
                var pci = _obj.GetObjectClassInfo(p);
                if (pci != null && pci.Name.EndsWith("LiteData", StringComparison.Ordinal))
                {
                    votes[off + 0x28] = votes.GetValueOrDefault(off + 0x28) + 1;
                    break;
                }
            }
        }

        if (samples == 0) return; // tenta no próximo scan
        var best = votes.OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (best.Value < samples / 2)
        {
            _lodLiteDataOff = -2;
            Logger.Warn(Cat, $"BaseLODActor.LiteData não encontrado ({best.Value}/{samples}); baús únicos usam a versão completa.");
            return;
        }

        _lodLiteDataOff = best.Key;
        if (best.Key != Off.BaseLODActor_LiteData) Off.Set("BaseLODActor.LiteData", best.Key);
        Logger.Info(Cat, $"BaseLODActor.LiteData encontrado em 0x{best.Key:X} ({best.Value}/{samples}).");
    }

    /// <summary>Baú único pela versão leve: o LiteData se chama LD_World_Buried_UniqueChest_&lt;nome&gt;.</summary>
    private Collectible? TryBuildLodChest(ulong lod, ulong cls, ClassInfo ci)
    {
        if (_lodLiteDataOff < 0) return null;
        ulong lite = _mem.ReadPtr(lod + (ulong)_lodLiteDataOff);
        if (!ProcessMemory.IsValidObjectPointer(lite)) return null;
        string liteName = _names.GetObjectName(lite);
        if (!IsUniqueChestName(liteName) || liteName.EndsWith("_Base", StringComparison.Ordinal)) return null;

        var c = new Collectible
        {
            Actor = lod,
            ClassPtr = cls,
            Class = ci,
            Filter = UniqueChestCategory,
            MatchedBy = "versão leve",
            ColorHex = ColorFor(UniqueChestCategory),
            IsLod = true,
            Name = Collectible.UniqueChestName(liteName),
        };
        Logger.Info(Cat, $"Novo item pela versão leve (uniquechest): '{c.Name}' LiteData='{liteName}' lod=0x{lod:X}");
        return c;
    }

    // ------------------------------------------------------------------ corpos (LootBag) com Tech Chip

    private static string BodyName(string chipRow) =>
        chipRow.Length > 0 ? $"{Collectible.TechChipName(chipRow)} (corpo)" : "Tech Chip (corpo)";

    private string FirstTechChipRow(List<ulong>? items)
    {
        if (items == null) return "";
        foreach (ulong it in items)
        {
            string row = ReadRowName(it + (ulong)Off.Item_ItemDataRowHandle + (ulong)Off.DataTableRowHandle_RowName);
            if (IsTechChipName(row)) return row;
        }

        return "";
    }

    /// <summary>Itens do LootBag completo (LootBag.InventoryComponent → Items); null = ilegível.</summary>
    private List<ulong>? InventoryItems(ulong actor)
    {
        ulong inv = _mem.ReadPtr(actor + (ulong)Off.LootBag_InventoryComponent);
        if (!ProcessMemory.IsValidObjectPointer(inv)) return null;
        return ReadItemArray(inv + (ulong)Off.InventoryComponent_Items, requireItems: false);
    }

    private List<ulong>? ReadItemArray(ulong address, bool requireItems)
    {
        if (!_mem.TryRead(address, out TArrayHeader arr) || !arr.IsPlausible(512)) return null;
        var list = new List<ulong>(arr.Num);
        if (arr.Num == 0) return list;
        var buf = new ulong[arr.Num];
        if (!_mem.ReadArray(arr.Data, buf.AsSpan())) return null;
        foreach (ulong p in buf)
            if (ProcessMemory.IsValidObjectPointer(p)) list.Add(p);
        if (requireItems && (list.Count == 0 || !IsItem(list[0]))) return null;
        return list;
    }

    // Offset de ProxyLootBagActor.Items conferido em runtime (o dump errou os offsets de outras classes UObject do LOD).
    private int _proxyItemsOff = -1;

    /// <summary>Itens do corpo na versão leve (ProxyLootBagActor.Items); null = ilegível ou offset ainda não conferido.</summary>
    private List<ulong>? ProxyItems(ulong proxy)
    {
        if (!ProcessMemory.IsValidObjectPointer(proxy)) return null;
        if (_proxyItemsOff >= 0) return ReadItemArray(proxy + (ulong)_proxyItemsOff, requireItems: false);

        // Procura a TArray<UItem*>: primeiro o offset do dump e ±0x10, depois de 0x40 a 0x228.
        int def = Off.ProxyLootBagActor_Items;
        var order = new List<int> { def, def + 0x10, def - 0x10 };
        for (int o = 0x40; o <= 0x228; o += 8) if (!order.Contains(o)) order.Add(o);
        foreach (int o in order)
        {
            var items = ReadItemArray(proxy + (ulong)o, requireItems: true);
            if (items == null) continue;
            _proxyItemsOff = o;
            Logger.Info(Cat, $"ProxyLootBagActor.Items encontrado em 0x{o:X} (dump: 0x{def:X}).");
            return items;
        }

        return null;
    }

    /// <summary>
    /// Corpo na versão leve. Tirolesa: o inventário (da versão completa, se existir, ou do proxy) tem um item "Zip…".
    /// Tech Chip: a versão completa é um corpo "TechChip" ou o inventário tem um Tech Chip.
    /// </summary>
    private Collectible? TryBuildLodBody(ulong lod, ulong cls, ClassInfo ci)
    {
        ulong full = _mem.ReadPtr(lod + (ulong)Off.BaseLODActor_FullActor);
        ClassInfo? fci = null;
        List<ulong>? items;
        if (ProcessMemory.IsValidObjectPointer(full))
        {
            fci = _obj.GetObjectClassInfo(full);
            if (fci == null || Array.IndexOf(fci.Chain, "LootBag") < 0) return null;
            items = InventoryItems(full);
        }
        else
        {
            items = ProxyItems(_mem.ReadPtr(lod + (ulong)Off.BaseLODActor_ProxyActor));
        }

        string category, row;
        if (_cfg.EspZipR && (row = FirstZipRow(items)).Length > 0)
        {
            category = ZipCategory;
        }
        else if (_cfg.EspTechChips && ((row = FirstTechChipRow(items)).Length > 0 || (fci != null && IsTechChipName(fci.Name))))
        {
            category = TechChipCategory;
        }
        else
        {
            return null; // sem Tirolesa / chip (ou já saqueado)
        }

        var c = new Collectible
        {
            Actor = lod,
            ClassPtr = cls,
            Class = ci,
            Filter = category,
            MatchedBy = "versão leve",
            ColorHex = ColorFor(category),
            IsLod = true,
            IsLootBody = true,
            CollectedLabel = category == ZipCategory ? "sem Tirolesa" : "vazio",
            Name = category == ZipCategory ? ZipBodyName : BodyName(row),
        };
        Logger.Info(Cat, $"Novo item pela versão leve ({category}, corpo): '{c.Name}' row='{row}' lod=0x{lod:X} completa=0x{full:X}");
        return c;
    }

    private const string ZipBodyName = "Corpo com Tirolesa";

    /// <summary>
    /// Tirolesa (ferramenta; "Tirolesa" é o nome em português): RowName interno em inglês com "zip" (ex.: "Zipline…")
    /// ou "tirolesa". O nome exato fica registrado no log quando o corpo é encontrado.
    /// </summary>
    private static bool IsZipName(string s)
    {
        string n = ObjectReader.Normalize(s);
        return n.Contains("zip", StringComparison.Ordinal) || n.Contains("tirolesa", StringComparison.Ordinal);
    }

    private string FirstZipRow(List<ulong>? items)
    {
        if (items == null) return "";
        foreach (ulong it in items)
        {
            string row = ReadRowName(it + (ulong)Off.Item_ItemDataRowHandle + (ulong)Off.DataTableRowHandle_RowName);
            if (IsZipName(row)) return row;
        }

        return "";
    }

    /// <summary>
    /// Corpo já saqueado? Tech Chip: inventário vazio. Tirolesa: ela saiu do inventário. Versão leve: lê da versão
    /// completa se existir, senão do proxy (ilegível = ainda não saqueado).
    /// </summary>
    private bool LootBodyDone(Collectible c)
    {
        List<ulong>? items;
        if (!c.IsLod) items = InventoryItems(c.Actor);
        else if (ProcessMemory.IsValidObjectPointer(c.LodFullActor)) items = InventoryItems(c.LodFullActor);
        else items = ProxyItems(_mem.ReadPtr(c.Actor + (ulong)Off.BaseLODActor_ProxyActor));
        if (items == null) return false;
        return c.Filter == ZipCategory ? FirstZipRow(items).Length == 0 : items.Count == 0;
    }

    private bool HasLodComponent(ulong lod, string className)
    {
        if (!_mem.TryRead(lod + (ulong)Off.BaseLODActor_LODComponents, out TArrayHeader arr) || arr.Num <= 0 || !arr.IsPlausible(64)) return false;
        Span<ulong> comps = stackalloc ulong[arr.Num];
        if (!_mem.ReadArray(arr.Data, comps)) return false;
        foreach (ulong comp in comps)
        {
            if (!ProcessMemory.IsValidObjectPointer(comp)) continue;
            var cci = _obj.GetObjectClassInfo(comp);
            if (cci != null && Array.IndexOf(cci.Chain, className) >= 0) return true;
        }

        return false;
    }

    private static bool PlausiblePos(FVector p) =>
        p.IsFinite && !p.IsZero && Math.Abs(p.X) < 1e8 && Math.Abs(p.Y) < 1e8 && Math.Abs(p.Z) < 1e8;

    /// <summary>Posição da versão leve: a da versão completa se ela existir, senão o FVector guardado no BaseLODActor.</summary>
    private bool ReadLodLocation(Collectible c, out FVector loc)
    {
        ulong full = c.LodFullActor;
        if (ProcessMemory.IsValidObjectPointer(full))
        {
            ulong root = _mem.ReadPtr(full + (ulong)Off.Actor_RootComponent);
            if (ProcessMemory.IsValidObjectPointer(root))
            {
                loc = ReadLocation(root);
                if (PlausiblePos(loc))
                {
                    if (!_lodLocValidated) ValidateLodLocation(c.Actor, loc);
                    return true;
                }
            }
        }

        return _mem.TryRead(c.Actor + (ulong)Off.BaseLODActor_Location, out loc) && PlausiblePos(loc);
    }

    /// <summary>
    /// Confere em runtime onde fica a posição dentro do BaseLODActor (não refletida): procura entre 0xF0 e 0x1C0 um
    /// FVector igual (até 1 m) à posição da versão completa. Três confirmações no mesmo offset bastam.
    /// </summary>
    private void ValidateLodLocation(ulong lod, FVector fullLoc)
    {
        const int start = 0xF0, end = 0x1C0;
        Span<byte> buf = stackalloc byte[end - start + 24];
        if (!_mem.ReadBytes(lod + start, buf)) return;
        int found = -1;
        for (int off = start; off <= end; off += 8)
        {
            var span = buf.Slice(off - start, 24);
            var v = new FVector(BitConverter.ToDouble(span), BitConverter.ToDouble(span[8..]), BitConverter.ToDouble(span[16..]));
            if (PlausiblePos(v) && v.DistanceTo(fullLoc) < 100)
            {
                found = off;
                break;
            }
        }

        if (found < 0) return;
        _lodLocVotes[found] = _lodLocVotes.GetValueOrDefault(found) + 1;
        if (_lodLocVotes[found] < 3) return;
        _lodLocValidated = true;
        if (found != Off.BaseLODActor_Location)
        {
            Logger.Warn(Cat, $"BaseLODActor.Location corrigido em runtime: 0x{Off.BaseLODActor_Location:X} → 0x{found:X}.");
            Off.Set("BaseLODActor.Location", found);
        }
        else
        {
            Logger.Info(Cat, $"BaseLODActor.Location validado em 0x{found:X}.");
        }
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

    private readonly HashSet<ulong> _lodFullActors = new();

    private void UpdateCollectibles(Camera cam)
    {
        int visible = 0;
        double maxDist = _cfg.CollectibleMaxDistanceMeters;
        double eggDist = _cfg.EggMaxDistanceMeters;

        // Versões completas que já têm a versão leve na lista: não são desenhadas (evita caixa duplicada).
        _lodFullActors.Clear();
        foreach (var c in _collectibles.Values)
        {
            if (!c.IsLod) continue;
            c.LodFullActor = _mem.ReadPtr(c.Actor + (ulong)Off.BaseLODActor_FullActor);
            if (ProcessMemory.IsValidObjectPointer(c.LodFullActor)) _lodFullActors.Add(c.LodFullActor);
        }

        foreach (var c in _collectibles.Values)
        {
            c.Visible = false;
            bool enabled = c.Filter switch
            {
                ArrowCategory => _cfg.EspArrows,
                DropletCategory => _cfg.EspDroplets,
                RawScienceCategory => _cfg.EspRawScience,
                EggCategory => _cfg.EspEggs,
                OrcTransmitterCategory => _cfg.EspOrcTransmitters,
                BossCategory => _cfg.EspBossArenas,
                RecipeCategory => _cfg.EspRecipes,
                TechChipCategory => _cfg.EspTechChips,
                KeyCategory => _cfg.EspKeys,
                UniqueChestCategory => _cfg.EspUniqueChests,
                ZipCategory => _cfg.EspZipR,
                _ => _cfg.EspCollectibles,
            };
            if (!enabled) continue;

            // Itens com versão leve (ovos, receitas, Tech Chips, chaves, baús únicos): desenhados por ela; a completa só é desenhada se nenhuma versão leve apontar para ela.
            if (!c.IsLod && _lodFullActors.Contains(c.Actor)) continue;

            if (c.IsLod)
            {
                if (!ReadLodLocation(c, out c.Location)) continue;
            }
            else
            {
                c.Location = ReadLocation(c.RootComponent);
                if (!c.Location.IsFinite || c.Location.IsZero) continue;
            }

            c.DistanceMeters = c.Location.DistanceTo(cam.Location) / 100.0;
            if (c.DistanceMeters > (c.Filter is EggCategory or OrcTransmitterCategory or BossCategory or RecipeCategory or TechChipCategory or KeyCategory or UniqueChestCategory or ZipCategory ? eggDist : maxDist)) continue;

            // HealthState == Dead → nó já quebrado (molar coletado).
            c.Collected = c.HealthComponent != 0 &&
                          _mem.TryRead(c.HealthComponent + (ulong)Off.HealthComponent_HealthState, out byte st) && st == 2;
            // Corpo com Tech Chip: inventário vazio → já saqueado (oculto).
            if (c.IsLootBody)
            {
                c.Collected = LootBodyDone(c);
                int st2 = c.Collected ? 1 : 0;
                if (st2 != c.LoggedState)
                {
                    c.LoggedState = st2;
                    Logger.Info(Cat, $"{c.Name} 0x{c.Actor:X} a {c.DistanceMeters:F0} m: {(c.Collected ? (c.Filter == ZipCategory ? "Tirolesa não está mais no inventário (oculto)" : "inventário vazio (oculto)") : "com itens")}.");
                }
            }

            // Transmissor ORC já usado: ScanSwitch.bScanned (fica salvo no save) → tratado como "coletado".
            if (c.Filter == OrcTransmitterCategory)
            {
                c.Collected = _mem.TryRead(c.Actor + (ulong)Off.ScanSwitch_bScanned, out byte scanned) && scanned != 0;
                int state = c.Collected ? 1 : 0;
                if (state != c.LoggedState)
                {
                    c.LoggedState = state;
                    Logger.Info(Cat, $"Transmissor ORC 0x{c.Actor:X} a {c.DistanceMeters:F0} m: {(c.Collected ? "já ativado (oculto)" : "não ativado")}.");
                }
            }

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
