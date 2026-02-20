using Azure.Messaging.ServiceBus;
using Common.Notifications.Function.Functions;
using Common.Notifications.Function.Interfaces;
using Common.Notifications.Function.Logging;
using Common.Notifications.Function.Models;
using Common.Notifications.Function.Services;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using System.Text.Json;

namespace UnitTests.Functions;

public class NotificationFunctionTests
{
    private readonly Mock<ILogger<NotificationFunction>> _loggerMock;
    private readonly Mock<IEmailService> _emailServiceMock;
    private readonly Mock<IEmailTemplateService> _templateServiceMock;
    private readonly Mock<CorrelationIdEnricher> _correlationIdEnricherMock;
    private readonly Mock<ServiceBusMessageActions> _messageActionsMock;
    private readonly NotificationFunction _function;

    public NotificationFunctionTests()
    {
        _loggerMock = new Mock<ILogger<NotificationFunction>>();
        _emailServiceMock = new Mock<IEmailService>();
        _templateServiceMock = new Mock<IEmailTemplateService>();
        _correlationIdEnricherMock = new Mock<CorrelationIdEnricher>();
        _messageActionsMock = new Mock<ServiceBusMessageActions> { CallBase = false };

        _function = new NotificationFunction(
            _loggerMock.Object,
            _emailServiceMock.Object,
            _templateServiceMock.Object,
            _correlationIdEnricherMock.Object
        );
    }

