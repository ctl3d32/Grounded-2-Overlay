using System.Buffers;
using System.Buffers.Binary;
using G2Overlay.Memory;
using G2Overlay.Utils;

namespace G2Overlay.PatternScanner;

public sealed class PeSection
{
    public string Name = "";
    public uint VirtualAddress;
    public uint VirtualSize;
    public uint Characteristics;
    public bool IsExecutable => (Characteristics & 0x20000000) != 0 || (Characteristics & 0x00000020) != 0;
}

public sealed class PeInfo
{
    public uint TimeDateStamp;
    public uint SizeOfImage;
    public List<PeSection> Sections = new();
    public string BuildKey => $"{TimeDateStamp:X8}-{SizeOfImage:X8}";
}

/// <summary>
/// Varre as seções executáveis do módulo principal diretamente na memória do processo,
/// em blocos (sem copiar o módulo inteiro para a RAM do overlay).
/// </summary>
public sealed class ModuleScanner
{
    private const string Cat = "PatternScan";
    private const int ChunkSize = 8 * 1024 * 1024;

    private readonly ProcessMemory _mem;

    public ModuleScanner(ProcessMemory mem) => _mem = mem;

    public PeInfo? ReadPeInfo()
    {
        var header = new byte[0x1000];
        if (!_mem.ReadBytes(_mem.ModuleBase, header))
        {
            Logger.Error(Cat, "Não foi possível ler o cabeçalho PE do módulo.");
            return null;
        }

        if (header[0] != 'M' || header[1] != 'Z')
        {
            Logger.Error(Cat, "Cabeçalho MZ inválido.");
            return null;
        }

        int e = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3C));
        if (e <= 0 || e > 0x800 || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(e)) != 0x00004550)
        {
            Logger.Error(Cat, "Assinatura PE inválida.");
            return null;
        }

        var info = new PeInfo();
        ushort numSections = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(e + 6));
        info.TimeDateStamp = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(e + 8));
        ushort optSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(e + 20));
        int opt = e + 24;
        info.SizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(opt + 56));

        int secTable = opt + optSize;
        for (int i = 0; i < numSections && secTable + 40 * (i + 1) <= header.Length; i++)
        {
            int s = secTable + 40 * i;
            var name = System.Text.Encoding.ASCII.GetString(header, s, 8).TrimEnd('\0');
            info.Sections.Add(new PeSection
            {
                Name = name,
                VirtualSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(s + 8)),
                VirtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(s + 12)),
                Characteristics = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(s + 36)),
            });
        }

        Logger.Info(Cat, $"PE: TimeDateStamp=0x{info.TimeDateStamp:X8} SizeOfImage=0x{info.SizeOfImage:X} Seções={info.Sections.Count}");
        foreach (var sec in info.Sections)
            Logger.Debug(Cat, $"  {sec.Name,-8} RVA=0x{sec.VirtualAddress:X8} Size=0x{sec.VirtualSize:X8} Exec={sec.IsExecutable}");
        return info;
    }

    /// <summary>
    /// Procura todas as assinaturas numa única passada pelas seções executáveis.
    /// Retorna, por assinatura, a lista de endereços absolutos de match (no máximo 4 cada).
    /// </summary>
    public Dictionary<Signature, List<ulong>> Scan(PeInfo pe, IReadOnlyList<Signature> signatures)
    {
        var result = new Dictionary<Signature, List<ulong>>();
        foreach (var s in signatures) result[s] = new List<ulong>();
        if (signatures.Count == 0) return result;

        int maxLen = signatures.Max(s => s.Compiled!.Length);
        int overlap = maxLen - 1;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + overlap);
        var local = new List<int>(8);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long scanned = 0;

        try
        {
            foreach (var sec in pe.Sections.Where(s => s.IsExecutable))
            {
                ulong secStart = _mem.ModuleBase + sec.VirtualAddress;
                long secLen = sec.VirtualSize;
                for (long off = 0; off < secLen; off += ChunkSize)
                {
                    int body = (int)Math.Min(ChunkSize, secLen - off);
                    int readLen = (int)Math.Min(body + overlap, secLen - off);
                    var span = buffer.AsSpan(0, readLen);
                    if (!_mem.ReadBytes(secStart + (ulong)off, span))
                    {
                        // Página não legível: tenta ler em blocos de 4 KB para não perder o bloco inteiro.
                        ReadPagewise(secStart + (ulong)off, span);
                    }

                    scanned += body;
                    foreach (var sig in signatures)
                    {
                        var list = result[sig];
                        if (list.Count >= 4) continue;
                        local.Clear();
                        sig.Compiled!.FindAll(span, 0, body, local, 4 - list.Count);
                        foreach (int idx in local) list.Add(secStart + (ulong)off + (ulong)idx);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        Logger.Info(Cat, $"Varredura concluída: {scanned / (1024 * 1024)} MB em {sw.ElapsedMilliseconds} ms.");
        return result;
    }

    private void ReadPagewise(ulong address, Span<byte> dest)
    {
        dest.Clear();
        for (int p = 0; p < dest.Length; p += 0x1000)
        {
            int len = Math.Min(0x1000, dest.Length - p);
            _mem.ReadBytes(address + (ulong)p, dest.Slice(p, len));
        }
    }
}
