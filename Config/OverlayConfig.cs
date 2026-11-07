using System.Text.Json;
using System.Text.Json.Serialization;
using G2Overlay.Utils;

namespace G2Overlay.Config;

/// <summary>Configuração do usuário (Config/config.json). Editável pelo menu ou manualmente.</summary>
public sealed class OverlayConfig
{
    // ---------------- Geral
    public string ProcessName { get; set; } = "Grounded2Steam-Win64-Shipping";
    public bool EnableOverlay { get; set; } = true;
    public bool ShowMenuOnStart { get; set; } = true;
    public int FpsLimit { get; set; } = 60;
    public bool HideWhenGameNotFocused { get; set; } = true;

    // ---------------- ESP
    public bool EspBoxes { get; set; } = true;
    public bool EspHealthBars { get; set; } = true;
    public bool EspNames { get; set; } = true;
    public bool EspDistance { get; set; } = true;
    public bool EspHealthText { get; set; } = false;
    public bool EspTeam { get; set; } = false;
    public bool ShowDeadCreatures { get; set; } = false;
    public bool ShowPlayers { get; set; } = false;
    public float MaxDistanceMeters { get; set; } = 150f;
    public string BoxColor { get; set; } = "#00FF00";
    public float BoxThickness { get; set; } = 1.5f;
    public float HealthBarHeight { get; set; } = 4f;

    /// <summary>"Mesh" = usa Bounds da malha (envolve a criatura toda); "Capsule" = usa a cápsula de colisão.</summary>
    public string BoxSource { get; set; } = "Mesh";

    /// <summary>Aplica os multiplicadores de vida (NG+, modo personalizado) ao MaxHealth.</summary>
    public bool ApplyHealthMultipliers { get; set; } = true;

    // ---------------- ESP de coletáveis
    public bool EspCollectibles { get; set; } = true;

    /// <summary>
    /// Termos procurados (sem diferenciar maiúsculas, ignorando "_" e espaços) no nome da classe do ator
    /// e no NodeData.RowName de HarvestNodes. Os Milk Molars do mundo são "BP_Milk_Nugget_*".
    /// </summary>
    public List<string> CollectibleFilters { get; set; } = new() { "milkmolar", "milknugget", "techchip", "uniquechest", "bossmanager" };

    /// <summary>
    /// Termos que precisam ser uma PALAVRA INTEIRA (entre "_") do nome da classe, e só para itens no chão (SpawnedItem).
    /// Ex.: "Key" casa BP_Key_CoaltanaMats_C mas não BP_Haulable_KeyLime_Seed_C; "Recipe" casa BP_Recipe_SpicyKatana_C
    /// mas não BP_Recipe_Workbench_C (bancada construída, não é item).
    /// </summary>
    public List<string> CollectibleItemTokens { get; set; } = new() { "Key", "Recipe" };

    /// <summary>Cor por termo (o termo que casou define a cor). Termos sem cor usam CollectibleColor.</summary>
    public Dictionary<string, string> CollectibleColors { get; set; } = new()
    {
        ["milkmolar"] = "#00E5FF",
        ["milknugget"] = "#00E5FF",
        ["techchip"] = "#FF4FD8",
        ["key"] = "#FFD400",
        ["recipe"] = "#FF8C1A",
        ["uniquechest"] = "#B388FF",
        ["bossmanager"] = "#FF5252",
    };
    public string CollectibleColor { get; set; } = "#00E5FF";

    /// <summary>Termos padrão que o usuário removeu de propósito (não são readicionados).</summary>
    public List<string> RemovedDefaultFilters { get; set; } = new();
    public float CollectibleMaxDistanceMeters { get; set; } = 300f;

    /// <summary>Meia-aresta (cm) da caixa desenhada ao redor do coletável.</summary>
    public float CollectibleBoxExtent { get; set; } = 25f;
    public bool ShowCollectedCollectibles { get; set; } = false;

    /// <summary>Raio (m) do diagnóstico da tecla F9.</summary>
    public float DiagnosticRadiusMeters { get; set; } = 15f;

