using G2Overlay.Memory;
using G2Overlay.Utils;

namespace G2Overlay.UnrealSDK;

/// <summary>Informações de uma UClass (cadeia de herança resolvida e cacheada por ponteiro).</summary>
public sealed class ClassInfo
{
    public ulong Ptr;
    public string Name = "";
    public string[] Chain = Array.Empty<string>();
    public bool IsActor;
    public bool IsCharacter;
    public bool IsSurvivalCharacter;
    public bool IsCreature;
    public bool IsPlayerCharacter;
    public bool IsHarvestNode;
    public bool IsSpawnedItem;

    /// <summary>Nome da classe em minúsculas sem "_" nem espaços (para filtros por termo).</summary>
    public string NormalizedName = "";

    /// <summary>Nome amigável: "BP_Ant_Soldier_Augusta_C" → "Ant Soldier Augusta".</summary>
    public string FriendlyName = "";

    // ---------------- validação contra reaproveitamento de endereço
    // Durante o streaming de níveis o jogo descarrega e recarrega classes; o endereço de uma classe liberada
    // pode passar a ser de outra classe. Guardamos o FName e o SuperStruct para detectar isso.
    public ulong NameRaw;
    public ulong SuperPtr;
    public long CreatedAt;
    public long ValidatedAt;

    // ---------------- resultado do filtro de coletáveis (calculado uma vez por classe e versão dos filtros)
    public int MatchVersion = -1;
    public string? CollectibleMatch;

    public override string ToString() => $"{Name} : {string.Join(" > ", Chain.Skip(1).Take(4))}";
}

/// <summary>
/// Leitura genérica de UObjects: classe, nome, herança (SuperStruct) e IsA.
/// </summary>
public sealed class ObjectReader
{
    private const string Cat = "UObject";
    private const int MaxChain = 24;

    private readonly ProcessMemory _mem;
    private readonly FNameResolver _names;
    private readonly Dictionary<ulong, ClassInfo?> _classes = new(1024);

    public ObjectReader(ProcessMemory mem, FNameResolver names)
    {
        _mem = mem;
        _names = names;
    }

    public int CachedClasses => _classes.Count;

    public ulong GetClass(ulong obj) =>
        ProcessMemory.IsValidObjectPointer(obj) ? _mem.ReadPtr(obj + (ulong)Off.UObject_ClassPrivate) : 0;

    public string GetName(ulong obj) => _names.GetObjectName(obj);

    public string GetClassName(ulong obj) => _names.GetObjectName(GetClass(obj));

    /// <summary>
    /// Detecta UStruct::SuperStruct partindo da classe do UWorld: a cadeia precisa ser World → Object → null.
    /// </summary>
    public bool DetectSuperStruct(ulong worldObj)
    {
        ulong worldClass = GetClass(worldObj);
        string wn = _names.GetObjectName(worldClass);
        if (wn != "World")
        {
            Logger.Error(Cat, $"Classe do GWorld é '{wn}', esperado 'World'.");
            return false;
        }

        int[] candidates = { Off.UStruct_SuperStruct, 0x40, 0x48, 0x38, 0x30, 0x50, 0x58 };
        foreach (int off in candidates.Distinct())
        {
            ulong sup = _mem.ReadPtr(worldClass + (ulong)off);
            if (!ProcessMemory.IsValidObjectPointer(sup)) continue;
            if (_names.GetObjectName(sup) != "Object") continue;
            if (_mem.ReadPtr(sup + (ulong)off) != 0) continue;

            if (off != Off.UStruct_SuperStruct)
                Logger.Warn(Cat, $"UStruct.SuperStruct detectado em 0x{off:X} (padrão 0x{Off.UStruct_SuperStruct:X}).");
            Off.Set("UStruct.SuperStruct", off);
            Logger.Info(Cat, $"UStruct.SuperStruct validado em 0x{off:X} (World → Object → null).");
            _classes.Clear();
            return true;
        }

        Logger.Error(Cat, "Não foi possível detectar UStruct.SuperStruct.");
        return false;
    }

    /// <summary>Intervalo entre revalidações de uma classe em cache (1 leitura de FName + 1 de SuperStruct).</summary>
    private const long RevalidateMs = 1000;

    /// <summary>Idade máxima de uma entrada do cache; depois disso a cadeia é relida do zero (rede de segurança).</summary>
    private const long MaxAgeMs = 5 * 60 * 1000;

