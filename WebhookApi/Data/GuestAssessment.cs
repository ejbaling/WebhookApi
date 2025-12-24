using System;

namespace WebhookApi.Data
{
    public class GuestAssessment
    {
        public int Id { get; set; }
        public int GuestMessageId { get; set; }
        public RedwoodIloilo.Common.Entities.GuestMessage? GuestMessage { get; set; }
        public bool IsGood { get; set; }
        public double Score { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public DateTime EvaluatedAt { get; set; } = DateTime.UtcNow;
    }
}