    // ---------------- AimBot (só criaturas; nunca jogadores)
    public bool AimEnabled { get; set; } = false;

    /// <summary>Tecla (código virtual do Windows) que precisa estar pressionada. 0x02 = botão direito.</summary>
    public int AimKey { get; set; } = 0x02;

    /// <summary>Raio (px) do círculo, a partir do centro da tela, onde um alvo pode ser escolhido.</summary>
    public float AimFovPixels { get; set; } = 150f;

    /// <summary>Quanto maior, mais suave/lento (fração do erro corrigida por frame = Ganho / Suavização).</summary>
    public float AimSmoothing { get; set; } = 6f;

    /// <summary>Converte pixels de erro em contagens de mouse; ajuste fino da velocidade.</summary>
    public float AimGain { get; set; } = 1f;

    /// <summary>Máximo de contagens de mouse por frame em cada eixo.</summary>
    public float AimMaxStep { get; set; } = 40f;

    /// <summary>Altura do ponto de mira na caixa: 0 = base, 0,5 = centro, 1 = topo.</summary>
    public float AimHeight { get; set; } = 0.55f;

    public float AimMaxDistanceMeters { get; set; } = 60f;
    public bool AimShowFov { get; set; } = true;
    public bool AimTargetIncapacitated { get; set; } = false;

    /// <summary>Não mirar criaturas domadas (pets / buggies).</summary>
    public bool AimIgnoreTamed { get; set; } = true;

    // ---------------- Desempenho
    public int EntityScanIntervalMs { get; set; } = 500;
    public int FullRevalidateIntervalMs { get; set; } = 10000;

    // ---------------- Offsets
    public bool AutoImportDump { get; set; } = true;
    public List<string> DumpSearchDirectories { get; set; } = new() { ".", "Claude_Test", "Dumps" };

    // ---------------- Logs
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public bool ConsoleLog { get; set; } = false;

    // ---------------- Menu (persistido)
    public float MenuX { get; set; } = 40f;
    public float MenuY { get; set; } = 80f;

    public void Sanitize()
    {
        FpsLimit = Math.Clamp(FpsLimit, 10, 240);
        MaxDistanceMeters = Math.Clamp(MaxDistanceMeters, 5f, 2000f);
        BoxThickness = Math.Clamp(BoxThickness, 0.5f, 6f);
        HealthBarHeight = Math.Clamp(HealthBarHeight, 2f, 12f);
        EntityScanIntervalMs = Math.Clamp(EntityScanIntervalMs, 100, 5000);
        FullRevalidateIntervalMs = Math.Clamp(FullRevalidateIntervalMs, 1000, 120000);
        CollectibleMaxDistanceMeters = Math.Clamp(CollectibleMaxDistanceMeters, 5f, 5000f);
        CollectibleBoxExtent = Math.Clamp(CollectibleBoxExtent, 2f, 500f);
        AimFovPixels = Math.Clamp(AimFovPixels, 10f, 1500f);
        AimSmoothing = Math.Clamp(AimSmoothing, 1f, 50f);
        AimGain = Math.Clamp(AimGain, 0.05f, 10f);
        AimMaxStep = Math.Clamp(AimMaxStep, 1f, 500f);
        AimHeight = Math.Clamp(AimHeight, 0f, 1f);
        AimMaxDistanceMeters = Math.Clamp(AimMaxDistanceMeters, 2f, 1000f);
        if (AimKey <= 0 || AimKey > 0xFE) AimKey = 0x02;
        CollectibleFilters = CollectibleFilters.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        // Migração: "opticaldisk" (versão anterior) não existe no jogo — o item é o Tech Chip (BP_TechChip_Base_C).
        CollectibleFilters.RemoveAll(f => f.Equals("opticaldisk", StringComparison.OrdinalIgnoreCase));
        CollectibleColors ??= new Dictionary<string, string>();
        CollectibleColors.Remove("opticaldisk");
        if (!CollectibleFilters.Any(f => f.Equals("techchip", StringComparison.OrdinalIgnoreCase)) && !RemovedDefaultFilters.Contains("techchip"))
            CollectibleFilters.Add("techchip");
        CollectibleColors.TryAdd("techchip", "#FF4FD8");

        // Configs antigas: adiciona os termos de item "Key" e "Recipe" (exceto se removidos de propósito).
        CollectibleItemTokens ??= new List<string>();
        CollectibleItemTokens = CollectibleItemTokens.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        foreach (var t in new[] { "Key", "Recipe" })
        {
            if (!CollectibleItemTokens.Any(f => f.Equals(t, StringComparison.OrdinalIgnoreCase)) &&
                !RemovedDefaultFilters.Any(f => f.Equals(t, StringComparison.OrdinalIgnoreCase)))
                CollectibleItemTokens.Add(t);
        }

        CollectibleColors.TryAdd("key", "#FFD400");
        CollectibleColors.TryAdd("recipe", "#FF8C1A");

        // Baús únicos enterrados (BP_World_Buried_UniqueChest_*), adicionados nesta versão.
        if (!CollectibleFilters.Any(f => f.Equals("uniquechest", StringComparison.OrdinalIgnoreCase)) &&
            !RemovedDefaultFilters.Any(f => f.Equals("uniquechest", StringComparison.OrdinalIgnoreCase)))
            CollectibleFilters.Add("uniquechest");
        CollectibleColors.TryAdd("uniquechest", "#B388FF");

        // Arenas de chefes (BP_BossManager_*), adicionadas nesta versão.
        if (!CollectibleFilters.Any(f => f.Equals("bossmanager", StringComparison.OrdinalIgnoreCase)) &&
            !RemovedDefaultFilters.Any(f => f.Equals("bossmanager", StringComparison.OrdinalIgnoreCase)))
            CollectibleFilters.Add("bossmanager");
        CollectibleColors.TryAdd("bossmanager", "#FF5252");
        if (BoxSource != "Mesh" && BoxSource != "Capsule") BoxSource = "Mesh";
        if (string.IsNullOrWhiteSpace(ProcessName)) ProcessName = "Grounded2Steam-Win64-Shipping";
    }
}

