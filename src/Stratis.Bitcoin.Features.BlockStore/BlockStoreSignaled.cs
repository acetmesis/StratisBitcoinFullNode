﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Stratis.Bitcoin.Base;
using Stratis.Bitcoin.Connection;
using Stratis.Bitcoin.Interfaces;
using Stratis.Bitcoin.P2P.Peer;
using Stratis.Bitcoin.Primitives;
using Stratis.Bitcoin.Signals;
using Stratis.Bitcoin.Utilities;

namespace Stratis.Bitcoin.Features.BlockStore
{
    public class BlockStoreSignaled : SignalObserver<ChainedHeaderBlock>
    {
        private readonly IBlockStoreQueue blockStoreQueue;

        private readonly ConcurrentChain chain;

        private readonly IChainState chainState;

        private readonly IConnectionManager connection;

        /// <summary>Instance logger.</summary>
        protected readonly ILogger logger;

        /// <summary>Global application life cycle control - triggers when application shuts down.</summary>
        private readonly INodeLifetime nodeLifetime;

        private readonly StoreSettings storeSettings;

        /// <summary>Queue of chained blocks that will be announced to the peers.</summary>
        private readonly AsyncQueue<ChainedHeader> blocksToAnnounce;

        /// <summary>Provider of IBD state.</summary>
        private readonly IInitialBlockDownloadState initialBlockDownloadState;

        /// <summary>Interval between batches in milliseconds.</summary>
        private const int BatchIntervalMs = 5000;

        /// <summary>Task that runs <see cref="DequeueContinuouslyAsync"/>.</summary>
        private readonly Task dequeueLoopTask;

        public BlockStoreSignaled(
            IBlockStoreQueue blockStoreQueue,
            ConcurrentChain chain,
            StoreSettings storeSettings,
            IChainState chainState,
            IConnectionManager connection,
            INodeLifetime nodeLifetime,
            ILoggerFactory loggerFactory,
            IInitialBlockDownloadState initialBlockDownloadState)
        {
            this.blockStoreQueue = blockStoreQueue;
            this.chain = chain;
            this.chainState = chainState;
            this.connection = connection;
            this.nodeLifetime = nodeLifetime;
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
            this.storeSettings = storeSettings;
            this.initialBlockDownloadState = initialBlockDownloadState;

            this.blocksToAnnounce = new AsyncQueue<ChainedHeader>();
            this.dequeueLoopTask = this.DequeueContinuouslyAsync();
        }

        protected override void OnNextCore(ChainedHeaderBlock blockPair)
        {
            if (this.storeSettings.Prune)
            {
                this.logger.LogTrace("(-)[PRUNE]");
                return;
            }

            ChainedHeader chainedHeader = blockPair.ChainedHeader;
            if (chainedHeader == null)
            {
                this.logger.LogTrace("(-)[REORG]");
                return;
            }

            this.logger.LogTrace("Block hash is '{0}'.", chainedHeader.HashBlock);

            // Ensure the block is written to disk before relaying.
            this.AddBlockToQueue(blockPair);

            if (this.initialBlockDownloadState.IsInitialBlockDownload())
            {
                this.logger.LogTrace("(-)[IBD]");
                return;
            }

            this.logger.LogTrace("Block header '{0}' added to the announce queue.", chainedHeader);
            this.blocksToAnnounce.Enqueue(chainedHeader);
        }

        /// <summary>
        /// Adds the block to queue.
        /// Ensures the block is written to disk before relaying to peers.
        /// </summary>
        /// <param name="blockPair">The block pair.</param>
        protected virtual void AddBlockToQueue(ChainedHeaderBlock blockPair)
        {
            this.blockStoreQueue.AddToPending(blockPair);
        }

