namespace FiestaProxy;

internal static class Log
{
    private static readonly object _lock = new();

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Debug(string msg) => Write("DEBUG", msg);

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {level} {msg}";
        lock (_lock) Console.WriteLine(line);
    }
}
