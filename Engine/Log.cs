namespace Engine;

public enum LogLevel { Info, Warning, Error }

public readonly record struct LogEntry(LogLevel Level, string Message, DateTime Time);

// The engine's print(). Messages go to the console *and* to anyone listening
// on MessageLogged — that's how the editor's Output panel shows them without
// the engine knowing the editor exists.
public static class Log
{
    public static event Action<LogEntry>? MessageLogged;

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    private static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(level, message, DateTime.Now);
        Console.WriteLine($"[{level}] {message}");
        MessageLogged?.Invoke(entry);
    }
}
