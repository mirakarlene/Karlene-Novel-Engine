using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace KNE;

/// <summary>Builds a deterministic startup order from leading init(integer) declarations.</summary>
public static class StartupScripts
{
    /// <summary>Includes the legacy init script at priority zero and opt-in Lua/KRL files; lower numbers run first.</summary>
    public static IReadOnlyList<StartupScript> Plan(ProjectInfo project)
    {
        string primary = project.InitScriptPath;
        var paths = project.Files.Where(f => ScriptDocument.IsCodePath(f.FilePath)).Select(f => f.FilePath)
            .Append(primary).Distinct(StringComparer.OrdinalIgnoreCase);
        var scripts = new List<StartupScript>();
        foreach (string candidate in paths)
        {
            string path = PathSafety.EnsureInside(project.Path, candidate);
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Скрипт больше 8 МБ: " + path);
            string source = File.ReadAllText(path);
            (int? Priority, string Source) declaration;
            try { declaration = Declaration(source); }
            catch (InvalidDataException ex) { throw new InvalidDataException("Инициализация «" + Path.GetRelativePath(project.Path, path) + "»: " + ex.Message, ex); }
            if (declaration.Priority != null || path.Equals(primary, StringComparison.OrdinalIgnoreCase))
                scripts.Add(new(path, declaration.Priority ?? 0, declaration.Source));
        }
        return scripts.OrderBy(s => s.Priority).ThenBy(s => Path.GetRelativePath(project.Path, s.Path).Replace('\\', '/'), StringComparer.Ordinal).ToArray();
    }

    /// <summary>Reads only the first executable statement, preserving source line numbers when removing init.</summary>
    public static (int? Priority, string Source) Declaration(string source)
    {
        int position = 0;
        while (position < source.Length)
        {
            if (char.IsWhiteSpace(source[position]) || source[position] == '\uFEFF') { position++; continue; }
            if (!source.AsSpan(position).StartsWith("--")) break;
            position += 2;
            var block = Regex.Match(source[position..], @"^\[(=*)\[");
            if (block.Success)
            {
                string close = "]" + block.Groups[1].Value + "]";
                int end = source.IndexOf(close, position + block.Length, StringComparison.Ordinal);
                if (end < 0) return (null, source); // Lua reports the unclosed comment if this file is executed.
                position = end + close.Length;
            }
            else
            {
                int newline = source.IndexOfAny(['\r', '\n'], position);
                position = newline < 0 ? source.Length : newline + 1;
            }
        }
        if (!source.AsSpan(position).StartsWith("init") ||
            (position + 4 < source.Length && (char.IsLetterOrDigit(source[position + 4]) || source[position + 4] == '_')))
            return (null, source);
        var match = Regex.Match(source[position..], @"^init\s*\(\s*([+-]?\d+)\s*\)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int priority) || priority is < -10000 or > 10000)
            throw new InvalidDataException("init должен быть первым оператором файла: init(целое число от -10000 до 10000).");
        char[] result = source.ToCharArray();
        for (int i = position; i < position + match.Length; i++) if (result[i] is not ('\r' or '\n')) result[i] = ' ';
        return (priority, new string(result));
    }
}

/// <summary>A validated startup file with its effective priority and declaration-free Lua source.</summary>
public sealed record StartupScript(string Path, int Priority, string Source);
