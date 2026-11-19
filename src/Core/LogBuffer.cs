// language: C#, file: src/Core/LogBuffer.cs, target: Windows 10/11, .NET 8
// Bounded memory ring + append-only file. Nothing grows without a ceiling.
using System.Text;

namespace RoutRunner.Core;

public sealed class LogBuffer
{
    private const int MaxLines = 4000;
    private const long MaxFileBytes = 4L * 1024 * 1024;

    private readonly object _sync = new();
    private readonly Queue<string> _lines = new(MaxLines);
    private readonly string? _filePath;
    private StreamWriter? _writer;

    public LogBuffer(string serviceId)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RoutRunner", "logs");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, serviceId + ".log");
            RotateIfNeeded(_filePath);
            _writer = new StreamWriter(new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            _writer = null;
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxFileBytes)
            {
                var backup = path + ".1";
                File.Delete(backup);
                File.Move(path, backup);
            }
        }
        catch
        {
            // log rotation is best-effort
        }
    }

    public void Write(string line)
    {
        lock (_sync)
        {
            if (_lines.Count >= MaxLines) _lines.Dequeue();
            _lines.Enqueue(line);
            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                _writer = null;
            }
        }
    }

    public string[] Snapshot()
    {
        lock (_sync)
        {
            return _lines.ToArray();
        }
    }
}
