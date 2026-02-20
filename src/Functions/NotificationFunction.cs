using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Common.Notifications.Function.Interfaces;
using Common.Notifications.Function.Logging;
using Common.Notifications.Function.Models;
using Common.Notifications.Function.Services;
using Elastic.Apm;
using Elastic.Apm.Api;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Common.Notifications.Function.Functions;

public class NotificationFunction
{
    private readonly ILogger<NotificationFunction> _logger;
    private readonly IEmailService _emailService;
    private readonly IEmailTemplateService _templateService;
    private readonly CorrelationIdEnricher _correlationIdEnricher;

    public NotificationFunction(
        ILogger<NotificationFunction> logger,
        IEmailService emailService,
        IEmailTemplateService templateService,
        CorrelationIdEnricher correlationIdEnricher)
    {
        _logger = logger;
        _emailService = emailService;
        _templateService = templateService;
        _correlationIdEnricher = correlationIdEnricher;
    }

    [Function("ProcessNotificationQueue")]
    public async Task Run(
        [ServiceBusTrigger("notifications-queue", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        // Obtém CorrelationId com ordem de prioridade:
        // 1. Campo nativo do AMQP message
        // 2. Custom Properties
        // 3. Metadata do body (será preenchido após deserialização)
        string? correlationId = message.CorrelationId;

        if (string.IsNullOrEmpty(correlationId) && message.ApplicationProperties.TryGetValue("CorrelationId", out var customCorrelationId))
            correlationId = customCorrelationId?.ToString();

        // Define o CorrelationId no enricher para todos os logs subsequentes
        _correlationIdEnricher.SetCorrelationId(correlationId);

        // Captura a transação atual do APM e adiciona labels
        var currentTransaction = Agent.Tracer.CurrentTransaction;
        if (currentTransaction != null)
        {
            currentTransaction.SetLabel("CorrelationId", correlationId ?? "N/A");
            currentTransaction.SetLabel("MessageId", message.MessageId);
            currentTransaction.SetLabel("DeliveryCount", message.DeliveryCount);
        }

        _logger.LogInformation(
            "Processing message ID: {MessageId}, CorrelationId: {CorrelationId}", 
            message.MessageId, 
            correlationId ?? "N/A");

        try
        {
            var notificationRequest = JsonSerializer.Deserialize<NotificationRequest>(message.Body.ToString());

            if (notificationRequest == null)
            {
                _logger.LogError(
                    "Failed to deserialize message body for message ID: {MessageId}, CorrelationId: {CorrelationId}", 
                    message.MessageId,
                    correlationId ?? "N/A");
                await messageActions.DeadLetterMessageAsync(
                    message, 
                    deadLetterReason: "DeserializationError", 
                    deadLetterErrorDescription: "Could not deserialize message body");
                return;
            }

            // Fallback final: se não veio nem no message nem nas properties, pega do Metadata
            correlationId ??= notificationRequest.Metadata?.CorrelationId;

            // Validação
            var (isValid, errorReason, errorDescription) = ValidateNotificationRequest(notificationRequest);

            if (!isValid)
            {
                _logger.LogError(
                    "Validation failed for message ID: {MessageId}, CorrelationId: {CorrelationId}, Reason: {Reason}", 
                    message.MessageId,
                    correlationId ?? "N/A",
                    errorReason);
                await messageActions.DeadLetterMessageAsync(
                    message, 
                    deadLetterReason: errorReason!, 
                    deadLetterErrorDescription: errorDescription!);
                return;
            }

            // Processar template ou usar subject/body diretos
            string subject, htmlBody;

            if (!string.IsNullOrEmpty(notificationRequest.TemplateId))
            {
                // Usar template
                _logger.LogInformation(
                    "Processing notification with template: {TemplateId}, CorrelationId: {CorrelationId}",
                    notificationRequest.TemplateId,
                    correlationId ?? "N/A");

                try
                {
                    subject = _templateService.GetSubject(notificationRequest.TemplateId, notificationRequest.Parameters);
                    htmlBody = _templateService.GetBody(notificationRequest.TemplateId, notificationRequest.Parameters);
                }
                catch (KeyNotFoundException ex)
                {
                    _logger.LogError(
                        ex,
                        "Template not found: {TemplateId}, MessageId: {MessageId}, CorrelationId: {CorrelationId}",
                        notificationRequest.TemplateId,
                        message.MessageId,
                        correlationId ?? "N/A");

                    await messageActions.DeadLetterMessageAsync(
                        message,
                        deadLetterReason: "TemplateNotFound",
                        deadLetterErrorDescription: $"Template '{notificationRequest.TemplateId}' not found");
                    return;
                }
            }
            else
            {
                // Usar subject e body diretos (modo legado)
                subject = notificationRequest.Subject!;
                htmlBody = notificationRequest.Body!;
            }

            // Converter NotificationRequest para EmailMessage
            var emailMessage = new EmailMessage
            {
                To = notificationRequest.EmailTo,
                Cc = notificationRequest.EmailCc ?? new(),
                Bcc = notificationRequest.EmailBcc ?? new(),
                Subject = subject,
                HtmlContent = htmlBody,
                CorrelationId = correlationId
            };

            // Adiciona labels adicionais ao APM após deserialização
            if (currentTransaction != null)
            {
                currentTransaction.SetLabel("AlertType", notificationRequest.Metadata?.AlertType ?? "N/A");
                currentTransaction.SetLabel("Severity", notificationRequest.Metadata?.Severity ?? "N/A");
                currentTransaction.SetLabel("FieldId", notificationRequest.Metadata?.FieldId ?? 0);
                currentTransaction.SetLabel("RecipientCount", notificationRequest.EmailTo.Count);
                currentTransaction.SetLabel("TemplateId", notificationRequest.TemplateId ?? "Direct");
            }

            _logger.LogInformation(
                "Sending notification - Subject: {Subject}, Recipients: {RecipientCount}, " +
                "AlertType: {AlertType}, Severity: {Severity}, FieldId: {FieldId}, CorrelationId: {CorrelationId}", 
                subject,
                notificationRequest.EmailTo.Count,
                notificationRequest.Metadata?.AlertType ?? "N/A",
                notificationRequest.Metadata?.Severity ?? "N/A",
                notificationRequest.Metadata?.FieldId ?? 0,
                correlationId ?? "N/A");

            // Cria um span customizado para rastreamento do envio de email
            if (Agent.Tracer.CurrentTransaction != null)
            {
                await Agent.Tracer.CurrentTransaction.CaptureSpan(
                    "Send Email via ACS",
                    ApiConstants.TypeExternal,
                    async () => await _emailService.SendEmailAsync(emailMessage),
                    ApiConstants.SubtypeHttp);
            }
            else
            {
                await _emailService.SendEmailAsync(emailMessage);
            }

            await messageActions.CompleteMessageAsync(message);

            _logger.LogInformation(
                "Successfully processed message ID: {MessageId}, CorrelationId: {CorrelationId}", 
                message.MessageId,
                correlationId ?? "N/A");
        }
        catch (TooManyRequestsException ex)
        {
            Agent.Tracer.CurrentTransaction?.CaptureError("Rate limit exceeded", ex.Message, new StackTrace(ex).GetFrames());

            _logger.LogWarning(
                ex, 
                "Rate limit exceeded for message ID: {MessageId}, CorrelationId: {CorrelationId}. Message will be retried.", 
                message.MessageId,
                correlationId ?? "N/A");
            // Abandona a mensagem para que ela volte para a fila e seja reprocessada
            await messageActions.AbandonMessageAsync(message);
        }
        catch (JsonException ex)
        {
            Agent.Tracer.CurrentTransaction?.CaptureException(ex);

            _logger.LogError(
                ex, 
                "Invalid JSON format for message ID: {MessageId}, CorrelationId: {CorrelationId}", 
                message.MessageId,
                correlationId ?? "N/A");
            await messageActions.DeadLetterMessageAsync(
                message, 
                deadLetterReason: "InvalidJsonFormat", 
                deadLetterErrorDescription: ex.Message);
        }
        catch (Exception ex)
        {
            Agent.Tracer.CurrentTransaction?.CaptureException(ex);

            _logger.LogError(
                ex, 
                "Error processing message ID: {MessageId}, CorrelationId: {CorrelationId}", 
                message.MessageId,
                correlationId ?? "N/A");
            // Em caso de erro genérico, abandona a mensagem para retry
            await messageActions.AbandonMessageAsync(message);
        }
    }

    private static (bool IsValid, string? ErrorReason, string? ErrorDescription) ValidateNotificationRequest(NotificationRequest request)
    {
        if (request.EmailTo == null || request.EmailTo.Count == 0)
            return (false, "ValidationError", "No recipients specified in EmailTo");

        // Validar que tem ou TemplateId ou Subject+Body
        if (string.IsNullOrWhiteSpace(request.TemplateId))
        {
            // Modo legado: requer Subject e Body
            if (string.IsNullOrWhiteSpace(request.Subject))
                return (false, "ValidationError", "Subject is required when TemplateId is not provided");

            if (string.IsNullOrWhiteSpace(request.Body))
                return (false, "ValidationError", "Body is required when TemplateId is not provided");
        }
        // Se tem TemplateId, não precisa validar Subject/Body

        return (true, null, null);
    }
}