using G2Overlay.Core;
using G2Overlay.Memory;
using G2Overlay.UnrealSDK;
using G2Overlay.Utils;

namespace G2Overlay.Aim;

/// <summary>
/// Descobre, lendo o próprio jogo, quais tipos de dano não podem ser bloqueados.
///
/// Percorre GObjects aos poucos (alguns milhares de objetos por frame) atrás dos Class Default Objects (CDO,
/// flag RF_ClassDefaultObject) de classes derivadas de USurvivalDamageType, e lê bCanBlock / bCanPerfectDodge.
/// Um tipo como BP_CricketLeapOfFaithDamage_C vira as palavras {cricket, leap, faith}; uma montagem cujo nome
/// contém todas elas (AM_Cricket_Attack_LeapOfFaith) é tratada como imbloqueável.
/// </summary>
public sealed class UnblockableScanner
{
    private const string Cat = "Block";
    private const int ChunkSize = 64 * 1024;
    private const int RF_ClassDefaultObject = 0x10;

    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "damage", "unblockable", "the", "and", "base", "type", "attack",
    };

    private GameSession? _session;
    private ulong _chunks;
    private int _num;
    private int _itemSize;
    private int _objOffset;
    private int _next;
    private bool _failed;
    private readonly List<(string Name, string[] Words, bool CanDodge)> _types = new();
    private readonly Dictionary<string, bool> _montageCache = new();
    private readonly HashSet<string> _allUnblockable = new(StringComparer.Ordinal);

    /// <summary>Classe de dano (ex.: BP_GeneralUnblockableDamage_C) marcada no jogo como imbloqueável.</summary>
    public bool IsUnblockableType(string className) => _allUnblockable.Contains(className);

    public bool Done { get; private set; }
    public int Version { get; private set; }

    public void Reset()
    {
        _session = null;
        Done = false;
        _failed = false;
        _next = 0;
        _types.Clear();
        _allUnblockable.Clear();
        _montageCache.Clear();
        Version++;
    }

    /// <summary>A montagem corresponde a um tipo de dano imbloqueável encontrado no jogo?</summary>
    public bool MatchesMontage(string montage, out string damageType)
    {
        damageType = "";
        if (_types.Count == 0 || montage.Length == 0) return false;
        string norm = ObjectReader.Normalize(montage);
        foreach (var t in _types)
        {
            if (t.Words.All(w => norm.Contains(w, StringComparison.Ordinal)))
            {
                damageType = t.Name;
                return true;
            }
        }

        return false;
    }

    /// <summary>Processa até <paramref name="budget"/> objetos. Chame a cada frame até <see cref="Done"/>.</summary>
    public void Step(GameSession s, int budget)
    {
        if (Done || _failed) return;
        if (!ReferenceEquals(s, _session))
        {
            Reset();
            _session = s;
        }

        var mem = s.Mem;
        if (_itemSize == 0 || _chunks == 0)
        {
            ulong gobjects = s.Globals.GObjects;
            if (gobjects == 0) return;
            _chunks = mem.ReadPtr(gobjects + (ulong)Off.FUObjectArray_ObjObjects);
            _num = mem.Read<int>(gobjects + (ulong)Off.FUObjectArray_NumElements);
            if (!ProcessMemory.IsValidObjectPointer(_chunks) || _num <= 0 || _num > 50_000_000)
            {
                Fail("GObjects ilegível");
                return;
            }

            _itemSize = DetectItemSize(mem);
            if (_itemSize == 0)
            {
                Fail("tamanho de FUObjectItem não reconhecido");
                return;
            }
        }

        int end = Math.Min(_num, _next + budget);
        while (_next < end)
        {
            int chunkIndex = _next / ChunkSize;
            int within = _next % ChunkSize;
            int count = Math.Min(end - _next, ChunkSize - within);
            ulong chunk = mem.ReadPtr(_chunks + (ulong)chunkIndex * 8);
            if (!ProcessMemory.IsValidObjectPointer(chunk))
            {
                _next += count;
                continue;
            }

            var items = new byte[count * _itemSize];
            if (mem.ReadBytes(chunk + (ulong)within * (ulong)_itemSize, items))
            {
                for (int i = 0; i < count; i++)
                {
                    ulong obj = BitConverter.ToUInt64(items, i * _itemSize + _objOffset);
                    if (ProcessMemory.IsValidObjectPointer(obj)) Inspect(s, obj);
                }
            }

            _next += count;
        }

        if (_next >= _num)
        {
            Done = true;
            Version++;
            _montageCache.Clear();
            Logger.Info(Cat, _types.Count == 0
                ? "Tipos de dano imbloqueáveis: nenhum encontrado (a lista manual AutoDodgeMontages continua valendo)."
                : $"Tipos de dano imbloqueáveis encontrados no jogo ({_types.Count}): " +
                  string.Join(", ", _types.Select(t => $"{t.Name}{(t.CanDodge ? " (esquiva perfeita)" : "")}")));
        }
    }

    private void Inspect(GameSession s, ulong obj)
    {
        var mem = s.Mem;
        if (!mem.TryRead(obj + (ulong)Off.UObject_ObjectFlags, out int flags) || (flags & RF_ClassDefaultObject) == 0) return;
        var ci = s.Objects.GetObjectClassInfo(obj);
        if (ci == null || Array.IndexOf(ci.Chain, "SurvivalDamageType") < 0) return;
        if (!mem.TryRead(obj + (ulong)Off.SurvivalDamageType_bCanBlock, out byte canBlock) || canBlock != 0) return;
        mem.TryRead(obj + (ulong)Off.SurvivalDamageType_bCanPerfectDodge, out byte canDodge);

        _allUnblockable.Add(ci.Name);
        string[] words = Words(ci.Name);
        if (words.Length == 0)
        {
            Logger.Debug(Cat, $"Dano imbloqueável sem palavras específicas (ignorado no mapeamento): {ci.Name}");
            return;
        }

        _types.Add((ci.Name, words, canDodge != 0));
        Logger.Debug(Cat, $"Dano imbloqueável: {ci.Name} · palavras [{string.Join(", ", words)}] · esquiva perfeita={(canDodge != 0 ? "sim" : "não")}");
    }

    /// <summary>"BP_CricketLeapOfFaithDamage_C" → ["cricket", "leap", "faith"].</summary>
    public static string[] Words(string className)
    {
        string n = className;
        if (n.StartsWith("BP_", StringComparison.Ordinal)) n = n[3..];
        if (n.EndsWith("_C", StringComparison.Ordinal)) n = n[..^2];
        var words = new List<string>();
        var sb = new System.Text.StringBuilder();
        void Flush()
        {
            if (sb.Length >= 3 && !GenericWords.Contains(sb.ToString())) words.Add(sb.ToString().ToLowerInvariant());
            sb.Clear();
        }

        for (int i = 0; i < n.Length; i++)
        {
            char ch = n[i];
            if (ch == '_' || !char.IsLetterOrDigit(ch)) { Flush(); continue; }
            if (i > 0 && char.IsUpper(ch) && (char.IsLower(n[i - 1]) || (i + 1 < n.Length && char.IsLower(n[i + 1]) && char.IsUpper(n[i - 1])))) Flush();
            sb.Append(ch);
        }

        Flush();
        return words.Distinct().ToArray();
    }

    /// <summary>
    /// Layout de FUObjectItem: tamanho e posição do ponteiro do objeto dentro do item. Validação: o objeto no índice i
    /// precisa ter InternalIndex == i. Testa tamanhos de 0x10 a 0x40 e o ponteiro em +0 ou +8.
    /// </summary>
    private int DetectItemSize(ProcessMemory mem)
    {
        ulong chunk0 = mem.ReadPtr(_chunks);
        if (!ProcessMemory.IsValidObjectPointer(chunk0))
        {
            Logger.Warn(Cat, $"GObjects: chunk 0 inválido (Objects=0x{_chunks:X} → 0x{chunk0:X}).");
            return 0;
        }

        var head = new byte[0x40 * 26];
        if (!mem.ReadBytes(chunk0, head)) return 0;
        for (int size = 0x10; size <= 0x40; size += 8)
        {
            foreach (int objOff in new[] { 0, 8 })
            {
                int ok = 0;
                for (int i = 1; i <= 24; i++)
                {
                    ulong obj = BitConverter.ToUInt64(head, i * size + objOff);
                    if (ProcessMemory.IsValidObjectPointer(obj) && mem.TryRead(obj + (ulong)Off.UObject_InternalIndex, out int idx) && idx == i) ok++;
                }

                if (ok >= 20)
                {
                    _objOffset = objOff;
                    Logger.Debug(Cat, $"FUObjectItem: {size} bytes, objeto em +0x{objOff:X} ({ok}/24 confirmados). Procurando tipos de dano imbloqueáveis em {_num} objetos…");
                    return size;
                }
            }
        }

        Logger.Warn(Cat, $"GObjects: layout de FUObjectItem não reconhecido. Primeiros bytes do chunk 0 (0x{chunk0:X}): " +
                         BitConverter.ToString(head, 0, 0x60).Replace("-", " "));
        return 0;
    }

    private void Fail(string why)
    {
        _failed = true;
        Logger.Warn(Cat, $"Busca de tipos de dano imbloqueáveis desativada: {why}. A lista manual AutoDodgeMontages continua valendo.");
    }
}
