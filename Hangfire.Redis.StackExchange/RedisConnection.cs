// Copyright � 2013-2015 Sergey Odinokov, Marco Casamento
// This software is based on https://github.com/HangfireIO/Hangfire.Redis

// Hangfire.Redis.StackExchange is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as
// published by the Free Software Foundation, either version 3
// of the License, or any later version.
//
// Hangfire.Redis.StackExchange is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with Hangfire.Redis.StackExchange. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using StackExchange.Redis;
using Hangfire.Annotations;

namespace Hangfire.Redis
{
    internal class RedisConnection : JobStorageConnection
    {
        private readonly RedisStorage _storage;
        private readonly RedisSubscription _subscription;
        private readonly string _jobStorageIdentity;
        private readonly TimeSpan _fetchTimeout = TimeSpan.FromMinutes(3);

        public RedisConnection(
            [NotNull] RedisStorage storage, 
            [NotNull] IDatabase redis, 
            [NotNull] RedisSubscription subscription, 
            [NotNull] string jobStorageIdentity, 
            TimeSpan fetchTimeout)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (redis == null) throw new ArgumentNullException(nameof(redis));
            if (subscription == null) throw new ArgumentNullException(nameof(subscription));
            if (jobStorageIdentity == null) throw new ArgumentNullException(nameof(jobStorageIdentity));

            _storage = storage;
            _subscription = subscription;
            _jobStorageIdentity = jobStorageIdentity;
            _fetchTimeout = fetchTimeout;

            Redis = redis;
        }

        public IDatabase Redis { get; }
        
        public override IDisposable AcquireDistributedLock([NotNull] string resource, TimeSpan timeout)
        {
            return new RedisLock(Redis, _storage.GetRedisKey(resource), _jobStorageIdentity + Thread.CurrentThread.ManagedThreadId, timeout);
        }

        public override void AnnounceServer([NotNull] string serverId, [NotNull] ServerContext context)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var transaction = Redis.CreateTransaction();

            transaction.SetAddAsync(_storage.GetRedisKey("servers"), serverId);

            transaction.HashSetAsync(
                _storage.GetRedisKey($"server:{serverId}"),
                new Dictionary<string, string>
                    {
                        { "WorkerCount", context.WorkerCount.ToString(CultureInfo.InvariantCulture) },
                        { "StartedAt", JobHelper.SerializeDateTime(DateTime.UtcNow) },
                    }.ToHashEntries());

            foreach (var queue in context.Queues)
            {
                var queue1 = queue;
                transaction.ListRightPushAsync(
                    _storage.GetRedisKey($"server:{serverId}:queues"),
                    queue1);
            }

