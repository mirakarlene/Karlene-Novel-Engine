using System.Security.Cryptography;
using System.Text;

namespace KNE;

/// <summary>Tracks the first interactive presentation of each scene visit, independently of autosave.</summary>
internal sealed class SceneSavePolicy
{
    private readonly HashSet<long> visited = [];
    private long restoredThrough;
    internal static bool AllowManual(SceneRuntimeState? scene) => scene?.Settings.AllowSaves != false;
    internal static bool AllowAuto(SceneRuntimeState? scene) => AllowManual(scene) && scene?.Settings.AllowAutosave != false;
    internal void Reset() { visited.Clear(); restoredThrough = 0; }
    internal void MarkRestored(SceneRuntimeState? scene) { if (scene != null) restoredThrough = Math.Max(restoredThrough, Math.Max(scene.Visit, scene.LastVisit)); }
    internal bool ShouldCreateEntry(SceneRuntimeState? scene, StoryFrame story)
    {
        if (scene == null || story.Kind is not ("dialogue" or "choice" or "input" or "wait" or "text_pause")) return false;
        return scene.Visit > restoredThrough && visited.Add(scene.Visit) && scene.Settings.CheckpointOnEntry && AllowManual(scene);
    }
    internal static string SlotName(SceneRuntimeState scene)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scene.File + "::" + scene.Function)))[..16];
        string label = scene.Settings.Name.Length == 0 ? scene.Function : scene.Settings.Name;
        label = new string(label.Where(c => !char.IsControl(c) && !System.IO.Path.GetInvalidFileNameChars().Contains(c) && c != '.').Take(40).ToArray()).Trim();
        return "Вход — " + label + " — " + hash;
    }
}
