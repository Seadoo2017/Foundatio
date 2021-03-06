using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Lock {
    public interface ILockProvider {
        Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default);
        Task<bool> IsLockedAsync(string resource);
        Task ReleaseAsync(ILock @lock);
        Task RenewAsync(ILock @lock, TimeSpan? timeUntilExpires = null);
    }

    public interface ILock : IAsyncDisposable {
        Task RenewAsync(TimeSpan? timeUntilExpires = null);
        Task ReleaseAsync();
        string LockId { get; }
        string Resource { get; }
        DateTime AcquiredTimeUtc { get; }
        TimeSpan TimeWaitedForLock { get; }
        int RenewalCount { get; }
    }

    public static class LockProviderExtensions {
        public static async Task<ILock> AcquireAsync(this ILockProvider provider, string resource, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null) {
            using (var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30))) {
                return await provider.AcquireAsync(resource, timeUntilExpires, cancellationTokenSource.Token).AnyContext();
            }
        }
        
        public static async Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Func<CancellationToken, Task> work, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default) {
            var l = await locker.AcquireAsync(resource, timeUntilExpires, cancellationToken).AnyContext();
            if (l == null)
                return false;

            try {
                await work(cancellationToken).AnyContext();
            } finally {
                await l.ReleaseAsync().AnyContext();
            }

            return true;
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Func<Task> work, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default) {
            var l = await locker.AcquireAsync(resource, timeUntilExpires, cancellationToken).AnyContext();
            if (l == null)
                return false;

            try {
                await work().AnyContext();
            } finally {
                await l.ReleaseAsync().AnyContext();
            }

            return true;
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Func<CancellationToken, Task> work, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null) {
            using (var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource()) {
                var l = await locker.AcquireAsync(resource, timeUntilExpires, cancellationTokenSource.Token).AnyContext();
                if (l == null)
                    return false;

                try {
                    await work(cancellationTokenSource.Token).AnyContext();
                } finally {
                    await l.ReleaseAsync().AnyContext();
                }
            }

            return true;
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Func<Task> work, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null) {
            using (var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource()) {
                var l = await locker.AcquireAsync(resource, timeUntilExpires, cancellationTokenSource.Token).AnyContext();
                if (l == null)
                    return false;

                try {
                    await work().AnyContext();
                } finally {
                    await l.ReleaseAsync().AnyContext();
                }
            }

            return true;
        }

        public static Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Action work, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null) {
            return locker.TryUsingAsync(resource, () => {
                work();
                return Task.CompletedTask;
            }, timeUntilExpires, acquireTimeout);
        }
        
        public static async Task<ILock> AcquireAsync(this ILockProvider provider, IEnumerable<string> resources, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default) {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));
            
            var resourceList = resources.Distinct().ToArray();
            if (resourceList.Length == 0)
                return new EmptyLock();

            var logger = provider.GetLogger();
            
            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace("Acquiring {LockCount} locks {Resource}", resourceList.Length, resourceList);
            
            var sw = Stopwatch.StartNew();
            var locks = await Task.WhenAll(resourceList.Select(r => provider.AcquireAsync(r, timeUntilExpires, cancellationToken)));
            sw.Stop();
            
            // if any lock is null, release any acquired and return null (all or nothing)
            var acquiredLocks = locks.Where(l => l != null).ToArray();
            var unacquiredResources = resourceList.Except(locks.Select(l => l?.Resource)).ToArray();
            if (unacquiredResources.Length > 0) {
                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace("Unable to acquire all {LockCount} locks {Resource} releasing acquired locks", unacquiredResources.Length, unacquiredResources);
                
                await Task.WhenAll(acquiredLocks.Select(l => l.ReleaseAsync())).AnyContext();
                return null;
            }
            
            if (logger.IsEnabled(LogLevel.Trace))
                logger.LogTrace("Acquired {LockCount} locks {Resource} after {Duration:g}", resourceList.Length, resourceList, sw.Elapsed);
            
            return new DisposableLockCollection(locks, String.Join("+", locks.Select(l => l.LockId)), sw.Elapsed, logger);
        }
        
        public static async Task<ILock> AcquireAsync(this ILockProvider provider, IEnumerable<string> resources, TimeSpan? timeUntilExpires, TimeSpan? acquireTimeout) {
            using (var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30))) {
                return await provider.AcquireAsync(resources, timeUntilExpires, cancellationTokenSource.Token).AnyContext();
            }
        }
        
        public static async Task<bool> TryUsingAsync(this ILockProvider locker, IEnumerable<string> resources, Func<CancellationToken, Task> work, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default) {
            var l = await locker.AcquireAsync(resources, timeUntilExpires, cancellationToken).AnyContext();
            if (l == null)
                return false;

            try {
                await work(cancellationToken).AnyContext();
            } finally {
                await l.ReleaseAsync().AnyContext();
            }

            return true;
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, IEnumerable<string> resources, Func<Task> work, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default) {
            var l = await locker.AcquireAsync(resources, timeUntilExpires, cancellationToken).AnyContext();
            if (l == null)
                return false;

            try {
                await work().AnyContext();
            } finally {
                await l.ReleaseAsync().AnyContext();
            }

            return true;
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, IEnumerable<string> resources, Func<CancellationToken, Task> work, TimeSpan? timeUntilExpires, TimeSpan? acquireTimeout) {
            using (var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource()) {
                var l = await locker.AcquireAsync(resources, timeUntilExpires, cancellationTokenSource.Token).AnyContext();
                if (l == null)
                    return false;

                try {
                    await work(cancellationTokenSource.Token).AnyContext();
                } finally {
                    await l.ReleaseAsync().AnyContext();
                }
            }

            return true;
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, IEnumerable<string> resources, Func<Task> work, TimeSpan? timeUntilExpires, TimeSpan? acquireTimeout) {
            using (var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource()) {
                var l = await locker.AcquireAsync(resources, timeUntilExpires, cancellationTokenSource.Token).AnyContext();
                if (l == null)
                    return false;

                try {
                    await work().AnyContext();
                } finally {
                    await l.ReleaseAsync().AnyContext();
                }
            }

            return true;
        }

        public static Task<bool> TryUsingAsync(this ILockProvider locker, IEnumerable<string> resources, Action work, TimeSpan? timeUntilExpires, TimeSpan? acquireTimeout) {
            return locker.TryUsingAsync(resources, () => {
                work();
                return Task.CompletedTask;
            }, timeUntilExpires, acquireTimeout);
        }
    }
}
