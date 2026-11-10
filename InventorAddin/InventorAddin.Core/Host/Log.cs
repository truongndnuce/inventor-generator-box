using System;
using System.IO;

namespace InventorAddin.Host;

/// <summary>Logs to the file %AppData%\InventorBoxAddin\logs\addin-yyyyMMdd.log. It never throws.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InventorBoxAddin");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = Path.Combine(LogDirectory, $"addin-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Ignore errors while logging; they must not break the add-in.
        }
    }
}
