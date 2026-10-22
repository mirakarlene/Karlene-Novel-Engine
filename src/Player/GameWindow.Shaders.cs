using System.Windows;

namespace KNE;

public partial class GameWindow
{
    private SceneShaderPresenter? shaders;
    private void InitializeShaders()
    {
        if (!project.Files.Any(file => ScriptDocument.IsShaderPath(file.FilePath))) return;
        shaders = new(project, (FrameworkElement)Content, ResolveShaderTarget, error => ShowStartupError(error, "Шейдеры GLSL/HLSL"));
        Loaded += async (_, _) => { if (!closed) await shaders.InitializeAsync(); };
    }
    private FrameworkElement? ResolveShaderTarget(CompiledShaderGraph shader) => shader.Target switch
    {
        "background" => ShaderBackgroundLayer,
        "sprite" => CharacterImage,
        "ui" => ShaderUiLayer,
        "screen" when string.Equals(project.Engine.Screens.Screens.FirstOrDefault(screen => screen.Id == runtime.Visuals.OverlayScreen)?.Name.Trim(), shader.Screen.Trim(), StringComparison.OrdinalIgnoreCase) => runtime.Visuals.ScreenLayer == "main" ? MainScriptScreenViewbox : ScriptScreenViewbox,
        "screen" when string.Equals(project.Engine.Screens.Screens.FirstOrDefault(screen => screen.Id == activeScreen)?.Name.Trim(), shader.Screen.Trim(), StringComparison.OrdinalIgnoreCase) => ScreenViewbox,
        "screen" => null,
        _ => SceneArea
    };
}
