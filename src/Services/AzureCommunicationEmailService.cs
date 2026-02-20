using Azure;
using Azure.Communication.Email;
using Common.Notifications.Function.Interfaces;
using Common.Notifications.Function.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Common.Notifications.Function.Services;

public class AzureCommunicationEmailService : IEmailService
{
    private readonly EmailClient _emailClient;
    private readonly string _senderAddress;
    private readonly ILogger<AzureCommunicationEmailService> _logger;

    public AzureCommunicationEmailService(
        IConfiguration configuration,
        ILogger<AzureCommunicationEmailService> logger)
    {
        _logger = logger;

        var connectionString = configuration["AzureCommunicationServices:ConnectionString"]
            ?? throw new InvalidOperationException("Azure Communication Services connection string not configured");

        _senderAddress = configuration["AzureCommunicationServices:SenderAddress"]
            ?? throw new InvalidOperationException("Azure Communication Services sender address not configured");

        _emailClient = new EmailClient(connectionString);
    }

    public async Task SendEmailAsync(Models.EmailMessage emailMessage, CancellationToken cancellationToken = default)
    {
        try
        {
            if (emailMessage.To == null || emailMessage.To.Count == 0)
                throw new ArgumentException("At least one recipient is required", nameof(emailMessage));

            var emailContent = new EmailContent(emailMessage.Subject)
            {
                Html = emailMessage.HtmlContent
            };

            if (!string.IsNullOrEmpty(emailMessage.PlainTextContent))
                emailContent.PlainText = emailMessage.PlainTextContent;

            // Primary recipients
            var toRecipients = emailMessage.To.Select(email => new EmailAddress(email)).ToList();
            var emailRecipients = new EmailRecipients(toRecipients);

            // CC recipients
            if (emailMessage.Cc != null && emailMessage.Cc.Count > 0)
            {
                foreach (var email in emailMessage.Cc)
                    emailRecipients.CC.Add(new EmailAddress(email));
            }

            // BCC recipients
            if (emailMessage.Bcc != null && emailMessage.Bcc.Count > 0)
            {
                foreach (var email in emailMessage.Bcc)
                    emailRecipients.BCC.Add(new EmailAddress(email));
            }

            var azureEmailMessage = new Azure.Communication.Email.EmailMessage(_senderAddress, emailRecipients, emailContent);

            var recipientsInfo = $"To: {string.Join(", ", emailMessage.To)}";

            if (emailMessage.Cc?.Count > 0)
                recipientsInfo += $", Cc: {string.Join(", ", emailMessage.Cc)}";
            if (emailMessage.Bcc?.Count > 0)
                recipientsInfo += $", Bcc: {emailMessage.Bcc.Count} recipient(s)";

            _logger.LogInformation(
                "Sending email - Subject: {Subject}, Recipients: {Recipients}, CorrelationId: {CorrelationId}", 
                emailMessage.Subject, 
                recipientsInfo,
                emailMessage.CorrelationId ?? "N/A");

            EmailSendOperation emailSendOperation = await _emailClient.SendAsync(
                                                                            WaitUntil.Started,
                                                                            azureEmailMessage,
                                                                            cancellationToken);

            _logger.LogInformation(
                "Email sent successfully. Operation ID: {OperationId}, CorrelationId: {CorrelationId}", 
                emailSendOperation.Id,
                emailMessage.CorrelationId ?? "N/A");
        }
        catch (RequestFailedException ex) when (ex.Status == 429)
        {
            _logger.LogWarning("Too many requests (429) when sending email. Message will be reprocessed.");
            throw new TooManyRequestsException("Rate limit exceeded", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending email. CorrelationId: {CorrelationId}", emailMessage.CorrelationId ?? "N/A");
            throw;
        }
    }
}

public class TooManyRequestsException : Exception
{
    public TooManyRequestsException(string message, Exception innerException) 
        : base(message, innerException)
    {
    }
}
