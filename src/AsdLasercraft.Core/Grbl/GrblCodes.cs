using System.Text.RegularExpressions;

namespace AsdLasercraft.Core.Grbl;

/// <summary>What a GRBL error or alarm code means and what to do about it, in plain English.</summary>
public sealed record GrblCodeInfo(string Code, string Meaning, string Fix);

/// <summary>
/// The GRBL 1.1 error and alarm tables with practical fixes for diode lasers. Works offline: the console shows these
/// automatically, and the AI assistant is given them so its answers match the app.
/// </summary>
public static class GrblCodes
{
    public static readonly IReadOnlyDictionary<int, (string Meaning, string Fix)> Errors = new Dictionary<int, (string, string)>
    {
        [1] = ("A G-code word is missing its letter.", "The file or command is malformed. Re-export the G-code from this app."),
        [2] = ("A number was badly formatted.", "Check the command for typos (e.g. a comma used as a decimal point)."),
        [3] = ("The $ command wasn't recognised.", "Check the spelling – e.g. $$ lists settings, $H homes, $X unlocks."),
        [4] = ("A value that must be positive was negative.", "Check the setting or command value."),
        [5] = ("Homing isn't enabled on this machine ($22=0).", "Your laser may not have limit switches. Don't use Home; set the origin by hand instead."),
        [6] = ("Step pulse time must be at least 3 µs.", "Set $0 to 3 or more."),
        [7] = ("Reading settings from EEPROM failed; defaults restored.", "Re-enter your machine's $ settings (from the maker's manual or a saved $$ list)."),
        [8] = ("That $ command only works when the machine is idle.", "Wait for the job or jog to finish, then try again."),
        [9] = ("Commands are locked while in alarm or jogging.", "Clear the alarm with Unlock ($X) or Home ($H), then try again."),
        [10] = ("Soft limits need homing enabled.", "Enable homing ($22=1) before soft limits ($20=1), or turn soft limits off."),
        [11] = ("The line is too long (over 80 characters).", "Re-export the G-code from this app; it keeps lines short."),
        [12] = ("A setting would exceed the maximum step rate.", "Lower the max rate ($110/$111) or steps per mm ($100/$101)."),
        [13] = ("The safety door (or enclosure switch) is open.", "Close the lid or door, then resume."),
        [14] = ("Build info or startup line is too long.", "Shorten the $I or $N text."),
        [15] = ("The jog would go past the machine's travel limits.", "Jog a shorter distance, or check bed size and homing."),
        [16] = ("The jog command was invalid.", "Use the jog buttons in the app; jog commands need $J= with X/Y and F."),
        [17] = ("Laser mode needs PWM output.", "Your firmware isn't built for variable laser power; check the maker's firmware."),
        [20] = ("Unsupported or unknown G-code command.", "The controller doesn't support that command. Export again from this app."),
        [21] = ("Two commands from the same group are on one line.", "Put them on separate lines."),
        [22] = ("Feed rate hasn't been set.", "Add an F value (e.g. F1000) before the first G1/G2/G3 move."),
        [23] = ("The command needs a whole number.", "Check the command value."),
        [24] = ("Two commands on one line both need axis words.", "Split them onto separate lines."),
        [25] = ("A G-code word is repeated on the line.", "Remove the duplicate word."),
        [26] = ("The command needs X/Y values but has none.", "Check the line – something is missing."),
        [27] = ("Invalid line number.", "Remove or fix the N line number."),
        [28] = ("The command is missing a required value.", "Check the line – something is missing."),
        [29] = ("G59.x work coordinates aren't supported.", "Use G54–G59 instead."),
        [30] = ("G53 only works with G0 or G1.", "Change the motion command."),
        [31] = ("Axis words found with no command that uses them.", "Check the line."),
        [32] = ("Arc is missing an axis word in the current plane.", "Check the G2/G3 line."),
        [33] = ("The motion target is invalid.", "Check the coordinates on the line."),
        [34] = ("Arc radius definition is invalid.", "Check the G2/G3 R value."),
        [35] = ("Arc is missing its offset (I/J) words.", "Check the G2/G3 line."),
        [36] = ("Unused value words on the line.", "Remove the extra values."),
        [37] = ("Tool length offset isn't assigned to the right axis.", "Not used on lasers – remove G43.1."),
        [38] = ("Tool number exceeds the maximum.", "Not used on lasers – remove the T word."),
    };

    public static readonly IReadOnlyDictionary<int, (string Meaning, string Fix)> Alarms = new Dictionary<int, (string, string)>
    {
        [1] = ("Hard limit: a limit switch was hit during a move.",
               "Position is lost. Check nothing is in the way, then Unlock ($X) and Home ($H). Make sure the job fits the bed."),
        [2] = ("Soft limit: the job goes outside the machine's travel.",
               "The design is bigger than the work area, or placed too near an edge. Move it or scale it down, check the job origin, then Unlock ($X)."),
        [3] = ("Reset while moving: position may be lost.",
               "Usually after pressing Stop. Unlock ($X), and Home ($H) if your laser has switches, before the next job."),
        [4] = ("Probe failed: the probe wasn't in the expected state.", "Lasers don't normally probe. Check the G-code."),
        [5] = ("Probe failed: no contact within the move.", "Lasers don't normally probe. Check the G-code."),
        [6] = ("Homing failed: homing was reset.", "Let homing finish before pressing anything, then try Home again."),
        [7] = ("Homing failed: the safety door opened during homing.", "Close the lid and Home again."),
        [8] = ("Homing failed: couldn't move off the limit switch.", "Check the switch wiring and increase the pull-off distance ($27)."),
        [9] = ("Homing failed: a limit switch wasn't found.",
               "Check the switches and their wiring, that the belts aren't slipping, and that the max travel ($130/$131) is right."),
        [10] = ("Homing failed: the second switch of a dual-motor axis wasn't found.", "Check both switches on that axis and their wiring."),
    };

    private static readonly Regex CodeRegex = new(@"\b(error|alarm)\s*:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Explains "error:N" or "ALARM:N" found anywhere in the text, or returns null.</summary>
    public static GrblCodeInfo? Explain(string text)
    {
        var m = CodeRegex.Match(text ?? "");
        if (!m.Success) return null;
        int n = int.Parse(m.Groups[2].Value);
        bool alarm = m.Groups[1].Value.Equals("alarm", StringComparison.OrdinalIgnoreCase);
        var table = alarm ? Alarms : Errors;
        string code = alarm ? $"ALARM:{n}" : $"error:{n}";
        return table.TryGetValue(n, out var info)
            ? new GrblCodeInfo(code, info.Meaning, info.Fix)
            : new GrblCodeInfo(code, alarm ? "Unknown alarm." : "Unknown error.", "Check your controller's documentation for this code.");
    }

    /// <summary>One line per code, for giving the assistant the same reference the app uses.</summary>
    public static string ReferenceText() =>
        string.Join("\n", Alarms.Select(a => $"ALARM:{a.Key} – {a.Value.Meaning} Fix: {a.Value.Fix}")
                    .Concat(Errors.Select(e => $"error:{e.Key} – {e.Value.Meaning} Fix: {e.Value.Fix}")));
}
