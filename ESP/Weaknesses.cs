using System.Text;
using G2Overlay.Memory;
using G2Overlay.UnrealSDK;
using G2Overlay.Utils;

namespace G2Overlay.ESP;

/// <summary>
/// Fraquezas e resistências de uma criatura, lidas do próprio jogo.
///
/// UHealthComponent.ColliderConfigs é um TArray&lt;FHealthColliderConfig&gt; com multiplicadores de dano:
///   ComponentName (FString, colisor específico ou vazio = corpo todo) · DamageMultiplier ·
///   DamageTypeFlags (bits de ESurvivalDamageTypeFlags) · DamageType (classe de dano específica).
/// Multiplicador &gt; 1 = fraqueza, &lt; 1 = resistência, 0 = imune. Entradas sem tipo de dano (só o colisor) são
/// os pontos fracos físicos e já aparecem como marcadores; aqui ficam de fora.
/// </summary>
public static class Weaknesses
{
    private const string Cat = "Weak";

    /// <summary>ESurvivalDamageTypeFlags → nome mostrado (nomes do jogo em inglês; Mining = Busting, Mint = Fresh).</summary>
    private static readonly string?[] FlagNames =
    {
        "Smashing", "Chopping", "Busting", "Stabbing", null /*Digging*/, "Gas", "Fresh", "Slashing", "Burning", "Explosive",
        "Shock", "Salty", "Spicy", "Acid", "Pollen", "Gas", "Shockwave", "Sour", null, null, null, "Freeze", "Venom",
        "Water", null, null,
    };

    public readonly record struct Result(string Weak, string Resist, int Entries);

    /// <summary>
    /// Junta as duas fontes: efeitos de status permanentes da criatura (onde o Grounded 2 guarda as fraquezas e
    /// resistências elementais: StatusEffectData.Type = DamageReduction / DamageReductionMultiplier + DamageTypeFlags)
    /// e os multiplicadores de HealthComponent.ColliderConfigs.
    /// </summary>
    public static Result Read(ProcessMemory mem, FNameResolver names, ulong actor, ulong healthComponent, string className)
    {
        var weak = new Dictionary<string, float>();
        var resist = new Dictionary<string, float>();
        int n = ReadStatusEffects(mem, names, actor, className, weak, resist);
        var colliders = ReadColliders(mem, names, healthComponent, className);
        Merge(weak, colliders.Weak, true);
        Merge(resist, colliders.Resist, false);
        return new Result(Format(weak, true), Format(resist, false), n + colliders.Entries);
    }

    private static void Merge(Dictionary<string, float> into, Dictionary<string, float> from, bool max)
    {
        foreach (var kv in from)
            into[kv.Key] = into.TryGetValue(kv.Key, out var v) ? (max ? Math.Max(v, kv.Value) : Math.Min(v, kv.Value)) : kv.Value;
    }

    // ------------------------------------------------------------------ efeitos de status

    private const byte TypeDamageReduction = 29;
    private const byte TypeDamageReductionMultiplier = 30;
    private const int RowMapOffset = 0x30;   // UDataTable::RowMap (TMap<FName, uint8*>), logo após RowStruct (0x28)
    private const int RowMapElementSize = 24; // TSetElement<TPair<FName, uint8*>>: FName + ptr + HashNextId + HashIndex