    /// <summary>Classes cujo endereço passou a apontar para outra coisa (descarregadas/recarregadas pelo jogo).</summary>
    public int StaleClassesDetected { get; private set; }

    /// <summary>Cadeias de herança que não terminaram em "Object" (leitura incompleta; não entram no cache).</summary>
    public int IncompleteChains { get; private set; }

    public ClassInfo? GetClassInfo(ulong classPtr)
    {
        if (!ProcessMemory.IsValidObjectPointer(classPtr)) return null;
        long now = Environment.TickCount64;
        if (_classes.TryGetValue(classPtr, out var cached) && cached != null)
        {
            if (now - cached.ValidatedAt < RevalidateMs) return cached;
            if (now - cached.CreatedAt < MaxAgeMs && StillSameClass(classPtr, cached))
            {
                cached.ValidatedAt = now;
                return cached;
            }

            _classes.Remove(classPtr);
            if (now - cached.CreatedAt < MaxAgeMs)
            {
                StaleClassesDetected++;
                Logger.Debug(Cat, $"Classe em cache mudou no endereço 0x{classPtr:X} (era {cached.Name}); relendo.");
            }
        }

        if (!_mem.TryRead(classPtr + (ulong)Off.UObject_NamePrivate, out ulong nameRaw)) return null;
        ulong superPtr = _mem.ReadPtr(classPtr + (ulong)Off.UStruct_SuperStruct);

        var chain = new List<string>(12);
        ulong c = classPtr;
        bool complete = false;
        for (int i = 0; i < MaxChain && ProcessMemory.IsValidObjectPointer(c); i++)
        {
            string n = _names.GetObjectName(c);
            if (n.Length == 0) break;
            chain.Add(n);
            if (n == "Object")
            {
                complete = true;
                break;
            }

            c = _mem.ReadPtr(c + (ulong)Off.UStruct_SuperStruct);
        }

        if (!complete)
        {
            // Cadeia incompleta (leitura falhou no meio, classe sendo carregada/descarregada): não cacheia,
            // senão a classe ficaria marcada como "não é ator" até o Recarregar. Tenta de novo no próximo scan.
            IncompleteChains++;
            return null;
        }

        var ci = new ClassInfo
        {
            Ptr = classPtr,
            Name = chain[0],
            Chain = chain.ToArray(),
            NameRaw = nameRaw,
            SuperPtr = superPtr,
            CreatedAt = now,
            ValidatedAt = now,
        };
        foreach (var n in chain)
        {
            switch (n)
            {
                case "Actor": ci.IsActor = true; break;
                case "Character": ci.IsCharacter = true; break;
                case "SurvivalCharacter": ci.IsSurvivalCharacter = true; break;
                case "SurvivalCreature": ci.IsCreature = true; break;
                case "SurvivalPlayerCharacter": ci.IsPlayerCharacter = true; break;
                case "HarvestNode": ci.IsHarvestNode = true; break;
                case "SpawnedItem": ci.IsSpawnedItem = true; break;
            }
        }

        ci.FriendlyName = MakeFriendly(ci.Name);
        ci.NormalizedName = Normalize(ci.Name);
        if (_classes.Count > 20000) _classes.Clear();
        _classes[classPtr] = ci;
        if (ci.IsCreature || ci.IsPlayerCharacter)
            Logger.Debug(Cat, $"Classe registrada: {ci}");
        return ci;
    }

    /// <summary>O endereço ainda contém a mesma classe? (mesmo FName e mesmo SuperStruct)</summary>
    private bool StillSameClass(ulong classPtr, ClassInfo ci) =>
        _mem.TryRead(classPtr + (ulong)Off.UObject_NamePrivate, out ulong nameRaw) && nameRaw == ci.NameRaw &&
        _mem.ReadPtr(classPtr + (ulong)Off.UStruct_SuperStruct) == ci.SuperPtr;

    public ClassInfo? GetObjectClassInfo(ulong obj) => GetClassInfo(GetClass(obj));

    public void ClearCache() => _classes.Clear();

    public static string Normalize(string s) =>
        s.Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    public static string MakeFriendly(string className)
    {
        string s = className;
        if (s.StartsWith("BP_", StringComparison.Ordinal)) s = s[3..];
        if (s.EndsWith("_C", StringComparison.Ordinal)) s = s[..^2];
        return s.Replace('_', ' ').Trim();
    }
}
