using Azure.Messaging.ServiceBus;
using Common.Notifications.Function.Functions;
using Common.Notifications.Function.Interfaces;
using Common.Notifications.Function.Logging;
using Common.Notifications.Function.Models;
using Common.Notifications.Function.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;
using System.Text.Json;

namespace UnitTests.Functions;

public class NotificationFunctionRateLimitTests
{
    private readonly Mock<ILogger<NotificationFunction>> _loggerMock;
    private readonly Mock<IEmailService> _emailServiceMock;
    private readonly Mock<IEmailTemplateService> _templateServiceMock;
    private readonly Mock<CorrelationIdEnricher> _correlationIdEnricherMock;
    private readonly Mock<ServiceBusMessageActions> _messageActionsMock;
    private readonly NotificationFunction _function;

    public NotificationFunctionRateLimitTests()
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
    public async Task Run_WhenRateLimitExceeded_ShouldAbandonMessage()
    {
        var correlationId = Guid.NewGuid().ToString();

        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "test@example.com" },
            Subject = "Test Subject",
            Body = "Test Body",
            Metadata = new AlertMetadata { CorrelationId = correlationId }
        };

        var message = CreateServiceBusMessage(notificationRequest, correlationId);

        _emailServiceMock
            .Setup(x => x.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TooManyRequestsException("Rate limit exceeded", new Exception("Test exception")));

        await _function.Run(message, _messageActionsMock.Object);

        _messageActionsMock.Verify(
            x => x.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _messageActionsMock.Verify(
            x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()),
            Times.Never);

        _messageActionsMock.Verify(
            x => x.DeadLetterMessageAsync(
                message,
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_WhenRateLimitExceededWithTemplate_ShouldAbandonMessageAndLogWarning()
    {
        var correlationId = Guid.NewGuid().ToString();
        var templateId = "HeatStress";
        
        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "test@example.com" },
            TemplateId = templateId,
            Parameters = new Dictionary<string, string> { { "{temp}", "35" } },
            Metadata = new AlertMetadata { CorrelationId = correlationId }
        };

        var message = CreateServiceBusMessage(notificationRequest, correlationId);

        _templateServiceMock
            .Setup(x => x.GetSubject(templateId, It.IsAny<Dictionary<string, string>>()))
            .Returns("Test Subject");

        _templateServiceMock
            .Setup(x => x.GetBody(templateId, It.IsAny<Dictionary<string, string>>()))
            .Returns("Test Body");

        _emailServiceMock
            .Setup(x => x.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TooManyRequestsException("Rate limit exceeded", new Exception("Test exception")));

        await _function.Run(message, _messageActionsMock.Object);

        _messageActionsMock.Verify(
            x => x.AbandonMessageAsync(message, It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Rate limit exceeded")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WhenRateLimitExceeded_ShouldNotCompleteOrDeadLetterMessage()
    {
        var correlationId = Guid.NewGuid().ToString();
        
        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "test@example.com" },
            Subject = "Test",
            Body = "Body",
            Metadata = new AlertMetadata { CorrelationId = correlationId }
        };

        var message = CreateServiceBusMessage(notificationRequest, correlationId);

        _emailServiceMock
            .Setup(x => x.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TooManyRequestsException("Rate limit exceeded", new Exception("Test exception")));

        await _function.Run(message, _messageActionsMock.Object);

        _messageActionsMock.Verify(
            x => x.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _messageActionsMock.Verify(
            x => x.DeadLetterMessageAsync(
                It.IsAny<ServiceBusReceivedMessage>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_WhenMultipleRateLimitErrors_ShouldAbandonEachMessage()
    {
        var correlationId = Guid.NewGuid().ToString();
        
        var notificationRequest = new NotificationRequest
        {
            EmailTo = new List<string> { "test@example.com" },
            Subject = "Test",
            Body = "Body",
            Metadata = new AlertMetadata { CorrelationId = correlationId }
        };

        var message1 = CreateServiceBusMessage(notificationRequest, correlationId);
        var message2 = CreateServiceBusMessage(notificationRequest, correlationId);

        _emailServiceMock
            .Setup(x => x.SendEmailAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TooManyRequestsException("Rate limit exceeded", new Exception("Test exception")));

        await _function.Run(message1, _messageActionsMock.Object);
        await _function.Run(message2, _messageActionsMock.Object);

        _messageActionsMock.Verify(
            x => x.AbandonMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
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
