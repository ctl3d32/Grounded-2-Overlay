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

    public ClassInfo? GetClassInfo(ulong classPtr)
    {
        if (!ProcessMemory.IsValidObjectPointer(classPtr)) return null;
        if (_classes.TryGetValue(classPtr, out var ci)) return ci;

        var chain = new List<string>(12);
        ulong c = classPtr;
        for (int i = 0; i < MaxChain && ProcessMemory.IsValidObjectPointer(c); i++)
        {
            string n = _names.GetObjectName(c);
            if (n.Length == 0) break;
            chain.Add(n);
            c = _mem.ReadPtr(c + (ulong)Off.UStruct_SuperStruct);
        }

        if (chain.Count == 0)
        {
            // Não cacheia falhas permanentemente: o ponteiro pode ser lixo transitório.
            return null;
        }

        ci = new ClassInfo
        {
            Ptr = classPtr,
            Name = chain[0],
            Chain = chain.ToArray(),
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