    private static int ReadStatusEffects(ProcessMemory mem, FNameResolver names, ulong actor, string className,
                                         Dictionary<string, float> weak, Dictionary<string, float> resist)
    {
        ulong sec = mem.ReadPtr(actor + (ulong)Off.SurvivalCharacter_StatusEffectComponent);
        if (!ProcessMemory.IsValidObjectPointer(sec)) return 0;

        var effects = new List<ulong>();
        foreach (int off in new[] { Off.StatusEffectComponent_InfiniteDefaultStatusEffects, Off.StatusEffectComponent_StatusEffects })
        {
            if (!mem.TryRead(sec + (ulong)off, out TArrayHeader arr) || arr.Num <= 0 || !arr.IsPlausible(256)) continue;
            var ptrs = new ulong[arr.Num];
            if (mem.ReadArray(arr.Data, ptrs.AsSpan()))
                foreach (var e in ptrs)
                    if (ProcessMemory.IsValidObjectPointer(e) && !effects.Contains(e)) effects.Add(e);
        }

        var diag = new StringBuilder();
        int used = 0;
        foreach (ulong e in effects)
        {
            ulong table = mem.ReadPtr(e + (ulong)Off.StatusEffect_RowHandle);
            var row = mem.Read<FName>(e + (ulong)Off.StatusEffect_RowHandle + (ulong)Off.DataTableRowHandle_RowName);
            string rowName = row.ComparisonIndex > 0 ? names.GetName(row) : "";
            ulong data = FindRow(mem, table, row);
            if (data == 0)
            {
                diag.Append($" [{rowName}: linha não encontrada]");
                continue;
            }

            byte type = mem.Read<byte>(data + (ulong)Off.StatusEffectData_Type);
            float value = mem.Read<float>(data + (ulong)Off.StatusEffectData_Value);
            uint flags = mem.Read<uint>(data + (ulong)Off.StatusEffectData_DamageTypeFlags);
            ulong dmgClass = mem.ReadPtr(data + (ulong)Off.StatusEffectData_DamageType);
            var types = TypeNames(names, flags, dmgClass);
            diag.Append($" [{rowName}: tipo {type} valor {value:0.###} {(types.Count > 0 ? string.Join("/", types) : "sem tipo de dano")}]");
            if (types.Count == 0 || !float.IsFinite(value)) continue;

            // Multiplicador do dano recebido daquele tipo (confirmado no jogo: DamageResistDownSpicyMedium = 1,3;
            // DamageResistStabbingTiny = 0,75). Só os tipos 29/30 são fraquezas/resistências do Bestiário; o tipo 3
            // (Immunity, ex.: GrubImmunities) é imunidade a efeitos de status, não a dano, e fica de fora.
            float mult;
            if (type is TypeDamageReduction or TypeDamageReductionMultiplier) mult = value;
            else continue;
            if (mult < 0) continue;

            used++;
            foreach (var t in types)
            {
                if (mult > 1.01f) weak[t] = weak.TryGetValue(t, out var w) ? Math.Max(w, mult) : mult;
                else if (mult < 0.99f) resist[t] = resist.TryGetValue(t, out var r) ? Math.Min(r, mult) : mult;
            }
        }

        if (effects.Count > 0) Logger.Debug(Cat, $"{className}: {effects.Count} efeitos de status:{diag}");
        return used;
    }

    /// <summary>Procura a linha no RowMap do UDataTable (comparando o FName da chave).</summary>
    private static ulong FindRow(ProcessMemory mem, ulong table, FName row)
    {
        if (!ProcessMemory.IsValidObjectPointer(table) || row.ComparisonIndex <= 0) return 0;
        if (!mem.TryRead(table + RowMapOffset, out TArrayHeader arr) || arr.Num <= 0 || arr.Num > 20000) return 0;
        var buf = new byte[arr.Num * RowMapElementSize];
        if (!mem.ReadBytes(arr.Data, buf)) return 0;
        for (int i = 0; i < arr.Num; i++)
        {
            int b = i * RowMapElementSize;
            if (BitConverter.ToInt32(buf, b) != row.ComparisonIndex || BitConverter.ToInt32(buf, b + 4) != row.Number) continue;
            ulong ptr = BitConverter.ToUInt64(buf, b + 8);
            if (ProcessMemory.IsValidObjectPointer(ptr)) return ptr;
        }

        return 0;
    }

    private static List<string> TypeNames(FNameResolver names, uint flags, ulong dmgClass)
    {
        var types = new List<string>();
        for (int bit = 0; bit < FlagNames.Length; bit++)
            if ((flags & (1u << bit)) != 0 && FlagNames[bit] is { } n && !types.Contains(n)) types.Add(n);
        if (ProcessMemory.IsValidObjectPointer(dmgClass))
        {
            string dn = DamageTypeLabel(names.GetObjectName(dmgClass));
            if (dn.Length > 0 && !types.Contains(dn)) types.Add(dn);
        }

        return types;
    }

    // ------------------------------------------------------------------ colisores (HealthComponent.ColliderConfigs)

    private readonly record struct ColliderResult(Dictionary<string, float> Weak, Dictionary<string, float> Resist, int Entries);

