using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace KNE;

public sealed partial class ProjectParametersWindow
{
    private readonly MenuTransitionInput storyStartInput = new("StoryStartTransition");
    private readonly MenuTransitionInput mainMenuReturnInput = new("MainMenuReturnTransition");

    private Border MenuSettingsCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Heading("Главное меню"));
        AddField(panel, "Переход при старте истории", storyStartInput);
        AddField(panel, "Переход при выходе в главное меню", mainMenuReturnInput);
        return RuntimeOptionsCard(panel);
    }

    private static Border RuntimeOptionsCard(StackPanel panel)
    {
        ParameterLayout.Compact(panel);
        foreach (var row in panel.Children.OfType<Grid>()) row.Margin = new(0);
        var card = Card(panel);
        card.Padding = new(12, 3, 12, 3);
        return card;
    }

    private sealed class MenuTransitionInput : DockPanel
    {
        private static readonly GraphicsOption<string>[] kinds =
        [
            new("none", "Без перехода"), new("fade", "Fade"), new("dissolve", "Dissolve"), new("pixellate", "Pixellate"),
            new("wipe", "Wipe"), new("slide", "Slide"), new("slideaway", "Slideaway"), new("iris", "Iris")
        ];
        private static readonly GraphicsOption<string>[] directions =
        [
            new("left", "Влево"), new("right", "Вправо"), new("up", "Вверх"), new("down", "Вниз")
        ];
        private static readonly GraphicsOption<string>[] apertures = [new("in", "Из центра"), new("out", "К центру")];

        private readonly ComboBox kind = GraphicsSettingsEditor.Choice(kinds);
        private readonly ComboBox direction = GraphicsSettingsEditor.Choice(directions);
        private readonly TextBox duration = new() { Width = 48, ToolTip = "Длительность в секундах" };

        internal MenuTransitionInput(string name)
        {
            kind.Name = name + "Kind";
            direction.Name = name + "Direction";
            duration.Name = name + "Duration";
            direction.ToolTip = "Направление перехода";
            duration.Margin = new(4, 0, 0, 0);
            direction.Margin = new(4, 0, 0, 0);
            SetDock(duration, Dock.Right);
            SetDock(direction, Dock.Right);
            Children.Add(duration);
            Children.Add(direction);
            Children.Add(kind);
            kind.SelectionChanged += (_, _) => UpdateFields();
        }

        internal MenuTransitionSettings Value
        {
            get
            {
                string selected = (string?)kind.SelectedValue ?? "none";
                double seconds = 1;
                if (selected != "none" && !double.TryParse(duration.Text, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out seconds) &&
                    !double.TryParse(duration.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds))
                    throw new InvalidDataException("Укажите длительность перехода в секундах.");
                var transition = new MenuTransitionSettings { Kind = selected, Direction = (string?)direction.SelectedValue ?? "left", Duration = seconds };
                transition.Validate();
                return transition;
            }
            set
            {
                kind.SelectedValue = value.Kind;
                direction.SelectedValue = value.Direction;
                duration.Text = value.Duration.ToString(CultureInfo.CurrentCulture);
            }
        }

        private void UpdateFields()
        {
            string? selected = kind.SelectedValue as string;
            bool hasDirection = selected is "wipe" or "slide" or "slideaway" or "iris";
            direction.Visibility = hasDirection ? Visibility.Visible : Visibility.Collapsed;
            var options = selected == "iris" ? apertures : directions;
            if (!ReferenceEquals(options, direction.ItemsSource))
            {
                direction.ItemsSource = options;
                direction.SelectedIndex = 0;
            }
            if (direction.SelectedIndex < 0) direction.SelectedIndex = 0;
            duration.Visibility = selected == "none" ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
