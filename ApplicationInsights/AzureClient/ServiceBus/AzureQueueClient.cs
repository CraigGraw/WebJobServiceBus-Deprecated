using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.ServiceBus;
using Newtonsoft.Json.Linq;
using static System.FormattableString;

namespace AzureClient.ServiceBus
{
    public class AzureQueueClient : IQueueClient
    {
        private readonly string _connectionString;

        private readonly ISystemLogger _logger;

        private readonly int _maxConcurrentSessions;

        private readonly int _prefetchCount;

        private readonly Lazy<QueueClient> _queueClient;

        private readonly string _queueName;

        public AzureQueueClient(IServiceBusCredentialsProvider credentialsProvider, string queueName, int maxConcurrentSessions, int prefetchCount, ISystemLogger logger)
        {
            _connectionString = credentialsProvider.ConnectionString;
            _queueName = queueName;
            _logger = logger;
            _queueClient = new Lazy<QueueClient>(InitialiseQueueClient);
            _maxConcurrentSessions = maxConcurrentSessions;
            _prefetchCount = prefetchCount;
        }

        public void RegisterMessageReceiver(Func<QueueMessageType, Guid, string, Task> callback)
        {
            Func<IMessageSession, Message, CancellationToken, Task> onMessage = CreateOnMessage(callback);

            SessionHandlerOptions options = new SessionHandlerOptions(
                args =>
                {
                    TelemetryClient telemetryClient = new TelemetryClient();
                    telemetryClient.TrackException(args.Exception);

                    return Task.CompletedTask;
                })
            {
                MessageWaitTimeout = TimeSpan.FromSeconds(1),
                AutoComplete = false
            };

            if (_maxConcurrentSessions > 0)
            {
                options.MaxConcurrentSessions = _maxConcurrentSessions;
            }

            if (_prefetchCount > 0)
            {
                _queueClient.Value.PrefetchCount = _prefetchCount;
            }

            _queueClient.Value.RegisterSessionHandler(onMessage, options);
        }

        public async Task SendAsync(QueueMessageType type, string body, string sessionId, Guid correlationId, DateTime? schedule = null)
        {
            Message message = new Message(System.Text.Encoding.UTF8.GetBytes(body))
            {
                UserProperties =
                {
                    [QueuePropertyName.ContentType] = (int)type
                },

                CorrelationId = correlationId.ToString(),

                SessionId = sessionId
            };

            if (schedule != null)
            {
                message.ScheduledEnqueueTimeUtc = schedule.Value;
            }

            await _queueClient.Value.SendAsync(message);
        }
                
        private static string CreateLogMessage(string messageTitle, IMessageSession session, Message receivedMessage)
        {
            try
            {
                string body = System.Text.Encoding.UTF8.GetString(receivedMessage.Body);
                JToken data = JToken.Parse(body);
                string logMessage
                    = Invariant($"{messageTitle} ")
                    + Invariant($"SessionId: '{session?.SessionId}'/'{receivedMessage.SessionId}' ")
                    + Invariant($"MessageId: '{receivedMessage?.MessageId}' ")
                    + Invariant($"CorrelationId: '{receivedMessage?.CorrelationId}'");

                return logMessage;
            }
            catch (Exception e)
            {
                return $"Error occurred when creating log message. Message title: '{messageTitle}'. Error: '{e.Message}'";
            }
        }
        private async Task AbandonMessageAfterErrorAsync(IMessageSession session, Message receivedMessage, Exception error)
        {
            try
            {
                TelemetryClient telemetryClient = new TelemetryClient();
                telemetryClient.TrackException(error);

                await session.AbandonAsync(receivedMessage.SystemProperties.LockToken);

                string logMessage = CreateLogMessage("Queue message abandoned after error.", session, receivedMessage);
                _logger.Error(logMessage, error);
            }
            catch (Exception e)
            {
                string logMessage = CreateLogMessage("Exception while abandoning queue message after error.", session, receivedMessage);
                _logger.Error(logMessage, e);
            }
        }
        private async Task CompleteMessageAfterErrorAsync(IMessageSession session, Message receivedMessage, Exception error)
        {
            try
            {
                TelemetryClient telemetryClient = new TelemetryClient();
                telemetryClient.TrackException(error);

                await session.CompleteAsync(receivedMessage.SystemProperties.LockToken);

                string logMessage = CreateLogMessage("Queue message completed after error.", session, receivedMessage);
                _logger.Error(logMessage, error);
            }
            catch (Exception e)
            {
                string logMessage = CreateLogMessage("Exception while completing queue message after error.", session, receivedMessage);
                _logger.Error(logMessage, e);
            }
        }

       
        private Func<IMessageSession, Message, CancellationToken, Task> CreateOnMessage(Func<QueueMessageType, Guid, string, Task> callback)
        {
            Func<IMessageSession, Message, CancellationToken, Task> onMessage = async (session, receivedMessage, cancellationToken) =>
            {
                while (true)
                {
                    try
                    {
                        if (!Guid.TryParse(receivedMessage.CorrelationId, out Guid correlationId))
                        {
                            correlationId = Guid.NewGuid();
                            _logger.Warn($"CorrelationId: {receivedMessage.CorrelationId} is not a recognized GUID format. Created new id: {correlationId}");
                        }

                        QueueMessageType messageType = (QueueMessageType)receivedMessage.UserProperties[QueuePropertyName.ContentType];
                        string body = System.Text.Encoding.UTF8.GetString(receivedMessage.Body);

                        double secondsQueued = (DateTime.UtcNow - receivedMessage.SystemProperties.EnqueuedTimeUtc).TotalSeconds;

                        _logger.Info("Dequeueing  " + messageType + " message CorrelationId=" + correlationId + " SecondsQueued=" + secondsQueued);
                        string metricName = $"{Assembly.GetEntryAssembly()?.FullName.Split(',')[0]}.MessageSecondsQueued";

                        TelemetryClient telemetryClient = new TelemetryClient();
                        telemetryClient.TrackMetric(metricName, secondsQueued);

                        await callback.Invoke(messageType, correlationId, body);

                        await session.CompleteAsync(receivedMessage.SystemProperties.LockToken);

                        string logMessage = CreateLogMessage("Queue message completed.", session, receivedMessage);
                        _logger.Info(logMessage);

                        return;
                    }
                    catch (ServiceBusException messagingError)
                    {
                        if (messagingError.IsTransient)
                        {
                            string logMessage = CreateLogMessage("Queue message is transient.", session, receivedMessage);
                            _logger.Error(logMessage, messagingError);

                            Thread.Sleep(200);
                            continue;
                        }

                        await AbandonMessageAfterErrorAsync(session, receivedMessage, messagingError);

                        return;
                    }
                    catch (Exception error)
                    {
                        // Handle errors thrown by CompleteAsync()
                        if (error is MessageLockLostException
                            || error is TimeoutException
                            || error is ServerBusyException
                            || error is QuotaExceededException
                            || error is SessionLockLostException)
                        {
                            await AbandonMessageAfterErrorAsync(session, receivedMessage, error);

                            return;
                        }

                        await CompleteMessageAfterErrorAsync(session, receivedMessage, error);

                        return;
                    }
                }
            };

            return onMessage;
        }

        private QueueClient InitialiseQueueClient()
        {
            QueueClient queueClient = new QueueClient(_connectionString, _queueName);

            return queueClient;
        }
    }
}