        /// <summary>
        /// Continuously dequeues items from <see cref="blocksToAnnounce"/> and sends
        /// them  to the peers after the timer runs out or if the last item is a tip.
        /// </summary>
        private async Task DequeueContinuouslyAsync()
        {
            var batch = new List<ChainedHeader>();

            Task<ChainedHeader> dequeueTask = null;
            Task timerTask = null;

            try
            {
                while (!this.nodeLifetime.ApplicationStopping.IsCancellationRequested)
                {
                    // Start new dequeue task if not started already.
                    dequeueTask = dequeueTask ?? this.blocksToAnnounce.DequeueAsync();

                    // Wait for one of the tasks: dequeue and timer (if available) to finish.
                    Task task = timerTask == null ? dequeueTask : await Task.WhenAny(dequeueTask, timerTask).ConfigureAwait(false);
                    await task.ConfigureAwait(false);

                    // Send batch if timer ran out or we've received a tip.
                    bool sendBatch = false;
                    if (dequeueTask.Status == TaskStatus.RanToCompletion)
                    {
                        ChainedHeader item = dequeueTask.Result;
                        // Set the dequeue task to null so it can be assigned on the next iteration.
                        dequeueTask = null;
                        batch.Add(item);
                        sendBatch = item == this.chain.Tip;
                    }
                    else sendBatch = true;

                    if (sendBatch)
                    {
                        this.nodeLifetime.ApplicationStopping.ThrowIfCancellationRequested();

                        await this.SendBatchAsync(batch).ConfigureAwait(false);
                        batch.Clear();

                        timerTask = null;
                    }
                    else
                    {
                        // Start timer if it is not started already.
                        timerTask = timerTask ?? Task.Delay(BatchIntervalMs, this.nodeLifetime.ApplicationStopping);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// A method that relays blocks found in <see cref="batch"/> to connected peers on the network.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The list <see cref="batch"/> contains hashes of blocks that were validated by the consensus rules.
        /// </para>
        /// <para>
        /// These block hashes need to be relayed to connected peers. A peer that does not have a block
        /// will then ask for the entire block, that means only blocks that have been stored/cached should be relayed.
        /// </para>
        /// <para>
        /// During IBD blocks are not relayed to peers.
        /// </para>
        /// <para>
        /// If no nodes are connected the blocks are just discarded, however this is very unlikely to happen.
        /// </para>
        /// <para>
        /// Before relaying, verify the block is still in the best chain else discard it.
        /// </para>
        /// <para>
        /// TODO: consider moving the relay logic to the <see cref="LoopSteps.ProcessPendingStorageStep"/>.
        /// </para>
        /// </remarks>
        private async Task SendBatchAsync(List<ChainedHeader> batch)
        {
            int announceBlockCount = batch.Count;
            if (announceBlockCount == 0)
            {
                this.logger.LogTrace("(-)[NO_BLOCKS]");
                return;
            }

            this.logger.LogTrace("There are {0} blocks in the announce queue.", announceBlockCount);

            // Remove blocks that we've reorged away from.
            foreach (ChainedHeader reorgedBlock in batch.Where(x => this.chainState.ConsensusTip.FindAncestorOrSelf(x) == null).ToList())
            {
                this.logger.LogTrace("Block header '{0}' not found in the consensus chain and will be skipped.", reorgedBlock);

                // List removal is of O(N) complexity but in this case removals will happen just a few times a day (on orphaned blocks)
                // and always only the latest items in this list will be subjected to removal so in this case it's better than creating
                // a new list of blocks on every batch send that were not reorged.
                batch.Remove(reorgedBlock);
            }

            if (!batch.Any())
            {
                this.logger.LogTrace("(-)[NO_BROADCAST_ITEMS]");
                return;
            }

            IReadOnlyNetworkPeerCollection peers = this.connection.ConnectedPeers;
            if (!peers.Any())
            {
                this.logger.LogTrace("(-)[NO_PEERS]");
                return;
            }

            // Announces the headers to peers using the appropriate behavior (BlockStoreBehavior or behaviors that inherits from it).
            List<BlockStoreBehavior> behaviors = peers.Select(peer => peer.Behavior<BlockStoreBehavior>())
                .Where(behavior => behavior != null).ToList();

            this.logger.LogTrace("{0} blocks will be sent to {1} peers.", batch.Count, behaviors.Count);
            foreach (BlockStoreBehavior behavior in behaviors)
                await behavior.AnnounceBlocksAsync(batch).ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            // Let current batch sending task finish.
            this.blocksToAnnounce.Dispose();
            this.dequeueLoopTask.GetAwaiter().GetResult();

            base.Dispose(disposing);
        }
    }
}