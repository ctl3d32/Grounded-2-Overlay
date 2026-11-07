using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using G2Overlay.Utils;

namespace G2Overlay.Memory;

/// <summary>
/// Acesso somente-leitura à memória do processo do jogo (OpenProcess + ReadProcessMemory).
/// Nenhuma escrita é feita no processo alvo.
/// </summary>
public sealed unsafe class ProcessMemory : IDisposable
{
    private const string Cat = "Memory";

    public IntPtr Handle { get; private set; }
    public int ProcessId { get; }
    public ulong ModuleBase { get; }
    public uint ModuleSize { get; }
    public string ModuleName { get; }

    public long ReadCount;
    public long ReadFailures;

    private ProcessMemory(IntPtr handle, int pid, ulong moduleBase, uint moduleSize, string moduleName)
    {
        Handle = handle;
        ProcessId = pid;
        ModuleBase = moduleBase;
        ModuleSize = moduleSize;
        ModuleName = moduleName;
    }

    /// <summary>
    /// Abre o processo e localiza a base do módulo principal via Toolhelp32 (não exige PROCESS_QUERY_INFORMATION).
    /// </summary>
    public static ProcessMemory? Open(int pid, string moduleName)
    {
        IntPtr h = Native.OpenProcess(
            Native.PROCESS_VM_READ | Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.SYNCHRONIZE, false, pid);
        if (h == IntPtr.Zero)
        {
            Logger.Error(Cat, $"OpenProcess falhou para PID {pid} (Win32 erro {Marshal.GetLastWin32Error()}). " +
                              "Se o jogo roda como administrador, execute o overlay como administrador também.");
            return null;
        }

        if (!TryGetModule(pid, moduleName, out ulong baseAddr, out uint size))
        {
            Logger.Error(Cat, $"Módulo '{moduleName}' não encontrado no PID {pid}.");
            Native.CloseHandle(h);
            return null;
        }

        Logger.Info(Cat, $"Processo aberto: PID={pid} Módulo={moduleName} Base=0x{baseAddr:X} Tamanho=0x{size:X}");
        return new ProcessMemory(h, pid, baseAddr, size, moduleName);
    }

    private static bool TryGetModule(int pid, string moduleName, out ulong baseAddr, out uint size)
    {
        baseAddr = 0;
        size = 0;
        // O snapshot pode falhar com ERROR_BAD_LENGTH enquanto o processo ainda está carregando: tenta algumas vezes.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32, pid);
            if (snap == Native.INVALID_HANDLE_VALUE || snap == IntPtr.Zero)
            {
                Thread.Sleep(100);
                continue;
            }

            try
            {
                var me = new Native.MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.MODULEENTRY32W>() };
                if (Native.Module32FirstW(snap, ref me))
                {
                    do
                    {
                        if (string.Equals(me.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
                        {
                            baseAddr = (ulong)me.modBaseAddr;
                            size = me.modBaseSize;
                            return true;
                        }
                    } while (Native.Module32NextW(snap, ref me));
                }
            }
            finally
            {
                Native.CloseHandle(snap);
            }

            Thread.Sleep(100);
        }

        return false;
    }

    public bool IsAlive
    {
        get
        {
            if (Handle == IntPtr.Zero) return false;
            return Native.GetExitCodeProcess(Handle, out uint code) && code == Native.STILL_ACTIVE;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidPointer(ulong p) => p >= 0x10000 && p <= 0x7FFF_FFFF_FFFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidObjectPointer(ulong p) => IsValidPointer(p) && (p & 0x7) == 0;

    public bool Read(ulong address, void* buffer, int size)
    {
        if (size <= 0 || !IsValidPointer(address)) return false;
        ReadCount++;
        if (Native.ReadProcessMemory(Handle, (IntPtr)(long)address, buffer, (nuint)size, out nuint read) && read == (nuint)size)
            return true;
        ReadFailures++;
        return false;
    }

    public bool TryRead<T>(ulong address, out T value) where T : unmanaged
    {
        T tmp = default;
        bool ok = Read(address, &tmp, sizeof(T));
        value = ok ? tmp : default;
        return ok;
    }

    public T Read<T>(ulong address) where T : unmanaged
    {
        TryRead(address, out T v);
        return v;
    }

    public ulong ReadPtr(ulong address) => Read<ulong>(address);

    public bool ReadBytes(ulong address, Span<byte> buffer)
    {
        fixed (byte* p = buffer) return Read(address, p, buffer.Length);
    }

    public bool ReadArray<T>(ulong address, Span<T> destination) where T : unmanaged
    {
        if (destination.Length == 0) return true;
        fixed (T* p = destination) return Read(address, p, destination.Length * sizeof(T));
    }

    /// <summary>Lê uma string ASCII/UTF-16 terminada em zero (máx. maxChars).</summary>
    public string ReadString(ulong address, int maxChars, bool wide)
    {
        if (maxChars <= 0) return string.Empty;
        int bytes = wide ? maxChars * 2 : maxChars;
        Span<byte> buf = new byte[bytes];
        if (!ReadBytes(address, buf)) return string.Empty;
        if (wide)
        {
            var chars = MemoryMarshal.Cast<byte, char>(buf);
            int len = chars.IndexOf('\0');
            return new string(len >= 0 ? chars[..len] : chars);
        }
        else
        {
            int len = buf.IndexOf((byte)0);
            return System.Text.Encoding.ASCII.GetString(len >= 0 ? buf[..len] : buf);
        }
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.CloseHandle(Handle);
            Handle = IntPtr.Zero;
            Logger.Info(Cat, $"Handle do processo {ProcessId} fechado. Leituras={ReadCount} Falhas={ReadFailures}");
        }
    }
}
