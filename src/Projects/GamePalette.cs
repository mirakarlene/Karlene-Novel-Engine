namespace KNE;

/// <summary>Editor color families adapted to the game's existing layout and authored artwork.</summary>
internal static class GamePalette
{
    private static readonly Dictionary<string, string[]> Colors = new()
    {
        ["kne"] = ["#171222", "#514065", "#4C3979", "#F4EFFA", "#241D34", "#E6A5D9"],
        ["light"] = ["#F7F6FA", "#B9C3D2", "#CCDEF9", "#202733", "#FFFFFF", "#934575"],
        ["dark"] = ["#191C24", "#56647B", "#37577F", "#E5EAF3", "#242935", "#E6A5D9"],
        ["rose"] = ["#FCF3F7", "#C9A5BF", "#EACBE3", "#422B42", "#FFF9FC", "#8A3F70"],
        ["ocean"] = ["#10232C", "#416673", "#356275", "#E4F3F6", "#19333E", "#E6AFDA"],
        ["forest"] = ["#172622", "#4C6A5B", "#456957", "#E7F3EC", "#243932", "#E3B1D1"],
        ["amber"] = ["#FBF5EB", "#CAB797", "#E8D6AC", "#45382A", "#FFFCF6", "#8D486F"],
        ["lavender"] = ["#F3EFFB", "#BBA7D1", "#D4C3EB", "#352747", "#FCFAFF", "#7A3F78"],
        ["midnight"] = ["#111729", "#4D608B", "#384F80", "#E9EDFC", "#1C2540", "#E0AEDF"],
        ["cherry"] = ["#2A1422", "#825466", "#724359", "#FBEFF3", "#3B2130", "#EDAFCF"],
        ["sand"] = ["#F8F3E8", "#BCAA8C", "#DDCDAE", "#3F3629", "#FFFCF3", "#8A4966"],
        ["aurora"] = ["#14292E", "#507B79", "#406B69", "#EAF8F2", "#203C42", "#E3B4E7"],
        ["sapphire"] = ["#101E32", "#526F96", "#355A87", "#EDF6FF", "#1A304D", "#BAC6FF"],
        ["peach"] = ["#FFF0E9", "#C79A8C", "#EBC9C0", "#4B302E", "#FFF9F5", "#925070"],
        ["graphite"] = ["#202227", "#676E7B", "#535A68", "#F2F2F5", "#2E3138", "#D3BFE9"],
        ["mint"] = ["#ECF7F1", "#97BAA9", "#BCDACB", "#243F35", "#F7FFFA", "#73507F"],
        ["plum"] = ["#261B32", "#806191", "#694A79", "#F9EFFC", "#392846", "#F0B7DC"],
    };
    internal static void Apply(GameTheme theme, ScreenLayouts screens, string palette)
    {
        if (!Colors.TryGetValue(palette, out var colors)) ThemePalettes.Apply(theme, palette);
        else
        {
            theme.Palette = palette; theme.BackgroundColor = colors[0]; theme.PatternColor = colors[1]; theme.AccentColor = colors[5];
            theme.TextColor = colors[3]; theme.ButtonColor = colors[4]; theme.Dialogue.BackgroundColor = colors[4];
            theme.Dialogue.BorderColor = colors[1]; theme.Dialogue.TextColor = colors[3]; theme.Dialogue.SpeakerColor = colors[5];
            theme.Menu.OverlayColor = colors[0];
        }
        screens.Theme = theme;
    }
}