            transaction.Execute();

        }

        public override string CreateExpiredJob(
            [NotNull] Job job,
            [NotNull] IDictionary<string, string> parameters,
            DateTime createdAt,
            TimeSpan expireIn)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            var jobId = Guid.NewGuid().ToString();

            var invocationData = InvocationData.Serialize(job);

            // Do not modify the original parameters.
            var storedParameters = new Dictionary<string, string>(parameters)
            {
                { "Type", invocationData.Type },
                { "Method", invocationData.Method },
                { "ParameterTypes", invocationData.ParameterTypes },
                { "Arguments", invocationData.Arguments },
                { "CreatedAt", JobHelper.SerializeDateTime(createdAt) }
            };

            var transaction = Redis.CreateTransaction();

            transaction.HashSetAsync(_storage.GetRedisKey($"job:{jobId}"), storedParameters.ToHashEntries());
            transaction.KeyExpireAsync(_storage.GetRedisKey($"job:{jobId}"), expireIn);

            // TODO: check return value
            transaction.Execute();

            return jobId;
        }

        public override IWriteOnlyTransaction CreateWriteTransaction()
        {
            return new RedisWriteOnlyTransaction(_storage, Redis.CreateTransaction());
        }

        public override void Dispose()
        {
            // nothing to dispose
        }

        public override IFetchedJob FetchNextJob([NotNull] string[] queues, CancellationToken cancellationToken)
        {
            if (queues == null) throw new ArgumentNullException(nameof(queues));

            string jobId = null;
            string queueName = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int i = 0; i < queues.Length; i++)
                {
                    queueName = queues[i];
                    var queueKey = _storage.GetRedisKey($"queue:{queueName}");
                    var fetchedKey = _storage.GetRedisKey($"queue:{queueName}:dequeued");
                    jobId = Redis.ListRightPopLeftPush(queueKey, fetchedKey);
                    if (jobId != null) break;
                }

                if (jobId == null)
                {
                    _subscription.WaitForJob(_fetchTimeout, cancellationToken);
                }
            }
            while (jobId == null);

            // The job was fetched by the server. To provide reliability,
            // we should ensure, that the job will be performed and acquired
            // resources will be disposed even if the server will crash
            // while executing one of the subsequent lines of code.

            // The job's processing is splitted into a couple of checkpoints.
            // Each checkpoint occurs after successful update of the
            // job information in the storage. And each checkpoint describes
            // the way to perform the job when the server was crashed after
            // reaching it.

            // Checkpoint #1-1. The job was fetched into the fetched list,
            // that is being inspected by the FetchedJobsWatcher instance.
            // Job's has the implicit 'Fetched' state.

            Redis.HashSet(
                _storage.GetRedisKey($"job:{jobId}"),
                "Fetched",
                JobHelper.SerializeDateTime(DateTime.UtcNow));

            // Checkpoint #2. The job is in the implicit 'Fetched' state now.
            // This state stores information about fetched time. The job will
            // be re-queued when the JobTimeout will be expired.

            return new RedisFetchedJob(_storage, Redis, jobId, queueName);
        }

        public override Dictionary<string, string> GetAllEntriesFromHash([NotNull] string key)
        {
            var result = Redis.HashGetAll(_storage.GetRedisKey(key)).ToStringDictionary();

            return result.Count != 0 ? result : null;
        }

        public override List<string> GetAllItemsFromList([NotNull] string key)
        {
            return Redis.ListRange(_storage.GetRedisKey(key)).ToStringArray().ToList();
        }

        public override HashSet<string> GetAllItemsFromSet([NotNull] string key)
        {
            HashSet<string> result = new HashSet<string>();
            foreach (var item in Redis.SortedSetScan(_storage.GetRedisKey(key)))
            {
                result.Add(item.Element);
            }

            return result;
        }

        public override long GetCounter([NotNull] string key)
        {
            return Convert.ToInt64(Redis.StringGet(_storage.GetRedisKey(key)));
        }

        public override string GetFirstByLowestScoreFromSet([NotNull] string key, double fromScore, double toScore)
        {
            return Redis.SortedSetRangeByScore(_storage.GetRedisKey(key), fromScore, toScore, skip: 0, take: 1)
                .FirstOrDefault();
        }

        public override long GetHashCount([NotNull] string key)
        {
            return Redis.HashLength(_storage.GetRedisKey(key));
        }

        public override TimeSpan GetHashTtl([NotNull] string key)
        {
            return Redis.KeyTimeToLive(_storage.GetRedisKey(key)) ?? TimeSpan.Zero;
        }

        public override JobData GetJobData([NotNull] string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            var storedData = Redis.HashGetAll(_storage.GetRedisKey($"job:{jobId}"));
            if (storedData.Length == 0) return null;

            string type = null;
            string method = null;
            string parameterTypes = null;
            string arguments = null;
            string createdAt = null;

            if (storedData.ContainsKey("Type"))
                type = storedData.First(x => x.Name == "Type").Value;

            if (storedData.ContainsKey("Method"))
                method = storedData.First(x => x.Name == "Method").Value;

            if (storedData.ContainsKey("ParameterTypes"))
                parameterTypes = storedData.First(x => x.Name == "ParameterTypes").Value;

            if (storedData.ContainsKey("Arguments"))
                arguments = storedData.First(x => x.Name == "Arguments").Value;

            if (storedData.ContainsKey("CreatedAt"))
                createdAt = storedData.First(x => x.Name == "CreatedAt").Value;

            Job job = null;
            JobLoadException loadException = null;

            var invocationData = new InvocationData(type, method, parameterTypes, arguments);

            try
            {
                job = invocationData.Deserialize();
            }
            catch (JobLoadException ex)
            {
                loadException = ex;
            }

            return new JobData
            {
                Job = job,
                State = storedData.ContainsKey("State") ? (string)storedData.First(x => x.Name == "State").Value : null,
                CreatedAt = JobHelper.DeserializeNullableDateTime(createdAt) ?? DateTime.MinValue,
                LoadException = loadException
            };
        }

        public override string GetJobParameter([NotNull] string jobId, [NotNull] string name)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            if (name == null) throw new ArgumentNullException(nameof(name));

            return Redis.HashGet(_storage.GetRedisKey($"job:{jobId}"), name);
        }

        public override long GetListCount([NotNull] string key)
        {
            return Redis.ListLength(_storage.GetRedisKey(key));
        }

        public override TimeSpan GetListTtl([NotNull] string key)
        {
            return Redis.KeyTimeToLive(_storage.GetRedisKey(key)) ?? TimeSpan.Zero;
        }

        public override List<string> GetRangeFromList([NotNull] string key, int startingFrom, int endingAt)
        {
            return Redis.ListRange(_storage.GetRedisKey(key), startingFrom, endingAt).ToStringArray().ToList();
        }

        public override List<string> GetRangeFromSet([NotNull] string key, int startingFrom, int endingAt)
        {
            return Redis.SortedSetRangeByRank(_storage.GetRedisKey(key), startingFrom, endingAt).ToStringArray().ToList();
        }

        public override long GetSetCount([NotNull] string key)
        {
            return Redis.SortedSetLength(_storage.GetRedisKey(key));
        }

        public override TimeSpan GetSetTtl([NotNull] string key)
        {
            return Redis.KeyTimeToLive(_storage.GetRedisKey(key)) ?? TimeSpan.Zero;
        }

        public override StateData GetStateData([NotNull] string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            var entries = Redis.HashGetAll(_storage.GetRedisKey($"job:{jobId}:state"));
            if (entries.Length == 0) return null;

            var stateData = entries.ToStringDictionary();

            stateData.Remove("State");
            stateData.Remove("Reason");

            return new StateData
            {
                Name = entries.First(x => x.Name == "State").Value,
                Reason = entries.ContainsKey("Reason") ? (string)entries.First(x => x.Name == "Reason").Value : null,
                Data = stateData
            };
        }

        public override string GetValueFromHash([NotNull] string key, [NotNull] string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));

            return Redis.HashGet(_storage.GetRedisKey(key), name);
        }

        public override void Heartbeat([NotNull] string serverId)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));

            Redis.HashSet(
                _storage.GetRedisKey($"server:{serverId}"),
                "Heartbeat",
                JobHelper.SerializeDateTime(DateTime.UtcNow));
        }

        public override void RemoveServer([NotNull] string serverId)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));

            var transaction = Redis.CreateTransaction();

            transaction.SetRemoveAsync(_storage.GetRedisKey("servers"), serverId);

            transaction.KeyDeleteAsync(
                new RedisKey[]
                {
                    _storage.GetRedisKey($"server:{serverId}"),
                    _storage.GetRedisKey($"server:{serverId}:queues")
                });

            transaction.Execute();
        }

        public override int RemoveTimedOutServers(TimeSpan timeOut)
        {
            var serverNames = Redis.SetMembers(_storage.GetRedisKey("servers"));
            var heartbeats = new Dictionary<string, Tuple<DateTime, DateTime?>>();

            var utcNow = DateTime.UtcNow;
            
            foreach (var serverName in serverNames)
            {
                var name = serverName;
                var srv = Redis.HashGet(_storage.GetRedisKey($"server:{name}"), new RedisValue[] { "StartedAt", "Heartbeat" });
                heartbeats.Add(name,
                                new Tuple<DateTime, DateTime?>(
                                JobHelper.DeserializeDateTime(srv[0]),
                                JobHelper.DeserializeNullableDateTime(srv[1])));
            }

            var removedServerCount = 0;
            foreach (var heartbeat in heartbeats)
            {
                var maxTime = new DateTime(
                    Math.Max(heartbeat.Value.Item1.Ticks, (heartbeat.Value.Item2 ?? DateTime.MinValue).Ticks));

                if (utcNow > maxTime.Add(timeOut))
                {
                    RemoveServer(heartbeat.Key);
                    removedServerCount++;
                }
            }

            return removedServerCount;
        }

        public override void SetJobParameter([NotNull] string jobId, [NotNull] string name, string value)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            if (name == null) throw new ArgumentNullException(nameof(name));

            Redis.HashSet(_storage.GetRedisKey($"job:{jobId}"), name, value);
        }

        public override void SetRangeInHash([NotNull] string key, [NotNull] IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            if (keyValuePairs == null) throw new ArgumentNullException(nameof(keyValuePairs));

            Redis.HashSet(_storage.GetRedisKey(key), keyValuePairs.ToHashEntries());
        }
    }
}
