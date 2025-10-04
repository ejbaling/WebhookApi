namespace RedwoodIloilo.Common.Entities;

public class GuestMessage
    {
        public int Id { get; set; }
        public required string Message { get; set; }
        public string? Language { get; set; }
        public string? Category { get; set; }
        public string? Sentiment { get; set; }
        public string? ReplySuggestion { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }