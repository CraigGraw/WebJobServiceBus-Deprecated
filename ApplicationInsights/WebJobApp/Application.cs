using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AzureClient.ServiceBus;
using Microsoft.Extensions.Hosting;

namespace WebJobApp
{
    [ExcludeFromCodeCoverage]
    public class Application : IHostedService
    {
        private ISystemLogger _systemLogger;

        private IQueueClient _queueClient;

        public Application(ISystemLogger systemLogger, IQueueClient queueClient)
        {
            _systemLogger = systemLogger;
            _queueClient = queueClient;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _queueClient.RegisterMessageReceiver(async (messageType, correlationId, body) => { await ProcessMessageTask(messageType, correlationId, body); });
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {   
        }

        private async Task ProcessMessageTask(QueueMessageType messageType, Guid correlationId, string body)
        {
            //QueueTestMessage queueTestMessage = JsonSerializer.Deserialize<QueueTestMessage>(body);

            _systemLogger.Info($"Processsed Message | Type: {messageType}, correlationId: {correlationId}, Body: {body}");

            Console.WriteLine($"Processsed Message | Type: {messageType}, correlationId: {correlationId}, Body: {body}");

            HttpClient httpClient = new HttpClient();
            await httpClient.GetAsync("https://localhost:55548/dummy");
        }
    }
}
