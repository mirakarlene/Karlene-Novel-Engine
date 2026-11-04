using System.IO;
using System.Text.Json;
using NLua;

namespace KNE;

/// <summary>Removes native/CLR escape routes and keeps managed results and exceptions out of restricted Lua.</summary>
internal static class LuaExecutionPolicy
{
    internal static void Register(Lua lua, object owner, System.Reflection.MethodInfo method, string name)
    {
        var bridge = new GuardedCallback(owner, method);
        using var callback = lua.RegisterFunction(name, bridge, typeof(GuardedCallback).GetMethod(nameof(GuardedCallback.Invoke))!);
        lua.DoString("""
            do
                local raw, pack, textload, raise = _G[NAME], table.pack, load, error
                _G[NAME] = function(...)
                    local packet = assert(textload(raw(pack(...)), 'KNE callback result', 't', {}))()
                    if not packet.ok then raise(packet.problem, 2) end
                    return packet.value
                end
            end
            """.Replace("NAME", JsonSerializer.Serialize(name)), "KNE safe callback");
    }
    private sealed class GuardedCallback(object owner, System.Reflection.MethodInfo method)
    {
        public string Invoke(LuaTable arguments)
        {
            try
            {
                var parameters = method.GetParameters(); var values = new object?[parameters.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    object? value = arguments[i + 1]; var parameter = parameters[i];
                    values[i] = value == null ? parameter.HasDefaultValue ? parameter.DefaultValue : null : parameter.ParameterType.IsInstanceOfType(value) ? value : Convert.ChangeType(value, parameter.ParameterType, System.Globalization.CultureInfo.InvariantCulture);
                }
                object? result = method.Invoke(owner, values);
                return "return {ok=true,value=" + Literal(result) + "}";
            }
            catch (Exception error)
            {
                string problem = (error is System.Reflection.TargetInvocationException { InnerException: { } cause } ? cause : error).Message;
                return "return {ok=false,problem=" + Quote(problem.Length > 8192 ? problem[..8192] : problem) + "}";
            }
        }
        private static string Literal(object? value) => value switch
        {
            null => "nil", string text => Quote(text), bool boolean => boolean ? "true" : "false",
            string[] array => "{" + string.Join(',', array.Select((text, index) => "[" + index + "]=" + Quote(text))) + "}",
            int or long or double or float => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
            _ => throw new InvalidDataException("Этот тип результата недоступен в ограниченном Lua.")
        };
        private static string Quote(string text)
        {
            var result = new System.Text.StringBuilder("\"");
            foreach (char character in text)
                if (character is '\\' or '"') result.Append('\\').Append(character);
                else if (character < 32) result.Append('\\').Append(((int)character).ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
                else result.Append(character);
            return result.Append('"').ToString();
        }
    }
    internal static void Apply(Lua lua, ProjectSecuritySettings policy)
    {
        if (!policy.Restricted) return;
        lua.DoString("""
            do
                local textload, raise = load, error
                function load(source, name, mode, environment)
                    if type(source) ~= 'string' then raise('Only text source is allowed', 2) end
                    return textload(source, name, 't', environment or _G)
                end
                local function denied() raise('Запрещено политикой безопасности проекта', 2) end
                require, dofile, loadfile = denied, denied, denied
                package = nil
                luanet = nil
                import = nil
                CLRPackage = nil
                local safeDebug = { getinfo = debug.getinfo, traceback = debug.traceback, gethook = debug.gethook, sethook = debug.sethook }
                debug = safeDebug
                os.execute, os.remove, os.rename, os.tmpname, os.getenv, os.exit, os.setlocale = denied, denied, denied, denied, denied, denied, denied
                io = nil
            end
            """, "KNE execution policy");
    }
}
