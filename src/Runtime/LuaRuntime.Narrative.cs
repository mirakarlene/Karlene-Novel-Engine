using System.Text.Json;
using NLua;

namespace KNE;

public sealed partial class LuaRuntime
{
    private readonly Dictionary<string, CharacterProfile> characters = new(StringComparer.Ordinal);

    private void InitializeNarrative()
    {
        characters.Clear();
        Register(nameof(RegisterCharacterForLua), "kne_register_character");
        Register(nameof(PrepareDialogueForLua), "kne_prepare_dialogue");
        Register(nameof(ClearNarrativeText), "kne_clear_remembered_text");
        lua!.DoString(NarrativeRuntimeLua.Source, "KNE narrative");
    }

    /// <summary>Clears the displayed text without adding a user-input stop.</summary>
    public void ClearNarrativeText()
    {
        lock (stateLock) story = story with { Speaker = "", Text = "", Character = null, TextEffect = null, RemainingSeconds = null, EraseProgress = 0 };
    }

    /// <summary>Validates and registers a profile, returning failures as Lua-safe data.</summary>
    public string RegisterCharacterForLua(string json)
    {
        try
        {
            var character = ReadNarrative<CharacterProfile>(json);
            character.Validate();
            if (characters.TryGetValue(character.Id, out var previous) && previous != character)
                throw new ArgumentException("Персонаж с таким ID уже зарегистрирован: " + character.Id);
            characters[character.Id] = character;
            return "";
        }
        catch (Exception error) when (error is ArgumentException or JsonException) { return "register_character: " + error.Message; }
    }

    /// <summary>Returns resolved speaker, immutable profile JSON, effect JSON and an error string.</summary>
    public string[] PrepareDialogueForLua(string speaker, string? characterId, string? effectJson)
    {
        try
        {
            CharacterProfile? character = null;
            if (!string.IsNullOrEmpty(characterId) && !characters.TryGetValue(characterId, out character))
                throw new ArgumentException("Персонаж не зарегистрирован: " + characterId);
            TextEffectSettings? effect = null;
            if (!string.IsNullOrEmpty(effectJson)) { effect = ReadNarrative<TextEffectSettings>(effectJson); effect.Validate(); }
            return [character?.Name ?? speaker, character is null ? "" : JsonSerializer.Serialize(character, EngineSettings.JsonOptions),
                effect is null ? "" : JsonSerializer.Serialize(effect, EngineSettings.JsonOptions), ""];
        }
        catch (Exception error) when (error is ArgumentException or JsonException) { return ["", "", "", "kne_say: " + error.Message]; }
    }

    private static T ReadNarrative<T>(string json)
    {
        if (json is null || json.Length > 32768) throw new ArgumentException("Нужна JSON-конфигурация длиной до 32768 символов.");
        return JsonSerializer.Deserialize<T>(json, NarrativeValidation.JsonOptions) ?? throw new ArgumentException("Конфигурация не может быть null.");
    }

    private static StoryFrame ReadNarrativeFrame(StoryFrame frame, LuaTable value)
    {
        CharacterProfile? character = null;
        TextEffectSettings? effect = null;
        if (value["character"] is string characterJson && characterJson.Length > 0) character = ReadNarrative<CharacterProfile>(characterJson);
        if (value["effect"] is string effectJson && effectJson.Length > 0) effect = ReadNarrative<TextEffectSettings>(effectJson);
        return frame with
        {
            Character = character, TextEffect = effect,
            PresentationId = Convert.ToInt64(value["presentation_id"] ?? 0),
            RemainingSeconds = value["remaining"] is { } remaining ? Convert.ToDouble(remaining) : null,
            IsSkippable = value["skippable"] as bool? != false,
            EraseProgress = value["erase_progress"] is { } progress ? Convert.ToDouble(progress) : 0
        };
    }
}
