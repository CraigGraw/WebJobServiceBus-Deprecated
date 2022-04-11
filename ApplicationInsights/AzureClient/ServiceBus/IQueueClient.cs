using System;
using System.Threading.Tasks;

namespace AzureClient.ServiceBus
{
    public interface IQueueClient
    {
        void RegisterMessageReceiver(Func<QueueMessageType, Guid, string, Task> callback);

        Task SendAsync(QueueMessageType type, string body, string sessionId, Guid correlationId, DateTime? schedule = null);
    }
}