    [Fact]
    public async Task Run_WithValidTemplateRequest_ShouldProcessSuccessfully()
    {
        var correlationId = Guid.NewGuid().ToString();
        var templateId = "HeatStress";

        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "test@example.com" },
            TemplateId = templateId,
            Parameters = new Dictionary<string, string>
            {
                { "{fieldName}", "Campo Teste" },
                { "{temperature}", "35°C" }
            },
            Metadata = new AlertMetadata
            {
                CorrelationId = correlationId,
                AlertType = "HeatStress",
                FieldId = 123,
                Severity = "High"
            }
        };

        var message = CreateServiceBusMessage(notificationRequest, correlationId);

        _templateServiceMock
            .Setup(x => x.GetSubject(templateId, It.IsAny<Dictionary<string, string>>()))
            .Returns("Test Subject");

        _templateServiceMock
            .Setup(x => x.GetBody(templateId, It.IsAny<Dictionary<string, string>>()))
            .Returns("<html>Test Body</html>");

        _emailServiceMock
            .Setup(x => x.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _function.Run(message, _messageActionsMock.Object);

        _emailServiceMock.Verify(
            x => x.SendEmailAsync(
                It.Is<EmailMessage>(e => 
                    e.To.Contains("test@example.com") &&
                    e.Subject == "Test Subject" &&
                    e.HtmlContent == "<html>Test Body</html>" &&
                    e.CorrelationId == correlationId),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _messageActionsMock.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WithDirectSubjectAndBody_ShouldProcessSuccessfully()
    {
        var correlationId = Guid.NewGuid().ToString();

        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "test@example.com" },
            Subject = "Direct Subject",
            Body = "<html>Direct Body</html>",
            Metadata = new AlertMetadata
            {
                CorrelationId = correlationId
            }
        };

        var message = CreateServiceBusMessage(notificationRequest, correlationId);

        _emailServiceMock
            .Setup(x => x.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _function.Run(message, _messageActionsMock.Object);

        _emailServiceMock.Verify(
            x => x.SendEmailAsync(
                It.Is<EmailMessage>(e => 
                    e.Subject == "Direct Subject" &&
                    e.HtmlContent == "<html>Direct Body</html>"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _messageActionsMock.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
        _templateServiceMock.Verify(x => x.GetSubject(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()), Times.Never);
    }

    [Fact]
    public async Task Run_WithInvalidJson_ShouldDeadLetterMessage()
    {
        var correlationId = Guid.NewGuid().ToString();
        var invalidJson = "{ invalid json }";
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(Encoding.UTF8.GetBytes(invalidJson)),
            messageId: Guid.NewGuid().ToString(),
            correlationId: correlationId
        );

        await _function.Run(message, _messageActionsMock.Object);

        _messageActionsMock.Verify(
            x => x.DeadLetterMessageAsync(
                message,
                It.IsAny<Dictionary<string, object>>(),
                It.Is<string>(r => r == "InvalidJsonFormat"),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _emailServiceMock.Verify(x => x.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_WithNoRecipients_ShouldDeadLetterMessage()
    {
        var correlationId = Guid.NewGuid().ToString();

        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string>(),
            Subject = "Test",
            Body = "Test Body",
            Metadata = new AlertMetadata { CorrelationId = correlationId }
        };

        var message = CreateServiceBusMessage(notificationRequest, correlationId);

        await _function.Run(message, _messageActionsMock.Object);

        _messageActionsMock.Verify(
            x => x.DeadLetterMessageAsync(
                message,
                It.IsAny<Dictionary<string, object>>(),
                It.Is<string>(r => r == "ValidationError"),
                It.Is<string>(d => d.Contains("No recipients")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WithMissingSubjectInDirectMode_ShouldDeadLetterMessage()
    {
        var correlationId = Guid.NewGuid().ToString();

        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "test@example.com" },
            Body = "Test Body",
            Metadata = new AlertMetadata { CorrelationId = correlationId }
        };

        var message = CreateServiceBusMessage(notificationRequest, correlationId);

        await _function.Run(message, _messageActionsMock.Object);

        _messageActionsMock.Verify(
            x => x.DeadLetterMessageAsync(
                message,
                It.IsAny<Dictionary<string, object>>(),
                It.Is<string>(r => r == "ValidationError"),
                It.Is<string>(d => d.Contains("Subject is required")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WithMissingBodyInDirectMode_ShouldDeadLetterMessage()
    {
        var correlationId = Guid.NewGuid().ToString();

        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "test@example.com" },
            Subject = "Test Subject",
            Metadata = new AlertMetadata { CorrelationId = correlationId }
        };

        var message = CreateServiceBusMessage(notificationRequest, correlationId);

        await _function.Run(message, _messageActionsMock.Object);

        _messageActionsMock.Verify(
            x => x.DeadLetterMessageAsync(
                message,
                It.IsAny<Dictionary<string, object>>(),
                It.Is<string>(r => r == "ValidationError"),
                It.Is<string>(d => d.Contains("Body is required")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WithTemplateNotFound_ShouldDeadLetterMessage()
    {
        var correlationId = Guid.NewGuid().ToString();
        var templateId = "non-existent-template";

        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "test@example.com" },
            TemplateId = templateId,
            Metadata = new AlertMetadata { CorrelationId = correlationId }
        };

        var message = CreateServiceBusMessage(notificationRequest, correlationId);

        _templateServiceMock
            .Setup(x => x.GetSubject(templateId, It.IsAny<Dictionary<string, string>>()))
            .Throws(new KeyNotFoundException($"Template '{templateId}' not found"));

        await _function.Run(message, _messageActionsMock.Object);

        _messageActionsMock.Verify(
            x => x.DeadLetterMessageAsync(
                message,
                It.IsAny<Dictionary<string, object>>(),
                It.Is<string>(r => r == "TemplateNotFound"),
                It.Is<string>(d => d.Contains(templateId)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WithCcAndBccRecipients_ShouldIncludeAllRecipients()
    {
        var correlationId = Guid.NewGuid().ToString();

        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "to@example.com" },
            EmailCc = new List<string> { "cc@example.com" },
            EmailBcc = new List<string> { "bcc@example.com" },
            Subject = "Test",
            Body = "Test Body",
            Metadata = new AlertMetadata { CorrelationId = correlationId }
        };

        var message = CreateServiceBusMessage(notificationRequest, correlationId);

        _emailServiceMock
            .Setup(x => x.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _function.Run(message, _messageActionsMock.Object);

        _emailServiceMock.Verify(
            x => x.SendEmailAsync(
                It.Is<EmailMessage>(e => 
                    e.To.Contains("to@example.com") &&
                    e.Cc.Contains("cc@example.com") &&
                    e.Bcc.Contains("bcc@example.com")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WithCorrelationIdInApplicationProperties_ShouldUseIt()
    {
        var correlationId = Guid.NewGuid().ToString();

        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "test@example.com" },
            Subject = "Test",
            Body = "Test Body"
        };

        var applicationProperties = new Dictionary<string, object>
        {
            { "CorrelationId", correlationId }
        };

        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(JsonSerializer.Serialize(notificationRequest)),
            messageId: Guid.NewGuid().ToString(),
            properties: applicationProperties
        );

        _emailServiceMock
            .Setup(x => x.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _function.Run(message, _messageActionsMock.Object);

        _emailServiceMock.Verify(
            x => x.SendEmailAsync(
                It.Is<EmailMessage>(e => e.CorrelationId == correlationId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WithNullBody_ShouldDeadLetterMessage()
    {
        var correlationId = Guid.NewGuid().ToString();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(Encoding.UTF8.GetBytes("null")),
            messageId: Guid.NewGuid().ToString(),
            correlationId: correlationId
        );

        await _function.Run(message, _messageActionsMock.Object);

        _messageActionsMock.Verify(
            x => x.DeadLetterMessageAsync(
                message,
                It.IsAny<Dictionary<string, object>>(),
                It.Is<string>(r => r == "DeserializationError"),
                It.Is<string>(d => d.Contains("Could not deserialize message body")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WhenEmailServiceThrowsGenericException_ShouldAbandonMessage()
    {
        var correlationId = Guid.NewGuid().ToString();

        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "test@example.com" },
            Subject = "Test",
            Body = "Test Body",
            Metadata = new AlertMetadata { CorrelationId = correlationId }
        };

        var message = CreateServiceBusMessage(notificationRequest, correlationId);

        _emailServiceMock
            .Setup(x => x.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Generic error"));

        await _function.Run(message, _messageActionsMock.Object);

        _messageActionsMock.Verify(x => x.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()), Times.Once);
        _messageActionsMock.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_WithMultipleRecipients_ShouldProcessAll()
    {
        var correlationId = Guid.NewGuid().ToString();

        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "user1@example.com", "user2@example.com", "user3@example.com" },
            Subject = "Test",
            Body = "Test Body",
            Metadata = new AlertMetadata { CorrelationId = correlationId }
        };

        var message = CreateServiceBusMessage(notificationRequest, correlationId);

        _emailServiceMock
            .Setup(x => x.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _function.Run(message, _messageActionsMock.Object);

        _emailServiceMock.Verify(
            x => x.SendEmailAsync(
                It.Is<EmailMessage>(e => e.To.Count == 3),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _messageActionsMock.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WithTemplateParameters_ShouldPassParametersToTemplateService()
    {
        var correlationId = Guid.NewGuid().ToString();
        var templateId = "test-template";
        var parameters = new Dictionary<string, string>
        {
            { "{param1}", "value1" },
            { "{param2}", "value2" }
        };

        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "test@example.com" },
            TemplateId = templateId,
            Parameters = parameters,
            Metadata = new AlertMetadata { CorrelationId = correlationId }
        };

        var message = CreateServiceBusMessage(notificationRequest, correlationId);

        _templateServiceMock
            .Setup(x => x.GetSubject(templateId, parameters))
            .Returns("Test Subject");

        _templateServiceMock
            .Setup(x => x.GetBody(templateId, parameters))
            .Returns("Test Body");

        _emailServiceMock
            .Setup(x => x.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _function.Run(message, _messageActionsMock.Object);

        _templateServiceMock.Verify(x => x.GetSubject(templateId, parameters), Times.Once);
        _templateServiceMock.Verify(x => x.GetBody(templateId, parameters), Times.Once);
    }

    private static ServiceBusReceivedMessage CreateServiceBusMessage(NotificationRequest request, string? correlationId = null)
    {
        var json = JsonSerializer.Serialize(request);
        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(Encoding.UTF8.GetBytes(json)),
            messageId: Guid.NewGuid().ToString(),
            correlationId: correlationId ?? Guid.NewGuid().ToString()
        );
    }
}

