namespace RedwoodIloilo.Common.Entities;

public class RuleCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ICollection<Rule> Rules { get; set; } = new List<Rule>();
}