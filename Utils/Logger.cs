using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace G2Overlay.Utils;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5,
}

/// <summary>
/// Logger assíncrono: as chamadas apenas enfileiram texto; uma thread de baixa prioridade grava em disco.
/// Um arquivo por execução em ./logs, mantendo os N mais recentes.
/// </summary>
public static class Logger
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 20000);
    private static Thread? _writer;
    private static StreamWriter? _file;
    private static long _dropped;

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;
    public static bool ConsoleEnabled { get; private set; }
    public static string? LogFilePath { get; private set; }

    public static void Initialize(string directory, LogLevel level, bool console, int keepFiles = 10)
    {
        MinLevel = level;
        try
        {
            Directory.CreateDirectory(directory);
            CleanupOld(directory, keepFiles);
            LogFilePath = Path.Combine(directory, $"overlay_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _file = new StreamWriter(
                new FileStream(LogFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // Sem arquivo de log: continua apenas com console (se habilitado).
            _file = null;
            console = true;
            Queue.TryAdd($"[Logger] Falha ao criar arquivo de log: {ex.Message}");
        }

        if (console)
        {
            AllocConsole();
            ConsoleEnabled = true;
        }

        _writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "LogWriter",
            Priority = ThreadPriority.BelowNormal,
        };
        _writer.Start();
    }

    private static void CleanupOld(string directory, int keep)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles("overlay_*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(Math.Max(0, keep - 1));
            foreach (var f in files)
            {
                try { f.Delete(); } catch { /* arquivo em uso */ }
            }
        }
        catch
        {
            // ignorado
        }
    }

    private static void WriterLoop()
    {
        try
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                try
                {
                    _file?.WriteLine(line);
                    if (ConsoleEnabled) Console.WriteLine(line);
                    if (Queue.Count == 0) _file?.Flush();
                }
                catch
                {
                    // Nunca derrubar o processo por causa de log.
                }
            }
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding chamado.
        }
    }

    public static void Shutdown()
    {
        try
        {
            Queue.CompleteAdding();
            _writer?.Join(2000);
            if (_dropped > 0) _file?.WriteLine($"[Logger] {_dropped} linhas descartadas (fila cheia).");
            _file?.Flush();
            _file?.Dispose();
        }
        catch
        {
            // ignorado
        }
    }

    public static bool IsEnabled(LogLevel level) => level >= MinLevel;

    public static void Log(LogLevel level, string category, string message, Exception? ex = null)
    {
        if (level < MinLevel || Queue.IsAddingCompleted) return;

        var sb = new StringBuilder(96 + message.Length);
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"))
          .Append(" [").Append(Tag(level)).Append("] [")
          .Append(category).Append("] ")
          .Append(message);
        if (ex != null)
        {
            sb.AppendLine().Append("    ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
            sb.AppendLine().Append(ex.StackTrace);
            if (ex.InnerException != null)
                sb.AppendLine().Append("    Inner: ").Append(ex.InnerException);
        }

        try
        {
            if (!Queue.TryAdd(sb.ToString())) Interlocked.Increment(ref _dropped);
        }
        catch (InvalidOperationException)
        {
            // fila finalizada
        }
    }

    private static string Tag(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "FTL",
    };

    public static void Trace(string cat, string msg) => Log(LogLevel.Trace, cat, msg);
    public static void Debug(string cat, string msg) => Log(LogLevel.Debug, cat, msg);
    public static void Info(string cat, string msg) => Log(LogLevel.Info, cat, msg);
    public static void Warn(string cat, string msg) => Log(LogLevel.Warn, cat, msg);
    public static void Error(string cat, string msg, Exception? ex = null) => Log(LogLevel.Error, cat, msg, ex);
    public static void Fatal(string cat, string msg, Exception? ex = null) => Log(LogLevel.Fatal, cat, msg, ex);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
}

/// <summary>
/// Evita inundar o log com a mesma mensagem a cada frame.
/// </summary>
public sealed class LogThrottle
{
    private readonly long _intervalTicks;
    private long _last;

    public LogThrottle(TimeSpan interval) => _intervalTicks = (long)(interval.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

    public bool Allow()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _last < _intervalTicks) return false;
        _last = now;
        return true;
    }
}
