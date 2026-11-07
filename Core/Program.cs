using G2Overlay.Config;
using G2Overlay.Utils;

namespace G2Overlay.Core;

public static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        var cfg = ConfigManager.Load();
        if (args.Contains("--console")) cfg.ConsoleLog = true;
        if (args.Contains("--debug")) cfg.LogLevel = LogLevel.Debug;
        if (args.Contains("--trace")) cfg.LogLevel = LogLevel.Trace;

        Logger.Initialize(ConfigManager.LogDirectory, cfg.LogLevel, cfg.ConsoleLog);
        Logger.Info("Init", $"G2Overlay {typeof(Program).Assembly.GetName().Version} — Grounded 2 (UE 5.7) — overlay externo somente-leitura.");
        Logger.Info("Init", $"SO: {Environment.OSVersion} · .NET {Environment.Version} · x64={Environment.Is64BitProcess} · Dir: {AppContext.BaseDirectory}");
        Logger.Info("Init", $"Log: {Logger.LogFilePath}");

        if (!Environment.Is64BitProcess)
        {
            Logger.Fatal("Init", "O overlay precisa rodar como processo x64.");
            Logger.Shutdown();
            return 1;
        }

        _singleInstance = new Mutex(true, "Local\\G2Overlay_SingleInstance", out bool created);
        if (!created)
        {
            Logger.Warn("Init", "Já existe uma instância do overlay em execução. Encerrando.");
            Logger.Shutdown();
            return 1;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Fatal("Init", "Exceção não tratada.", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error("Init", "Exceção de Task não observada.", e.Exception);
            e.SetObserved();
        };

        int code;
        try
        {
            using var app = new OverlayApp(cfg);
            code = app.Run();
        }
        catch (Exception ex)
        {
            Logger.Fatal("Init", "Falha fatal.", ex);
            code = 10;
        }
        finally
        {
            Logger.Info("Init", "Overlay encerrado.");
            Logger.Shutdown();
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }

        return code;
    }
}
