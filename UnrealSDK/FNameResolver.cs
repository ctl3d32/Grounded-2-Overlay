using System.Text;
using G2Overlay.Memory;
using G2Overlay.Utils;

namespace G2Overlay.UnrealSDK;

/// <summary>
/// Decodificação de FName usando o FNamePool (GNames) da UE 5.x.
///   Handle: Block = ComparisonIndex &gt;&gt; 16, Offset = (ComparisonIndex &amp; 0xFFFF) * Stride(2)
///   FNameEntryHeader (uint16): bIsWide:1 | LowercaseProbeHash:5 | Len:10
/// O layout é validado em tempo de execução: a entrada 0 precisa decodificar para "None".
/// </summary>
public sealed class FNameResolver
{
    private const string Cat = "FName";
    private const int Stride = 2;
    private const int MaxBlocks = 8192;

    private readonly ProcessMemory _mem;
    private readonly Dictionary<int, string> _cache = new(16384);
    private readonly ulong[] _blocks = new ulong[MaxBlocks];
    private ulong _pool;
    private bool _lenShift6 = true; // layout UE5 padrão: Len nos bits 6..15

    public bool IsReady { get; private set; }
    public int CacheSize => _cache.Count;

    public FNameResolver(ProcessMemory mem) => _mem = mem;

    public bool Initialize(ulong namePool)
    {
        _pool = namePool;
        IsReady = false;
        _cache.Clear();

        int[] blockOffsets = { Off.FNamePool_Blocks, 0x10, 0x8, 0x18, 0x40 };
        foreach (int bo in blockOffsets.Distinct())
        {
            foreach (bool shift6 in new[] { true, false })
            {
                Array.Clear(_blocks);
                _lenShift6 = shift6;
                ulong block0 = _mem.ReadPtr(_pool + (ulong)bo);
                if (!ProcessMemory.IsValidPointer(block0)) continue;
                _blocks[0] = block0;

                string n0 = DecodeRaw(0);
                if (n0 != "None") continue;

                if (bo != Off.FNamePool_Blocks)
                    Logger.Warn(Cat, $"FNamePool.Blocks detectado em 0x{bo:X} (padrão era 0x{Off.FNamePool_Blocks:X}).");
                Off.Set("FNamePool.Blocks", bo);
                IsReady = true;
                Logger.Info(Cat, $"FNamePool validado: pool=0x{_pool:X} blocks@+0x{bo:X} header={(shift6 ? "UE5 (Len>>6)" : "alternativo")}. Entrada 0 = \"None\".");
                return true;
            }
        }

        Logger.Error(Cat, $"Não foi possível validar o FNamePool em 0x{_pool:X} (entrada 0 != \"None\").");
        return false;
    }

    public string GetName(FName name)
    {
        string b = GetName(name.ComparisonIndex);
        return name.Number > 0 ? $"{b}_{name.Number - 1}" : b;
    }

    public string GetName(int comparisonIndex)
    {
        if (!IsReady || comparisonIndex < 0) return string.Empty;
        if (_cache.TryGetValue(comparisonIndex, out var s)) return s;
        s = DecodeRaw(comparisonIndex);
        if (s.Length > 0) _cache[comparisonIndex] = s;
        return s;
    }

    /// <summary>Lê o FName de um UObject (NamePrivate).</summary>
    public string GetObjectName(ulong obj)
    {
        if (!ProcessMemory.IsValidObjectPointer(obj)) return string.Empty;
        return _mem.TryRead(obj + (ulong)Off.UObject_NamePrivate, out FName n) ? GetName(n) : string.Empty;
    }

    private unsafe string DecodeRaw(int index)
    {
        int block = index >> 16;
        int offset = (index & 0xFFFF) * Stride;
        if (block >= MaxBlocks) return string.Empty;

        ulong blockPtr = _blocks[block];
        if (blockPtr == 0)
        {
            blockPtr = _mem.ReadPtr(_pool + (ulong)Off.FNamePool_Blocks + (ulong)block * 8);
            if (!ProcessMemory.IsValidPointer(blockPtr)) return string.Empty;
            _blocks[block] = blockPtr;
        }

        ulong entry = blockPtr + (ulong)offset;
        // Lê cabeçalho + até 64 bytes numa só chamada (cobre a maioria dos nomes); se for maior, lê de novo.
        byte* buf = stackalloc byte[2 + 128];
        bool gotBulk = _mem.Read(entry, buf, 2 + 128);
        if (!gotBulk && !_mem.Read(entry, buf, 2)) return string.Empty; // fim do bloco: lê só o cabeçalho

        ushort header = *(ushort*)buf;
        bool wide;
        int len;
        if (_lenShift6)
        {
            wide = (header & 1) != 0;
            len = header >> 6;
        }
        else
        {
            wide = (header & 0x8000) != 0;
            len = header & 0x3FF;
        }

        if (len <= 0 || len > 1024) return string.Empty;

        int bytes = wide ? len * 2 : len;
        if (bytes <= 128 && gotBulk)
        {
            return wide ? new string((char*)(buf + 2), 0, len) : Encoding.Latin1.GetString(buf + 2, len);
        }

        var big = new byte[bytes];
        if (!_mem.ReadBytes(entry + 2, big)) return string.Empty;
        return wide ? Encoding.Unicode.GetString(big) : Encoding.Latin1.GetString(big);
    }
}
