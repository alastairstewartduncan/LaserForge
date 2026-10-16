using System.Globalization;
using AsdLasercraft.Core.Geometry;

namespace AsdLasercraft.Core.Grbl;

/// <summary>A parsed GRBL 1.1 real-time status report, e.g. &lt;Idle|MPos:10.000,5.000,0.000|FS:0,0|WCO:0,0,0&gt;.</summary>
public sealed record GrblStatus(string State, Vec2? MachinePos, Vec2? WorkPos, double Feed, double Spindle)
{
    public static bool TryParse(string line, out GrblStatus status)
    {
        status = new GrblStatus("Unknown", null, null, 0, 0);
        line = line.Trim();
        if (!line.StartsWith('<') || !line.EndsWith('>')) return false;
        var parts = line[1..^1].Split('|');
        string state = parts[0];
        Vec2? mpos = null, wpos = null, wco = null;
        double feed = 0, spindle = 0;
        foreach (var part in parts.Skip(1))
        {
            int colon = part.IndexOf(':');
            if (colon < 0) continue;
            string key = part[..colon], val = part[(colon + 1)..];
            var nums = val.Split(',').Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0).ToArray();
            switch (key)
            {
                case "MPos" when nums.Length >= 2: mpos = new Vec2(nums[0], nums[1]); break;
                case "WPos" when nums.Length >= 2: wpos = new Vec2(nums[0], nums[1]); break;
                case "WCO" when nums.Length >= 2: wco = new Vec2(nums[0], nums[1]); break;
                case "FS" when nums.Length >= 2: feed = nums[0]; spindle = nums[1]; break;
                case "F" when nums.Length >= 1: feed = nums[0]; break;
            }
        }
        if (wpos == null && mpos != null && wco != null) wpos = mpos.Value - wco.Value;
        if (mpos == null && wpos != null && wco != null) mpos = wpos.Value + wco.Value;
        status = new GrblStatus(state, mpos, wpos, feed, spindle);
        return true;
    }
}

public static class GrblErrors
{
    /// <summary>"error:9" → "error:9 – Commands are locked while in alarm or jogging." (full fixes: <see cref="GrblCodes"/>).</summary>
    public static string Describe(string response)
    {
        int colon = response.IndexOf(':');
        if (colon >= 0 && int.TryParse(response[(colon + 1)..], out int code))
            return GrblCodes.Errors.TryGetValue(code, out var info) ? $"error:{code} – {info.Meaning.TrimEnd('.')}" : $"error:{code}";
        return response;
    }
}
