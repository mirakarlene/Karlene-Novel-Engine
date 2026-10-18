using System.Windows.Media;

namespace KNE;

/// <summary>Linear-light, full-severity Machado et al. (2009) simulation for selection previews only.</summary>
public static class ColorVisionFilter
{
    // https://www.inf.ufrgs.br/~oliveira/pubs_files/CVD_Simulation/CVD_Simulation.html
    /// <summary>Simulates perception in linear light, clamps the gamut and preserves alpha.</summary>
    /// <param name="color">sRGB preview color.</param><param name="mode">Accessibility mode ID.</param>
    /// <returns>The simulated sRGB color; unknown modes leave it unchanged.</returns>
    public static Color Transform(Color color, string mode)
    {
        mode = BlockAccessibility.Normalize(mode); if (mode == "normal") return color;
        double r = BlockAccessibility.Linear(color.R), g = BlockAccessibility.Linear(color.G), b = BlockAccessibility.Linear(color.B);
        (double R, double G, double B) result = mode switch
        {
            "protanopia" => (.152286*r+1.052583*g-.204868*b, .114503*r+.786281*g+.099216*b, -.003882*r-.048116*g+1.051998*b),
            "deuteranopia" => (.367322*r+.860646*g-.227968*b, .280085*r+.672501*g+.047413*b, -.011820*r+.042940*g+.968881*b),
            "tritanopia" => (1.255528*r-.076749*g-.178779*b, -.078411*r+.930809*g+.147602*b, .004733*r+.691367*g+.303900*b),
            "achromatopsia" => Gray(.2126*r+.7152*g+.0722*b),
            _ => (Math.Clamp((r-.5)*1.4+.5, 0, 1), Math.Clamp((g-.5)*1.4+.5, 0, 1), Math.Clamp((b-.5)*1.4+.5, 0, 1))
        };
        return Color.FromArgb(color.A, Srgb(result.R), Srgb(result.G), Srgb(result.B));
    }
    private static (double, double, double) Gray(double value) => (value, value, value);
    private static byte Srgb(double value) { value = Math.Clamp(value, 0, 1); return (byte)Math.Round((value <= .0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - .055) * 255); }
}
