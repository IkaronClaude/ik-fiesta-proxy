namespace FiestaProxy;

internal static class Log
{
    private static readonly object _lock = new();
    private static StreamWriter? _file;

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Debug(string msg) => Write("DEBUG", msg);

    /// <summary>Also write every line to a file (a Windows service has no console). The previous run's file is kept
    /// as .1, so the log never grows past two runs.</summary>
    public static void ToFile(string path)
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(path)) File.Move(path, path + ".1", overwrite: true);
                _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"cannot open the log file {path}: {e.Message}");
            }
        }
    }

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {level} {msg}";
        lock (_lock)
        {
            Console.WriteLine(line);
            _file?.WriteLine(line);
        }
    }
}
