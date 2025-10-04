namespace RedwoodIloilo.Common.Entities;

public class Config
{
    public int Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Key { get; set; } = default!;
    public bool Value { get; set; } = false;
    public string ValueType { get; set; } = "string";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}