using System.Text.Json;
using G2Overlay.Utils;

namespace G2Overlay.UnrealSDK;

/// <summary>
/// Importa offsets refletidos (UPROPERTY) de um dump JSONL do SDK
/// (formato: uma linha "meta" + linhas "class"/"struct"/"enum" com "props":[{name, offset, ...}]).
/// Isto torna a atualização de offsets automática: depois de um patch do jogo basta gerar
/// um novo dump e o overlay reimporta tudo por NOME (Classe.Propriedade), sem editar código.
/// </summary>
public static class DumpOffsetImporter
{
    private const string Cat = "DumpImport";

    public sealed class DumpMeta
    {
        public int UeVersion;
        public ulong ModuleBase;
        public ulong GWorld;
        public ulong GNames;
        public ulong GObjects;
        public long ObjectCount;
        public string DumpedAt = "";
    }

    public static FileInfo? FindNewestDump(IEnumerable<string> directories)
    {
        FileInfo? best = null;
        foreach (var dir in directories)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*dump*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    // "*.objects.jsonl" é a lista de instâncias do dumper (sem classes/props): não serve para offsets.
                    if (f.Name.EndsWith(".objects.jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                    if (best == null || f.LastWriteTimeUtc > best.LastWriteTimeUtc) best = f;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(Cat, $"Não foi possível listar {dir}: {ex.Message}");
            }
        }

        return best;
    }

    /// <summary>
    /// Lê o dump e aplica em <see cref="Off"/> todos os offsets refletidos encontrados.
    /// Retorna quantos foram aplicados (ou -1 em caso de erro).
    /// </summary>
    public static int Import(FileInfo dump, out DumpMeta? meta)
    {
        meta = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var wanted = new HashSet<string>(Off.Defs.Where(d => d.Source == OffsetSource.Reflected).Select(d => d.Class));
        var props = new Dictionary<string, Dictionary<string, int>>(); // classe → (prop → offset)

        try
        {
            using var reader = new StreamReader(dump.FullName);
            string? line;
            int lineNo = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNo++;
                if (line.Length < 10) continue;

                if (line.StartsWith("{\"kind\":\"meta\"", StringComparison.Ordinal))
                {
                    meta = ParseMeta(line);
                    continue;
                }

                string? name = ExtractName(line);
                if (name == null || !wanted.Contains(name)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var map = new Dictionary<string, int>(StringComparer.Ordinal);
                if (root.TryGetProperty("props", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in arr.EnumerateArray())
                    {
                        if (p.TryGetProperty("name", out var pn) && p.TryGetProperty("offset", out var po))
                            map[pn.GetString() ?? ""] = po.GetInt32();
                    }
                }

                // Pode haver classe e struct com o mesmo nome curto; mantém a que tem mais propriedades.
                if (!props.TryGetValue(name, out var existing) || existing.Count < map.Count)
                    props[name] = map;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(Cat, $"Falha ao ler o dump {dump.FullName}.", ex);
            return -1;
        }

        if (meta != null && meta.UeVersion != 0 && meta.UeVersion / 100 != 5)
            Logger.Warn(Cat, $"Dump reporta UE {meta.UeVersion}; o overlay foi projetado para UE 5.7 (507).");

        int applied = 0, changed = 0, missing = 0;
        foreach (var d in Off.Defs.Where(d => d.Source == OffsetSource.Reflected))
        {
            if (props.TryGetValue(d.Class, out var map) && d.Property != null && map.TryGetValue(d.Property, out int off))
            {
                int old = Off.Get(d.Key);
                if (old != off)
                {
                    Logger.Info(Cat, $"Offset atualizado: {d.Key} 0x{old:X} → 0x{off:X}");
                    changed++;
                }

                Off.Set(d.Key, off);
                applied++;
            }
            else
            {
                missing++;
                Logger.Warn(Cat, $"{d.Class}.{d.Property} não encontrado no dump; mantendo 0x{Off.Get(d.Key):X}.");
            }
        }

        Logger.Info(Cat, $"Dump importado em {sw.ElapsedMilliseconds} ms: {applied} aplicados, {changed} alterados, {missing} ausentes. " +
                         (meta != null ? $"UE={meta.UeVersion} objetos={meta.ObjectCount} dumped_at={meta.DumpedAt}" : "sem meta"));
        return applied;
    }

    private static string? ExtractName(string line)
    {
        const string key = "\"name\":\"";
        int i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0 || i > 64) return null; // "name" é o 2º campo da linha
        int start = i + key.Length;
        int end = line.IndexOf('"', start);
        return end > start ? line.Substring(start, end - start) : null;
    }

    private static DumpMeta? ParseMeta(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            return new DumpMeta
            {
                UeVersion = r.TryGetProperty("ue_version", out var v) ? v.GetInt32() : 0,
                ModuleBase = Hex(r, "module_base"),
                GWorld = Hex(r, "gworld"),
                GNames = Hex(r, "gnames"),
                GObjects = Hex(r, "gobjects"),
                ObjectCount = r.TryGetProperty("object_count", out var oc) ? oc.GetInt64() : 0,
                DumpedAt = r.TryGetProperty("dumped_at", out var da) ? da.GetString() ?? "" : "",
            };
        }
        catch (Exception ex)
        {
            Logger.Warn(Cat, $"Linha meta inválida: {ex.Message}");
            return null;
        }
    }

    private static ulong Hex(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String) return 0;
        string s = e.GetString() ?? "";
        try { return Convert.ToUInt64(s, 16); } catch { return 0; }
    }
}