    private static ColliderResult ReadColliders(ProcessMemory mem, FNameResolver names, ulong healthComponent, string className)
    {
        var empty = new ColliderResult(new(), new(), 0);
        if (!mem.TryRead(healthComponent + (ulong)Off.HealthComponent_ColliderConfigs, out TArrayHeader arr) ||
            arr.Num <= 0 || !arr.IsPlausible(128))
            return empty;

        int size = Off.HealthColliderConfig_Size;
        var buf = new byte[arr.Num * size];
        if (!mem.ReadBytes(arr.Data, buf)) return empty;

        // Por tipo: o multiplicador mais extremo (fraqueza: maior; resistência: menor).
        var weak = new Dictionary<string, float>();
        var resist = new Dictionary<string, float>();
        var diag = new StringBuilder();

        for (int i = 0; i < arr.Num; i++)
        {
            int b = i * size;
            string component = ReadFString(mem, buf, b + Off.HealthColliderConfig_ComponentName);
            float mult = BitConverter.ToSingle(buf, b + Off.HealthColliderConfig_DamageMultiplier);
            uint flags = BitConverter.ToUInt32(buf, b + Off.HealthColliderConfig_DamageTypeFlags);
            ulong dmgClass = BitConverter.ToUInt64(buf, b + Off.HealthColliderConfig_DamageType);
            if (!float.IsFinite(mult)) continue;

            var types = TypeNames(names, flags, dmgClass);

            diag.Append($" [{(component.Length > 0 ? component : "corpo")}: ×{mult:0.##} {(types.Count > 0 ? string.Join("/", types) : "sem tipo")}]");
            if (types.Count == 0) continue; // ponto fraco físico (já marcado no ESP)

            string suffix = component.Length > 0 ? $" ({Prettify(component)})" : "";
            foreach (var t in types)
            {
                string key = t + suffix;
                if (mult > 1.01f) weak[key] = weak.TryGetValue(key, out var w) ? Math.Max(w, mult) : mult;
                else if (mult < 0.99f) resist[key] = resist.TryGetValue(key, out var r) ? Math.Min(r, mult) : mult;
            }
        }

        Logger.Debug(Cat, $"{className}: {arr.Num} multiplicadores de dano:{diag}");
        return new ColliderResult(weak, resist, arr.Num);
    }

    private static string Format(Dictionary<string, float> d, bool weak)
    {
        if (d.Count == 0) return "";
        var immune = d.Where(kv => kv.Value <= 0.001f).Select(kv => kv.Key).ToList();
        var rest = d.Where(kv => kv.Value > 0.001f);
        var items = (weak ? rest.OrderByDescending(kv => kv.Value) : rest.OrderBy(kv => kv.Value))
            .Select(kv => $"{kv.Key} ×{kv.Value:0.##}").ToList();
        if (immune.Count >= 8) items.Insert(0, "imune a quase tudo");
        else if (immune.Count > 0) items.Insert(0, $"imune a {string.Join("/", immune)}");
        return string.Join(", ", items);
    }

    /// <summary>"BP_SpicyDamageType_C" → "Spicy".</summary>
    private static string DamageTypeLabel(string cls)
    {
        string n = cls;
        if (n.StartsWith("BP_", StringComparison.Ordinal)) n = n[3..];
        if (n.EndsWith("_C", StringComparison.Ordinal)) n = n[..^2];
        foreach (var suffix in new[] { "DamageType", "Damage" })
            if (n.EndsWith(suffix, StringComparison.Ordinal) && n.Length > suffix.Length) n = n[..^suffix.Length];
        n = n.Trim('_');
        return n == "General" ? "Smashing" : n;
    }

    private static string Prettify(string s) => Collectible.Prettify(s);

    /// <summary>FString inline no buffer: TCHAR* Data (8) + int32 Num (inclui o terminador).</summary>
    private static string ReadFString(ProcessMemory mem, byte[] buf, int offset)
    {
        ulong data = BitConverter.ToUInt64(buf, offset);
        int num = BitConverter.ToInt32(buf, offset + 8);
        if (num <= 1 || num > 256 || !ProcessMemory.IsValidObjectPointer(data)) return "";
        var chars = new byte[(num - 1) * 2];
        if (!mem.ReadBytes(data, chars)) return "";
        return Encoding.Unicode.GetString(chars);
    }
}
