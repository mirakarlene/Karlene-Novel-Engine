namespace KNE;

/// <summary>A numeric expression and its nonnegative relative probability.</summary>
public sealed record WeightedNumber(string Value = "0", double Weight = 1);
/// <summary>A named intermediate expression. Later steps may refer to earlier names.</summary>
public sealed record FormulaStep(string Name = "result", string Expression = "0");
/// <summary>Editable operands for calculation commands. Expressions read numeric state variables.</summary>
public sealed record CalculationSettings
{
    public string A { get; init; } = "0";
    public string B { get; init; } = "1";
    public string C { get; init; } = "0";
    public string D { get; init; } = "1";
    public string T { get; init; } = "0.5";
    public string Operator { get; init; } = "+";
    public string Function { get; init; } = "sin";
    public string Constant { get; init; } = "pi";
    public string FormulaName { get; init; } = "Формула";
    public List<WeightedNumber> Weights { get; init; } = [new("0", .6), new("1", .4)];
    public List<FormulaStep> Steps { get; init; } = [new("result", "0")];
}
