namespace KNE;

internal sealed record TriggerBlock(string Event, string Title, string Description);

internal static class TriggerBlocks
{
    internal static readonly IReadOnlyDictionary<ScriptNodeKind, TriggerBlock> All = new Dictionary<ScriptNodeKind, TriggerBlock>
    {
        [ScriptNodeKind.TriggerKey] = new("key", "При клике на клавишу", "Выполнить ветку при нажатии выбранной клавиши."),
        [ScriptNodeKind.TriggerButton] = new("button", "При клике на кнопку", "Выполнить ветку при нажатии игровой кнопки с заданным идентификатором."),
        [ScriptNodeKind.TriggerHotspot] = new("hotspot", "При клике на зону на экране", "Выполнить ветку при щелчке внутри прямоугольной зоны."),
        [ScriptNodeKind.TriggerTextClosed] = new("text_closed", "При закрытии текстового окна", "Отреагировать на скрытие текстового окна или очистку текста."),
        [ScriptNodeKind.TriggerCombination] = new("key", "При комбинации нажатий", "Выполнить ветку при одновременном нажатии клавиш, например Ctrl+Shift+E."),
        [ScriptNodeKind.TriggerSequence] = new("sequence", "Последовательность", "Распознать отдельные нажатия клавиш в заданном порядке."),
        [ScriptNodeKind.TriggerPointerMove] = new("pointer_move", "Движение курсора", "Выполнить ветку, когда курсор перемещается внутри окна игры."),
        [ScriptNodeKind.TriggerPointerEnterZone] = new("pointer_enter_zone", "Курсор в зоне", "Выполнить ветку, когда курсор входит в указанную область."),
        [ScriptNodeKind.TriggerPointerLeave] = new("pointer_leave", "Курсор за пределами окна", "Выполнить ветку в момент выхода курсора из окна игры."),
        [ScriptNodeKind.TriggerDoubleClick] = new("double_click", "Двойной клик", "Распознать двойной щелчок выбранной кнопкой мыши."),
        [ScriptNodeKind.TriggerLongPress] = new("long_press", "Долгое нажатие", "Выполнить ветку, когда клавишу или кнопку мыши удерживают заданное время."),
        [ScriptNodeKind.TriggerWheel] = new("wheel", "Прокрутка колеса", "Отреагировать на прокрутку колеса мыши в выбранном направлении."),
        [ScriptNodeKind.TriggerFocus] = new("focus", "Фокус окна", "Отреагировать на получение или потерю фокуса окном игры."),
        [ScriptNodeKind.TriggerResize] = new("resize", "Изменение размера окна", "Отреагировать на изменение размеров окна игры."),
        [ScriptNodeKind.TriggerClipboard] = new("clipboard", "При копировании/вставке", "Отреагировать на команду копирования, вставки или вырезания внутри игры."),
        [ScriptNodeKind.TriggerGesture] = new("gesture", "Жест мыши", "Распознать движение мыши по заданной траектории.")
    };
}
