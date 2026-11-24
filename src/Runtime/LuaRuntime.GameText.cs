using System.Globalization;
using System.IO;
using System.Text.Json;
using NLua;

namespace KNE;

public sealed partial class LuaRuntime
{
    private string? presentationLanguage;
    private ProjectTranslations translations = ProjectTranslations.Empty;
    internal StoryFrame DisplayStory
    {
        get
        {
            var frame = Story;
            if (frame.Localized is not { } display) return frame;
            return frame with
            {
                Text = display.Text, Speaker = display.Speaker, Character = display.Character,
                VisiblePrefix = display.VisiblePrefix, VisibleSuffix = display.VisibleSuffix, Reveal = display.Reveal,
                Options = display.Options, ChoiceOptions = display.ChoiceOptions, Input = display.Input,
                InputFeedback = frame.InputFeedback.Length > 0 && display.Input != null ? display.Input.FailureMessage : frame.InputFeedback
            };
        }
    }

    internal void SetPresentationLanguage(string? code)
    {
        if (presentationLanguage == code) return;
        presentationLanguage = code;
        try { translations = ProjectTranslations.Load(project, code); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            translations = ProjectTranslations.Empty;
            PrintToUI("Перевод не загружен: " + error.Message);
        }
    }

    private string TranslateMarkup(GameTextCategory category, string source, string original)
    {
        string translated = translations.Translate(category, source);
        if (translated == source) return original;
        try { return InlineText.ToMarkup(ResolveInline(translated)); }
        catch (InvalidDataException) { return original; }
    }

    private InlineTextDocument ResolveLocalizedInline(GameTextCategory category, string source)
    {
        string translated = translations.Translate(category, source);
        if (translated != source)
        {
            try { return ResolveInline(translated); }
            catch (InvalidDataException) { }
        }
        return ResolveInline(source);
    }

    private StoryFrame LocalizeFrame(StoryFrame frame, LuaTable value)
    {
        if (translations.Values.Count == 0) return frame;
        StoryFrame display = frame;
        try
        {
            if (frame.Kind == "dialogue" && value["source_text"] is string source)
            {
                if (value["source_speaker"] is string speaker)
                {
                    string label = TranslateMarkup(GameTextCategory.Other, speaker, frame.Speaker);
                    display = display with { Speaker = label, Character = frame.Character == null ? null : frame.Character with { Name = label } };
                }

                string text = translations.Translate(GameTextCategory.Dialogue, source);
                if (text != source)
                {
                    int flags = Convert.ToInt32(value["source_flags"]);
                    var localized = InlineText.Reverse(ResolveInline(text), (flags & 2) != 0, (flags & 4) != 0);
                    string markup = InlineText.ToMarkup(localized);
                    int length = StringInfo.ParseCombiningCharacters(localized.Text).Length;
                    int originalLength = Math.Max(1, StringInfo.ParseCombiningCharacters(InlineText.Parse(frame.Text).Text).Length);
                    if (frame.Reveal == null && frame.VisiblePrefix == null && frame.VisibleSuffix == null)
                    {
                        int fullLength = Math.Max(1, StringInfo.ParseCombiningCharacters(ResolveInline(source).Text).Length);
                        if (originalLength < fullLength)
                        {
                            int[] elements = [.. StringInfo.ParseCombiningCharacters(localized.Text), localized.Text.Length];
                            markup = InlineText.ToMarkup(localized, elements[Scale(originalLength, length, fullLength)]);
                        }
                    }
                    display = display with
                    {
                        Text = markup,
                        VisiblePrefix = frame.VisiblePrefix is int prefix ? Scale(prefix, length, originalLength) : null,
                        VisibleSuffix = frame.VisibleSuffix is int suffix ? Scale(suffix, length, originalLength) : null,
                        Reveal = frame.Reveal is { } reveal
                            ? reveal with { First = Scale(reveal.First, length, originalLength), Last = Scale(reveal.Last, length, originalLength) }
                            : null
                    };
                }
            }
            else if (frame.Kind == "choice")
            {
                if (value["source_choice"] is string json && frame.ChoiceOptions is { } states)
                {
                    var request = ReadNarrative<ChoiceRequest>(json);
                    var choices = states.Select((choice, index) => choice with
                    {
                        Text = TranslateMarkup(GameTextCategory.Interface, request.Options[index].Text, choice.Text),
                        Hint = request.Options[index].Lock is { ShowHint: true } rules
                            ? TranslateMarkup(GameTextCategory.Interface, rules.Hint, choice.Hint) : choice.Hint
                    }).ToArray();
                    display = frame with { Text = TranslateMarkup(GameTextCategory.Interface, request.Prompt, frame.Text),
                        Options = choices.Select(choice => choice.Text).ToArray(), ChoiceOptions = choices };
                }
                else if (value["source_text"] is string prompt)
                {
                    using var raw = value["source_options"] as LuaTable;
                    display = frame with
                    {
                        Text = TranslateMarkup(GameTextCategory.Interface, prompt, frame.Text),
                        Options = frame.Options.Select((option, index) => raw?[index + 1] is string text
                            ? TranslateMarkup(GameTextCategory.Interface, text, option) : option).ToArray()
                    };
                }
            }
            else if (frame.Input is { } input && value["source_input"] is string json)
            {
                var request = ReadNarrative<TextInputSettings>(json);
                display = frame with { Input = input with
                {
                    Prompt = TranslateMarkup(GameTextCategory.Interface, request.Prompt, input.Prompt),
                    Placeholder = TranslateMarkup(GameTextCategory.Interface, request.Placeholder, input.Placeholder),
                    FailureMessage = TranslateMarkup(GameTextCategory.Interface, request.FailureMessage, input.FailureMessage)
                } };
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or JsonException)
        {
            PrintToUI("Строка перевода пропущена: " + error.Message);
            return frame;
        }
        return ReferenceEquals(frame, display) ? frame : frame with { Localized = display };
    }

    private static int Scale(int count, int length, int original) =>
        (int)Math.Round(Math.Clamp((double)count / original, 0, 1) * length);
}
