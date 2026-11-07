using G2Overlay.UnrealSDK;

namespace G2Overlay.ESP;

/// <summary>
/// Coletável rastreado (ex.: Milk Molar = BP_Milk_Nugget_Personal_C / _Party_C / _Buggies_C).
/// </summary>
public sealed class Collectible
{
    // ---------------- estáticos (scan)
    public ulong Actor;
    public ulong ClassPtr;
    public ClassInfo Class = null!;
    public ulong RootComponent;
    public ulong HealthComponent;
    public string Name = "";
    public string MatchedBy = "";
    public string Filter = "";
    public string ColorHex = "#00E5FF";

    // ---------------- dinâmicos (frame)
    public FVector Location;
    public double DistanceMeters;
    public bool Collected;

    /// <summary>Arena de chefe (BossManager) — e se a luta está em andamento (bBossActive).</summary>
    public bool IsBossManager;
    public bool BossActive;
    public bool Visible;

    /// <summary>"Spear_Tier2" → "Spear Tier2"; "SlimeMold" → "Slime Mold".</summary>
    public static string Prettify(string row)
    {
        var sb = new System.Text.StringBuilder(row.Length + 8);
        for (int i = 0; i < row.Length; i++)
        {
            char ch = row[i];
            if (ch == '_') { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(ch) && char.IsLower(row[i - 1]) && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            sb.Append(ch);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// "TechChip_Ominent_Super_Stolen" → "Super Tech Chip (Ominent · Stolen)";
    /// "TechChip_Ominent_Hedge" → "Tech Chip (Ominent · Hedge)". Também aceita o nome da classe.
    /// </summary>
    public static string TechChipName(string source)
    {
        string s = source;
        if (s.StartsWith("BP_", StringComparison.Ordinal)) s = s[3..];
        if (s.EndsWith("_C", StringComparison.Ordinal)) s = s[..^2];
        bool super = s.Contains("Super", StringComparison.OrdinalIgnoreCase);
        var parts = s.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.Equals("TechChip", StringComparison.OrdinalIgnoreCase) &&
                        !p.Equals("SuperTechChip", StringComparison.OrdinalIgnoreCase) &&
                        !p.Equals("Super", StringComparison.OrdinalIgnoreCase))
            .Select(Prettify)
            .ToList();
        string title = super ? "Super Tech Chip" : "Tech Chip";
        return parts.Count > 0 ? $"{title} ({string.Join(" · ", parts)})" : title;
    }

    /// <summary>"BP_BossManager_Centipede_C" → "Boss: Centipede"; "BP_BossManager_SubjectV_C" → "Boss: Subject V".</summary>
    public static string BossName(string className)
    {
        const string marker = "BossManager_";
        string name = className.EndsWith("_C", StringComparison.Ordinal) ? className[..^2] : className;
        int i = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "Boss";
        string rest = name[(i + marker.Length)..];
        if (rest.Length == 0) return "Boss";
        var parts = rest.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(Prettify).ToArray();
        return parts.Length == 1 ? $"Boss: {parts[0]}" : $"Boss: {parts[0]} ({string.Join(" ", parts.Skip(1))})";
    }

    /// <summary>"BP_World_Buried_UniqueChest_IceCart_C" → "Unique Chest (Ice Cart)".</summary>
    public static string UniqueChestName(string className)
    {
        const string marker = "UniqueChest_";
        int i = className.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "Unique Chest";
        string rest = className[(i + marker.Length)..];
        if (rest.EndsWith("_C", StringComparison.Ordinal)) rest = rest[..^2];
        return rest.Length > 0 && !rest.Equals("Base", StringComparison.OrdinalIgnoreCase)
            ? $"Unique Chest ({Prettify(rest)})"
            : "Unique Chest";
    }

    /// <summary>Nome amigável a partir de ECollectibleType (EMolarTypeForTooltip).</summary>
    public static string MolarName(byte type, string fallback) => type switch
    {
        2 => "Milk Molar",
        3 => "Milk Molar (grupo)",
        4 => "Milk Molar (buggy)",
        1 => "Coletável (cor)",
        _ => fallback,
    };
}