public static class ConfigManager
{
    private const string Cat = "Config";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string BaseDirectory { get; } = AppContext.BaseDirectory;
    public static string ConfigDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Config");
    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");
    public static string OffsetsPath => Path.Combine(ConfigDirectory, "offsets.json");
    public static string LogDirectory => Path.Combine(BaseDirectory, "logs");

    public static OverlayConfig Load()
    {
        Directory.CreateDirectory(ConfigDirectory);
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<OverlayConfig>(File.ReadAllText(ConfigPath), Json) ?? new OverlayConfig();
                cfg.Sanitize();
                return cfg;
            }
        }
        catch (Exception ex)
        {
            // O logger ainda pode não estar inicializado: a mensagem fica na fila e é gravada depois.
            Logger.Error(Cat, "config.json inválido; usando padrões.", ex);
        }

        var def = new OverlayConfig();
        Save(def);
        return def;
    }

    public static void Save(OverlayConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, Json));
            Logger.Info(Cat, "config.json salvo.");
        }
        catch (Exception ex)
        {
            Logger.Error(Cat, "Falha ao salvar config.json.", ex);
        }
    }

    /// <summary>
    /// Resolve diretórios relativos ao executável E a cada pasta ancestral (até 6 níveis),
    /// para que "Claude_Test" seja encontrado tanto rodando de Overlay\ quanto de Overlay\bin\Release\...
    /// </summary>
    public static IEnumerable<string> ResolveDirectories(IEnumerable<string> dirs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bases = new List<string>();
        var cur = new DirectoryInfo(BaseDirectory);
        for (int i = 0; i < 6 && cur != null; i++, cur = cur.Parent) bases.Add(cur.FullName);

        foreach (var d in dirs)
        {
            if (Path.IsPathRooted(d))
            {
                if (seen.Add(d)) yield return d;
                continue;
            }

            foreach (var b in bases)
            {
                string full;
                try
                {
                    full = Path.GetFullPath(Path.Combine(b, d));
                }
                catch
                {
                    continue;
                }

                if (seen.Add(full)) yield return full;
            }
        }
    }
}
