using G2Overlay.Config;
using G2Overlay.UnrealSDK;
using G2Overlay.Utils;

namespace G2Overlay.Core;

/// <summary>
/// Atualização automática de offsets:
///   1. carrega Config/offsets.json (ou os padrões internos, extraídos do dump UE 5.7);
///   2. se existir um dump *.jsonl mais novo que o usado para gerar offsets.json, reimporta por nome e regrava.
/// </summary>
public static class OffsetBootstrap
{
    private const string Cat = "Offsets";

    public static void Run(OverlayConfig cfg)
    {
        var file = Off.Load(ConfigManager.OffsetsPath);

        if (cfg.AutoImportDump)
        {
            var dirs = ConfigManager.ResolveDirectories(cfg.DumpSearchDirectories).ToList();
            var dump = DumpOffsetImporter.FindNewestDump(dirs);
            if (dump == null)
            {
                Logger.Info(Cat, $"Nenhum dump *.jsonl encontrado em: {string.Join("; ", dirs)}");
            }
            else if (file == null || file.DumpFileTicks != dump.LastWriteTimeUtc.Ticks || !string.Equals(file.DumpFile, dump.Name, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info(Cat, $"Dump novo detectado: {dump.FullName} ({dump.Length / (1024 * 1024)} MB). Importando…");
                int n = DumpOffsetImporter.Import(dump, out var meta);
                if (n > 0)
                    Off.Save(ConfigManager.OffsetsPath, "dump", dump.Name, dump.LastWriteTimeUtc.Ticks, meta?.UeVersion ?? 0);
            }
            else
            {
                Logger.Info(Cat, $"offsets.json já corresponde ao dump {dump.Name}.");
            }
        }

        if (!File.Exists(ConfigManager.OffsetsPath))
            Off.Save(ConfigManager.OffsetsPath, "padrões internos (dump 2026-10-30, UE 5.7)");

        Logger.Info(Cat, $"Offsets em uso ({Off.LoadedFrom}):");
        Off.LogAll();
    }
}
