namespace KafkaFlow.Client.Producers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using KafkaFlow.Client.Exceptions;
    using KafkaFlow.Client.Extensions;
    using KafkaFlow.Client.Protocol.Messages;

    internal class ProducerSender : IAsyncDisposable
    {
        private readonly IKafkaBroker broker;
        private readonly ProducerConfiguration configuration;
        private readonly Task produceTimeoutTask;
        private readonly CancellationTokenSource stopLingerProduceTokenSource = new();
        private readonly SemaphoreSlim produceSemaphore = new(1, 1);

        private IProduceRequest request;
        private volatile int messageCount;
        private DateTime lastProductionTime = DateTime.MinValue;

        private SortedDictionary<(string, int), LinkedList<ProduceQueueItem>> pendingRequests = new();

        public ProducerSender(
            IKafkaBroker broker,
            ProducerConfiguration configuration)
        {
            this.broker = broker;
            this.configuration = configuration;

            this.produceTimeoutTask = Task.Run(this.LingerProduceAsync);
        }

        public async ValueTask EnqueueAsync(ProduceQueueItem item)
        {
            await this.produceSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                this.request ??= await this.CreateProduceRequestAsync();

                var topic = this.request.Topics.SafeGetOrAdd(
                    item.Data.Topic,
                    _ => this.request.CreateTopic(item.Data.Topic));

                var partition = topic.Partitions.SafeGetOrAdd(
                    item.PartitionId,
                    _ => topic.CreatePartition(item.PartitionId));

                partition.RecordBatch.AddRecord(
                    new RecordBatch.Record
                    {
                        Key = item.Data.Key,
                        Value = item.Data.Value,
                        Headers = item.Data.Headers
                    });

                item.OffsetDelta = partition.RecordBatch.LastOffsetDelta;

                this.pendingRequests
                    .SafeGetOrAdd(
                        (item.Data.Topic, item.PartitionId),
                        key => new LinkedList<ProduceQueueItem>())
                    .AddLast(item);
            }
            finally
            {
                this.produceSemaphore.Release();
            }

            if (Interlocked.Increment(ref this.messageCount) >= this.configuration.MaxProduceBatchSize)
            {
                await this.ProduceAsync();
            }
        }

        private async Task ProduceAsync()
        {
            try
            {
                if (this.messageCount == 0)
                {
                    return;
                }

                Task<IProduceResponse> resultTask;
                SortedDictionary<(string, int), LinkedList<ProduceQueueItem>> requests;

                await this.produceSemaphore.WaitAsync().ConfigureAwait(false);

                try
                {
                    if (Interlocked.Exchange(ref this.messageCount, 0) == 0)
                    {
                        return;
                    }

                    var queued = Interlocked.Exchange(ref this.request, await this.CreateProduceRequestAsync());

                    resultTask = this.broker.Connection.SendAsync(queued);

                    requests = Interlocked.Exchange(
                        ref this.pendingRequests,
                        new SortedDictionary<(string, int), LinkedList<ProduceQueueItem>>());
                }
                finally
                {
                    this.produceSemaphore.Release();
                    this.lastProductionTime = DateTime.Now;
                }

                this.RespondRequests(
                    await resultTask.ConfigureAwait(false),
                    requests);
            }
            catch (Exception e)
            {
                // TODO: some kind of log or retry on errors
                throw;
            }
        }

        private async Task<IProduceRequest> CreateProduceRequestAsync()
        {
            var requestFactory = await this.broker.GetRequestFactoryAsync();

            return requestFactory.CreateProduce(
                this.configuration.Acks,
                (int) this.configuration.ProduceTimeout.TotalMilliseconds);
        }

        private void RespondRequests(
            IProduceResponse result,
            IDictionary<(string, int), LinkedList<ProduceQueueItem>> requests)
        {
            foreach (var topic in result.Topics)
            {
                foreach (var partition in topic.Partitions)
                {
                    if (!requests.TryGetValue((topic.Name, partition.Id), out var items))
                    {
                        continue;
                    }

                    // requests.Remove((topic.Name, partition.Id));

                    if (partition.Error == ErrorCode.None)
                    {
                        foreach (var item in items)
                        {
                            item.CompletionSource.SetResult(
                                new ProduceResult(
                                    topic.Name,
                                    partition.Id,
                                    partition.BaseOffset + item.OffsetDelta,
                                    item.Data));
                        }
                    }
                    else
                    {
                        foreach (var item in items)
                        {
                            var recordError = partition.RecordErrors.FirstOrDefault(x => x.BatchIndex == item.OffsetDelta);

                            if (recordError is null)
                            {
                                item.CompletionSource.SetResult(
                                    new ProduceResult(
                                        topic.Name,
                                        partition.Id,
                                        partition.BaseOffset + item.OffsetDelta,
                                        item.Data));
                            }
                            else
                            {
                                item.CompletionSource.SetException(
                                    new ProduceException(
                                        partition.Error,
                                        partition.ErrorMessage,
                                        recordError?.Message));
                            }
                        }
                    }
                }
            }
        }

        private async Task LingerProduceAsync()
        {
            try
            {
                while (!this.stopLingerProduceTokenSource.IsCancellationRequested)
                {
                    var diff = DateTime.Now - this.lastProductionTime;
                    if (diff < this.configuration.Linger)
                    {
                        await Task
                            .Delay(
                                this.configuration.Linger - diff,
                                this.stopLingerProduceTokenSource.Token)
                            .ConfigureAwait(false);

                        continue;
                    }

                    await this.ProduceAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Do nothing
            }

            await this.ProduceAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            this.produceSemaphore.Dispose();
            this.stopLingerProduceTokenSource.Cancel();
            await this.produceTimeoutTask.ConfigureAwait(false);
            this.produceTimeoutTask.Dispose();
        }
    }
}
