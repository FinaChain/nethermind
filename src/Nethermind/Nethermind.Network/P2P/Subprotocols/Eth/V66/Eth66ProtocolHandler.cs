// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DotNetty.Common.Utilities;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using GetPooledTransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;
using PooledTransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66
{
    /// <summary>
    /// https://github.com/ethereum/EIPs/blob/master/EIPS/eip-2481.md
    /// </summary>
    public class Eth66ProtocolHandler : Eth65ProtocolHandler
    {
        private readonly MessageDictionary<GetBlockHeadersMessage, V62.Messages.GetBlockHeadersMessage, BlockHeader[]> _headersRequests66;
        private readonly MessageDictionary<GetBlockBodiesMessage, V62.Messages.GetBlockBodiesMessage, BlockBody[]> _bodiesRequests66;
        private readonly MessageDictionary<GetNodeDataMessage, V63.Messages.GetNodeDataMessage, byte[][]> _nodeDataRequests66;
        private readonly MessageDictionary<GetReceiptsMessage, V63.Messages.GetReceiptsMessage, TxReceipt[][]> _receiptsRequests66;
        private readonly IPooledTxsRequestor _pooledTxsRequestor;
        private readonly ILogger _logger;

        private bool _isDisposed = false;

        // Well, we don't really want many tasks as we already have many peer. But we also don't want a heavy task
        // to block small tasks. For messages using `MessageDictionary`, this is effectively the number of deserialization
        // thread.
        private static readonly int _processingTasksCount = 2;

        // In case something very heavy happens intermittently that really slows down processing, queue message but
        // don't block network. Alternatively we can set this to 0, and have the `_processingTasksCount` to be high.
        // Its ok to have the queue capacity high, we just don't wanna have a bad peer to fill up the RAM.
        private static readonly int _queueCapacity = 32;

        private Channel<ZeroPacket> _incomingMessageQueue = Channel.CreateBounded<ZeroPacket>(_queueCapacity);
        private CancellationTokenSource _cancellationTokenSource = new();

        public Eth66ProtocolHandler(ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager nodeStatsManager,
            ISyncServer syncServer,
            ITxPool txPool,
            IPooledTxsRequestor pooledTxsRequestor,
            IGossipPolicy gossipPolicy,
            ISpecProvider specProvider,
            ILogManager logManager)
            : base(session, serializer, nodeStatsManager, syncServer, txPool, pooledTxsRequestor, gossipPolicy, specProvider, logManager)
        {
            _headersRequests66 = new MessageDictionary<GetBlockHeadersMessage, V62.Messages.GetBlockHeadersMessage, BlockHeader[]>(Send);
            _bodiesRequests66 = new MessageDictionary<GetBlockBodiesMessage, V62.Messages.GetBlockBodiesMessage, BlockBody[]>(Send);
            _nodeDataRequests66 = new MessageDictionary<GetNodeDataMessage, V63.Messages.GetNodeDataMessage, byte[][]>(Send);
            _receiptsRequests66 = new MessageDictionary<GetReceiptsMessage, V63.Messages.GetReceiptsMessage, TxReceipt[][]>(Send);
            _pooledTxsRequestor = pooledTxsRequestor;
            _logger = logManager.GetClassLogger();
        }

        public override string Name => "eth66";

        public override byte ProtocolVersion => 66;

        // Just for debugging
        private uint _lastProcessingPacketType = 0xff;

        public override void Init()
        {
            for (int i = 0; i < _processingTasksCount; i++)
            {
                Task.Factory.StartNew(StartReceiveLoop, TaskCreationOptions.LongRunning);
            }
            base.Init();
        }

        public override void HandleMessage(ZeroPacket message)
        {
            switch (message.PacketType)
            {
                case Eth66MessageCode.GetBlockHeaders:
                case Eth66MessageCode.BlockHeaders:
                case Eth66MessageCode.GetBlockBodies:
                case Eth66MessageCode.BlockBodies:
                case Eth66MessageCode.GetPooledTransactions:
                case Eth66MessageCode.PooledTransactions:
                case Eth66MessageCode.GetReceipts:
                case Eth66MessageCode.Receipts:
                case Eth66MessageCode.GetNodeData:
                case Eth66MessageCode.NodeData:
                    // Eth66 messages only. When applying this globally, the node struggles to keep peers.
                    QueueMessage(message);
                    break;
                default:
                    DoHandleMessage(message);
                    break;
            }
        }

        private void DoHandleMessage(ZeroPacket message)
        {
            int size = message.Content.ReadableBytes;

            switch (message.PacketType)
            {
                case Eth66MessageCode.GetBlockHeaders:
                    GetBlockHeadersMessage getBlockHeadersMessage
                        = Deserialize<GetBlockHeadersMessage>(message.Content);
                    Metrics.Eth66GetBlockHeadersReceived++;
                    ReportIn(getBlockHeadersMessage);
                    Handle(getBlockHeadersMessage);
                    break;
                case Eth66MessageCode.BlockHeaders:
                    BlockHeadersMessage headersMsg = Deserialize<BlockHeadersMessage>(message.Content);
                    Metrics.Eth66BlockHeadersReceived++;
                    ReportIn(headersMsg);
                    Handle(headersMsg, size);
                    break;
                case Eth66MessageCode.GetBlockBodies:
                    GetBlockBodiesMessage getBodiesMsg = Deserialize<GetBlockBodiesMessage>(message.Content);
                    Metrics.Eth66GetBlockBodiesReceived++;
                    ReportIn(getBodiesMsg);
                    Handle(getBodiesMsg);
                    break;
                case Eth66MessageCode.BlockBodies:
                    BlockBodiesMessage bodiesMsg = Deserialize<BlockBodiesMessage>(message.Content);
                    Metrics.Eth66BlockBodiesReceived++;
                    ReportIn(bodiesMsg);
                    HandleBodies(bodiesMsg, size);
                    break;
                case Eth66MessageCode.GetPooledTransactions:
                    GetPooledTransactionsMessage getPooledTxMsg
                        = Deserialize<GetPooledTransactionsMessage>(message.Content);
                    Metrics.Eth66GetPooledTransactionsReceived++;
                    ReportIn(getPooledTxMsg);
                    Handle(getPooledTxMsg);
                    break;
                case Eth66MessageCode.PooledTransactions:
                    PooledTransactionsMessage pooledTxMsg
                        = Deserialize<PooledTransactionsMessage>(message.Content);
                    Metrics.Eth66PooledTransactionsReceived++;
                    ReportIn(pooledTxMsg);
                    Handle(pooledTxMsg.EthMessage);
                    break;
                case Eth66MessageCode.GetReceipts:
                    GetReceiptsMessage getReceiptsMessage = Deserialize<GetReceiptsMessage>(message.Content);
                    Metrics.Eth66GetReceiptsReceived++;
                    ReportIn(getReceiptsMessage);
                    Handle(getReceiptsMessage);
                    break;
                case Eth66MessageCode.Receipts:
                    ReceiptsMessage receiptsMessage = Deserialize<ReceiptsMessage>(message.Content);
                    Metrics.Eth66ReceiptsReceived++;
                    ReportIn(receiptsMessage);
                    Handle(receiptsMessage, size);
                    break;
                case Eth66MessageCode.GetNodeData:
                    GetNodeDataMessage getNodeDataMessage = Deserialize<GetNodeDataMessage>(message.Content);
                    Metrics.Eth66GetNodeDataReceived++;
                    ReportIn(getNodeDataMessage);
                    Handle(getNodeDataMessage);
                    break;
                case Eth66MessageCode.NodeData:
                    NodeDataMessage nodeDataMessage = Deserialize<NodeDataMessage>(message.Content);
                    Metrics.Eth66NodeDataReceived++;
                    ReportIn(nodeDataMessage);
                    Handle(nodeDataMessage, size);
                    break;
                default:
                    base.HandleMessage(message);
                    break;
            }
        }

        private void QueueMessage(ZeroPacket zeroPacket)
        {
            zeroPacket.Retain();
            if (!_incomingMessageQueue.Writer.TryWrite(zeroPacket))
            {
                zeroPacket.SafeRelease();

                // TODO: In almost all case that I found, the queue is full of PooledTransaction message.
                //   to some extend maybe its good that we are disconnecting spammy peer? But its likely
                //   that we need to have another look at how its handled.
                throw new IncomingQueueFullException($"Incoming message queue full. New packet: {zeroPacket.PacketType}, Last packet: {_lastProcessingPacketType}.");
            }
        }

        private async Task StartReceiveLoop()
        {
            await foreach (ZeroPacket message in _incomingMessageQueue.Reader.ReadAllAsync(_cancellationTokenSource.Token))
            {
                try
                {
                    _lastProcessingPacketType = message.PacketType;
                    DoHandleMessage(message);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    if (_logger.IsDebug)
                    {
                        _logger.Error($"Error handling message {message}", exception);
                    }
                    Session.InitiateDisconnect(InitiateDisconnectReason.MessageHandlingException, $"Exception: {exception}");
                }
                finally
                {
                    message.SafeRelease();
                }
            }
        }

        private void Handle(GetBlockHeadersMessage getBlockHeaders)
        {
            V62.Messages.BlockHeadersMessage ethBlockHeadersMessage =
                FulfillBlockHeadersRequest(getBlockHeaders.EthMessage);
            Send(new BlockHeadersMessage(getBlockHeaders.RequestId, ethBlockHeadersMessage));
        }

        private void Handle(GetBlockBodiesMessage getBlockBodies)
        {
            V62.Messages.BlockBodiesMessage ethBlockBodiesMessage =
                FulfillBlockBodiesRequest(getBlockBodies.EthMessage);
            Send(new BlockBodiesMessage(getBlockBodies.RequestId, ethBlockBodiesMessage));
        }

        private void Handle(GetPooledTransactionsMessage getPooledTransactions)
        {
            V65.Messages.PooledTransactionsMessage pooledTransactionsMessage =
                FulfillPooledTransactionsRequest(getPooledTransactions.EthMessage);
            Send(new PooledTransactionsMessage(getPooledTransactions.RequestId, pooledTransactionsMessage));
        }

        private void Handle(GetReceiptsMessage getReceiptsMessage)
        {
            V63.Messages.ReceiptsMessage receiptsMessage =
                FulfillReceiptsRequest(getReceiptsMessage.EthMessage);
            Send(new ReceiptsMessage(getReceiptsMessage.RequestId, receiptsMessage));
        }

        private void Handle(GetNodeDataMessage getNodeDataMessage)
        {
            V63.Messages.NodeDataMessage nodeDataMessage =
                FulfillNodeDataRequest(getNodeDataMessage.EthMessage);
            Send(new NodeDataMessage(getNodeDataMessage.RequestId, nodeDataMessage));
        }

        private void Handle(BlockHeadersMessage message, long size)
        {
            _headersRequests66.Handle(message.RequestId, message.EthMessage.BlockHeaders, size);
        }

        private void HandleBodies(BlockBodiesMessage blockBodiesMessage, long size)
        {
            _bodiesRequests66.Handle(blockBodiesMessage.RequestId, blockBodiesMessage.EthMessage.Bodies, size);
        }

        private void Handle(NodeDataMessage msg, int size)
        {
            _nodeDataRequests66.Handle(msg.RequestId, msg.EthMessage.Data, size);
        }

        private void Handle(ReceiptsMessage msg, long size)
        {
            _receiptsRequests66.Handle(msg.RequestId, msg.EthMessage.TxReceipts, size);
        }

        protected override void Handle(NewPooledTransactionHashesMessage msg)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            _pooledTxsRequestor.RequestTransactionsEth66(Send, msg.Hashes.ToArray());

            stopwatch.Stop();
            if (Logger.IsTrace)
                Logger.Trace($"OUT {Counter:D5} {nameof(NewPooledTransactionHashesMessage)} to {Node:c} " +
                             $"in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }

        protected override async Task<BlockHeader[]> SendRequest(V62.Messages.GetBlockHeadersMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace($"Sending headers request to {Session.Node:c}:");
                Logger.Trace($"  Starting blockhash: {message.StartBlockHash}");
                Logger.Trace($"  Starting number: {message.StartBlockNumber}");
                Logger.Trace($"  Skip: {message.Skip}");
                Logger.Trace($"  Reverse: {message.Reverse}");
                Logger.Trace($"  Max headers: {message.MaxHeaders}");
            }

            GetBlockHeadersMessage msg66 = new() { EthMessage = message };

            return await SendRequestGenericEth66(
                _headersRequests66,
                msg66,
                TransferSpeedType.Headers,
                static (message) => $"{nameof(GetBlockHeadersMessage)} with {message.EthMessage.MaxHeaders} max headers",
                token);
        }

        protected override async Task<BlockBody[]> SendRequest(V62.Messages.GetBlockBodiesMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending bodies request:");
                Logger.Trace($"Blockhashes count: {message.BlockHashes.Count}");
            }

            GetBlockBodiesMessage msg66 = new() { EthMessage = message };
            return await SendRequestGenericEth66(
                _bodiesRequests66,
                msg66,
                TransferSpeedType.Bodies,
                static (message) => $"{nameof(GetBlockBodiesMessage)} with {message.EthMessage.BlockHashes.Count} block hashes",
                token);
        }

        protected override async Task<byte[][]> SendRequest(V63.Messages.GetNodeDataMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending node fata request:");
                Logger.Trace($"Keys count: {message.Hashes.Count}");
            }

            GetNodeDataMessage msg66 = new() { EthMessage = message };
            return await SendRequestGenericEth66(
                _nodeDataRequests66,
                msg66,
                TransferSpeedType.NodeData,
                static (_) => $"{nameof(GetNodeDataMessage)}",
                token);
        }

        protected override async Task<TxReceipt[][]> SendRequest(V63.Messages.GetReceiptsMessage message, CancellationToken token)
        {
            if (Logger.IsTrace)
            {
                Logger.Trace("Sending node fata request:");
                Logger.Trace($"Hashes count: {message.Hashes.Count}");
            }

            GetReceiptsMessage msg66 = new() { EthMessage = message };
            return await SendRequestGenericEth66(
                _receiptsRequests66,
                msg66,
                TransferSpeedType.Receipts,
                static (_) => $"{nameof(GetReceiptsMessage)}",
                token);
        }

        private async Task<TResponse> SendRequestGenericEth66<T66, TRequest, TResponse>(
            MessageDictionary<T66, TRequest, TResponse> messageQueue,
            T66 message,
            TransferSpeedType speedType,
            Func<T66, string> describeRequestFunc,
            CancellationToken token
        )
            where T66 : Eth66Message<TRequest>
            where TRequest : P2PMessage
        {
            Request<T66, TResponse> request = new(message);
            messageQueue.Send(request);

            return await HandleResponse(request, speedType, describeRequestFunc, token);
        }

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _incomingMessageQueue.Writer.Complete();
                base.Dispose();
                Task.Factory.StartNew(FlushQueue);
            }
        }

        private async Task FlushQueue()
        {
            await foreach (ZeroPacket packet in _incomingMessageQueue.Reader.ReadAllAsync(CancellationToken.None))
            {
                packet.SafeRelease();
            }
        }

        public class IncomingQueueFullException : Exception
        {
            public IncomingQueueFullException(string message) : base(message) { }
        }
    }
}
