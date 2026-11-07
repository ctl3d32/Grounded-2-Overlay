using System.Text.Json;
using G2Overlay.Memory;
using G2Overlay.Utils;

namespace G2Overlay.PatternScanner;

/// <summary>Endereços absolutos das globais da engine.</summary>
public sealed class GlobalAddresses
{
    public ulong GWorld;
    public ulong GNames;
    public ulong GObjects;
    public string Source = "";

    public override string ToString() =>
        $"GWorld=0x{GWorld:X} GNames=0x{GNames:X} GObjects=0x{GObjects:X} ({Source})";
}

/// <summary>
/// Resolve GWorld / GNames / GObjects via pattern scanning, com cache por build do executável
/// (TimeDateStamp + SizeOfImage). Uma atualização do jogo muda a chave e força nova varredura.
/// </summary>
public sealed class SignatureResolver
{
    private const string Cat = "PatternScan";

    private sealed class CacheFile
    {
        public string BuildKey { get; set; } = "";
        public Dictionary<string, string> Rvas { get; set; } = new();
        public DateTime SavedAt { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly ProcessMemory _mem;
    private readonly string _signaturesPath;
    private readonly string _cachePath;

    public SignatureResolver(ProcessMemory mem, string configDir)
    {
        _mem = mem;
        _signaturesPath = Path.Combine(configDir, "signatures.json");
        _cachePath = Path.Combine(configDir, "scan_cache.json");
    }

    public List<Signature> LoadSignatures()
    {
        List<Signature>? list = null;
        try
        {
            if (File.Exists(_signaturesPath))
            {
                var file = JsonSerializer.Deserialize<SignatureFile>(File.ReadAllText(_signaturesPath));
                list = file?.Signatures;
                Logger.Info(Cat, $"{list?.Count ?? 0} assinaturas carregadas de {_signaturesPath} (build: {file?.GameBuild}).");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(Cat, $"signatures.json inválido; usando assinaturas internas.", ex);
            list = null;
        }

        if (list == null || list.Count == 0)
        {
            list = DefaultSignatures.Create();
            Logger.Info(Cat, $"Usando {list.Count} assinaturas internas ({DefaultSignatures.Build}).");
            try
            {
                if (!File.Exists(_signaturesPath))
                {
                    File.WriteAllText(_signaturesPath, JsonSerializer.Serialize(
                        new SignatureFile { GameBuild = DefaultSignatures.Build, Signatures = list }, Json));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(Cat, $"Não foi possível gravar signatures.json: {ex.Message}");
            }
        }

        var valid = new List<Signature>();
        foreach (var s in list)
        {
            try
            {
                s.Compiled = Pattern.Parse(s.Pattern);
                if (s.DisplacementOffset < 0 || s.DisplacementOffset + 4 > s.Compiled.Length || s.InstructionEnd <= s.DisplacementOffset)
                    throw new FormatException("DisplacementOffset/InstructionEnd fora do padrão.");
                valid.Add(s);
                Logger.Debug(Cat, $"AOB [{s.Target}] {s.Pattern} disp@{s.DisplacementOffset} end@{s.InstructionEnd}");
            }
            catch (Exception ex)
            {
                Logger.Error(Cat, $"Assinatura inválida ignorada ({s}): {ex.Message}");
            }
        }

        return valid;
    }

    /// <summary>
    /// Retorna os candidatos (endereços absolutos) por global, em ordem de prioridade.
    /// </summary>
    public Dictionary<string, List<ulong>> FindCandidates(PeInfo pe, bool allowCache, out bool fromCache)
    {
        fromCache = false;
        if (allowCache && TryLoadCache(pe, out var cached))
        {
            fromCache = true;
            return cached;
        }

        var sigs = LoadSignatures();
        var scanner = new ModuleScanner(_mem);
        var matches = scanner.Scan(pe, sigs);

        var result = new Dictionary<string, List<ulong>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sig in sigs)
        {
            var hits = matches[sig];
            if (hits.Count == 0)
            {
                Logger.Warn(Cat, $"AOB não encontrado [{sig.Target}] {sig.Pattern}");
                continue;
            }

            if (hits.Count > 1)
            {
                Logger.Warn(Cat, $"AOB ambíguo ({hits.Count}+ ocorrências) ignorado [{sig.Target}] {sig.Pattern}");
                continue;
            }

            ulong at = hits[0];
            int disp = _mem.Read<int>(at + (ulong)sig.DisplacementOffset);
            ulong target = (ulong)((long)at + sig.InstructionEnd + disp);
            Logger.Info(Cat, $"AOB encontrado [{sig.Target}] em RVA 0x{at - _mem.ModuleBase:X} → global RVA 0x{target - _mem.ModuleBase:X} ({sig.Pattern})");

            if (!result.TryGetValue(sig.Target, out var list))
                result[sig.Target] = list = new List<ulong>();
            if (!list.Contains(target)) list.Add(target);
            else Logger.Debug(Cat, $"  confirmado por assinatura adicional.");
        }

        foreach (var kv in result)
        {
            if (kv.Value.Count > 1)
                Logger.Warn(Cat, $"{kv.Key}: assinaturas discordam ({string.Join(", ", kv.Value.Select(v => $"0x{v - _mem.ModuleBase:X}"))}). A validação escolherá.");
        }

        return result;
    }

    private bool TryLoadCache(PeInfo pe, out Dictionary<string, List<ulong>> cand)
    {
        cand = new Dictionary<string, List<ulong>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_cachePath)) return false;
            var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath));
            if (c == null || c.BuildKey != pe.BuildKey)
            {
                Logger.Info(Cat, $"Cache de scan descartado (build mudou: {c?.BuildKey} → {pe.BuildKey}).");
                return false;
            }

            foreach (var kv in c.Rvas)
            {
                ulong rva = Convert.ToUInt64(kv.Value, 16);
                cand[kv.Key] = new List<ulong> { _mem.ModuleBase + rva };
            }

            Logger.Info(Cat, $"Globais carregadas do cache (build {pe.BuildKey}, salvo em {c.SavedAt:u}).");
            return cand.Count == 3;
        }
        catch (Exception ex)
        {
            Logger.Warn(Cat, $"Cache de scan inválido: {ex.Message}");
            return false;
        }
    }

    public void SaveCache(PeInfo pe, GlobalAddresses g)
    {
        try
        {
            var c = new CacheFile
            {
                BuildKey = pe.BuildKey,
                SavedAt = DateTime.UtcNow,
                Rvas = new Dictionary<string, string>
                {
                    ["GWorld"] = $"0x{g.GWorld - _mem.ModuleBase:X}",
                    ["GNames"] = $"0x{g.GNames - _mem.ModuleBase:X}",
                    ["GObjects"] = $"0x{g.GObjects - _mem.ModuleBase:X}",
                },
            };
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(c, Json));
        }
        catch (Exception ex)
        {
            Logger.Warn(Cat, $"Não foi possível salvar o cache de scan: {ex.Message}");
        }
    }
}
