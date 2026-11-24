using System.Text.Json;

namespace AiGym;

static class GymComparisonEvidence
{
    public static FileStream Begin(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path + ".lease", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }

    public static JsonDocument Open(string path)
    {
        using var evidence = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonDocument.Parse(evidence);
    }

    public static string DisplayState(string path, string? state)
    {
        if (state != "Running") return state ?? "Unknown";

        // A live writer holds this lease until it saves the terminal observation.
        // Legacy evidence has no lease and remains readable as an interrupted run.
        try
        {
            using var lease = new FileStream(path + ".lease", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException error) when (error.HResult == unchecked((int)0x80070020))
        {
            return "Running";
        }
        catch (FileNotFoundException) { }
        return "Interrupted";
    }
}
