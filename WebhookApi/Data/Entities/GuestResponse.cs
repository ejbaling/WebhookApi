namespace RedwoodIloilo.Common.Entities;

public class GuestResponse
{
    public int Id { get; set; }

    // Foreign key to GuestMessage
    public int GuestMessageId { get; set; }
    public GuestMessage GuestMessage { get; set; } = null!;

    // The actual response text
    public required string Response { get; set; }

    // Optional metadata
    public string? Language { get; set; }
    public string? Sentiment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}