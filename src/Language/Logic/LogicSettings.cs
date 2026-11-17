namespace KNE;

/// <summary>A typed literal, state reference or named-list reference, without executable source text.</summary>
public sealed record LogicOperand(string Kind = "string", string Value = "");

/// <summary>An editable, nested Boolean group. Operators short-circuit from left to right.</summary>
public sealed record LogicCondition
{
    public string Operator { get; set; } = "compare";
    public LogicOperand A { get; set; } = new("variable", "score");
    public LogicOperand B { get; set; } = new("number", "0");
    public string Comparison { get; set; } = "==";
    public List<LogicCondition> Children { get; set; } = [];
}

/// <summary>Operands shared by list, text, conversion and variable commands.</summary>
public sealed record LogicSettings
{
    public string List { get; set; } = "inventory";
    public string OtherList { get; set; } = "loot";
    public string InitialValues { get; set; } = "[]";
    public LogicOperand A { get; set; } = new("string", "");
    public LogicOperand B { get; set; } = new("string", "");
    public LogicOperand C { get; set; } = new("string", "");
    public LogicOperand Index { get; set; } = new("number", "0");
    public string Conversion { get; set; } = "number";
    public LogicCondition Condition { get; set; } = new() { Operator = "and", Children = [new(), new()] };
}
