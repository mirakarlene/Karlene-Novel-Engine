using System.IO;

namespace KNE;

internal static class DialogueSpeed
{
    internal const int Default = 40;
    internal const int Maximum = 120;
    internal const string RangeError = "Скорость текста должна быть целым числом от 0 до 120 символов в секунду.";

    internal static int Validate(double speed)
    {
        if (!double.IsFinite(speed) || speed != Math.Truncate(speed) || speed is < 0 or > Maximum)
            throw new InvalidDataException(RangeError);
        return (int)speed;
    }
}
