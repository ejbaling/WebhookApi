namespace RedwoodIloilo.Common.Entities;

public class Rule
{
    public int Id { get; set; }
    public int RuleCategoryId { get; set; }
    public int? PropertyId { get; set; }
    public string RuleText { get; set; } = string.Empty;

    public RuleCategory RuleCategory { get; set; } = null!;
}