using System.Windows;
using System.Windows.Controls;

namespace KNE;

public partial class ScenePreview
{
    private GameplayUiPresenter? gameplayUi;
    private readonly Dictionary<string, Button> previewUiButtons = [];

    private void ApplyGameplayUi(SceneVisuals visuals, StoryFrame story)
    {
        if (project is null) return;
        gameplayUi ??= new(SceneArea, DialoguePanel, SpeakerText, DialogueText, ChoicePanel, previewUiButtons, project.Path, assets);
        foreach (string target in new[] { "next", "save", "load", "auto_save", "pause" })
        {
            bool configured = visuals.UiConfigurations.Any(entry => entry.Target == target);
            if (configured && !previewUiButtons.ContainsKey(target))
            {
                var button = new Button
                {
                    Content = target switch { "next" => "Далее  ›", "save" => "Сохранить", "load" => "Загрузить", "auto_save" => "Автосохранение", _ => "Меню" },
                    IsHitTestVisible = false, Focusable = false, Tag = target
                };
                button.BorderBrush = Brush(project.Engine.Theme.AccentColor);
                previewUiButtons.Add(target, button);
                SceneArea.Children.Add(button);
            }
            if (previewUiButtons.TryGetValue(target, out var existing))
                existing.Visibility = configured && (target != "next" || story.CanAdvance || story.Triggers.Any(trigger => trigger.Matches(new("button", Button: "next")))) ? Visibility.Visible : Visibility.Collapsed;
        }
        gameplayUi.Apply(visuals);
    }

    private void ResetGameplayUi()
    {
        gameplayUi?.Reset(); gameplayUi = null;
        foreach (var button in previewUiButtons.Values) SceneArea.Children.Remove(button);
        previewUiButtons.Clear();
    }
}
