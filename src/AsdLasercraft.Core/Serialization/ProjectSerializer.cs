using System.Text.Json;
using System.Text.Json.Serialization;
using AsdLasercraft.Core.Model;

namespace AsdLasercraft.Core.Serialization;

/// <summary>Saves/loads projects as JSON (*.asdl). Images are embedded as base64.</summary>
public static class ProjectSerializer
{
    public const string FileExtension = ".asdl";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(LaserProject project) => JsonSerializer.Serialize(project, Options);

    public static LaserProject FromJson(string json) =>
        JsonSerializer.Deserialize<LaserProject>(json, Options) ?? throw new InvalidDataException("Not an A·S·D Lasercraft project");

    public static void Save(LaserProject project, string path) => File.WriteAllText(path, ToJson(project));

    public static LaserProject Load(string path) => FromJson(File.ReadAllText(path));

    public static LaserProject DeepClone(LaserProject project) => FromJson(ToJson(project));
}

/// <summary>Snapshot-based undo/redo. Simple and robust for a design tool of this size.</summary>
public sealed class UndoStack
{
    private readonly List<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly int _limit;

    public UndoStack(int limit = 100) => _limit = limit;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Call before making a change, with the state as it was.</summary>
    public void Push(LaserProject before)
    {
        _undo.Add(ProjectSerializer.ToJson(before));
        if (_undo.Count > _limit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public LaserProject? Undo(LaserProject current)
    {
        if (!CanUndo) return null;
        _redo.Push(ProjectSerializer.ToJson(current));
        var json = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return ProjectSerializer.FromJson(json);
    }

    public LaserProject? Redo(LaserProject current)
    {
        if (!CanRedo) return null;
        _undo.Add(ProjectSerializer.ToJson(current));
        return ProjectSerializer.FromJson(_redo.Pop());
    }

    public void Clear() { _undo.Clear(); _redo.Clear(); }
}
