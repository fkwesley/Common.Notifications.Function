using System.ComponentModel.DataAnnotations;

namespace Common.Notifications.Function.Models;

public class NotificationRequest
{
    /// <summary>
    /// Email recipients (primary)
    /// </summary>
    [Required]
    public List<string> EmailTo { get; set; } = new();

    /// <summary>
    /// Email carbon copy recipients
    /// </summary>
    public List<string> EmailCc { get; set; } = new();

    /// <summary>
    /// Email blind carbon copy recipients
    /// </summary>
    public List<string> EmailBcc { get; set; } = new();

    /// <summary>
    /// Template ID for predefined email templates.
    /// If provided, uses template with parameters. Otherwise, uses Subject and Body.
    /// </summary>
    public string? TemplateId { get; set; }

    /// <summary>
    /// Parameters for template replacement (key-value pairs)
    /// Example: { "{orderId}": "12345", "{customerName}": "John Doe" }
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>
    /// Email subject line (required if TemplateId is not provided)
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Email body with detailed explanation (required if TemplateId is not provided)
    /// </summary>
    public string? Body { get; set; }

    /// <summary>
    /// Alert metadata for tracking and categorization
    /// </summary>
    public AlertMetadata Metadata { get; set; } = new();
}

/// <summary>
/// Metadata for alert tracking and categorization
/// </summary>
public class AlertMetadata
{
    /// <summary>
    /// Unique identifier for correlating this alert across systems and logs.
    /// Should be the same CorrelationId from the original request/measurement.
    /// </summary>
    public string CorrelationId { get; set; } = "";

    public string AlertType { get; set; } = string.Empty;
    public int FieldId { get; set; }
    public DateTime DetectedAt { get; set; }
    public string Severity { get; set; } = "Medium"; // Low, Medium, High, Critical
}
