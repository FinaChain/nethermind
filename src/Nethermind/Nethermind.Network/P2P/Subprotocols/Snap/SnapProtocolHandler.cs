//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.State.Snap;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Network.P2P.Subprotocols.Snap
{
    public class SnapProtocolHandler : ZeroProtocolHandlerBase, ISnapSyncPeer
    {
        private const int MaxBytesLimit = 2_000_000;
        private const int MinBytesLimit = 20_000;
        public static readonly TimeSpan UpperLatencyThreshold = TimeSpan.FromMilliseconds(2000);
        public static readonly TimeSpan LowerLatencyThreshold = TimeSpan.FromMilliseconds(1000);
        private const double BytesLimitAdjustmentFactor = 2;

        protected SnapServer SyncServer { get; }

        public override string Name => "snap1";
        protected override TimeSpan InitTimeout => Timeouts.Eth;

        public override byte ProtocolVersion => 1;
        public override string ProtocolCode => Protocol.Snap;
        public override int MessageIdSpaceSize => 8;

        private const string DisconnectMessage = "Serving snap data in not implemented in this node.";

        private readonly MessageQueue<GetAccountRangeMessage, AccountRangeMessage> _getAccountRangeRequests;
        private readonly MessageQueue<GetStorageRangeMessage, StorageRangeMessage> _getStorageRangeRequests;
        private readonly MessageQueue<GetByteCodesMessage, ByteCodesMessage> _getByteCodesRequests;
        private readonly MessageQueue<GetTrieNodesMessage, TrieNodesMessage> _getTrieNodesRequests;
        private static readonly byte[] _rootNodePath = { 0 };

        private int _currentBytesLimit = MinBytesLimit;

        public SnapProtocolHandler(ISession session,
            INodeStatsManager nodeStats,
            IMessageSerializationService serializer,
            ILogManager logManager)
            : base(session, nodeStats, serializer, logManager)
        {
            _getAccountRangeRequests = new(Send);
            _getStorageRangeRequests = new(Send);
            _getByteCodesRequests = new(Send);
            _getTrieNodesRequests = new(Send);
        }

        public override event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized;
        public override event EventHandler<ProtocolEventArgs>? SubprotocolRequested
        {
            add { }
            remove { }
        }

        public override void Init()
        {
            ProtocolInitialized?.Invoke(this, new ProtocolInitializedEventArgs(this));
        }

        public override void Dispose()
        {
        }

        public override void HandleMessage(ZeroPacket message)
        {
            int size = message.Content.ReadableBytes;

            switch (message.PacketType)
            {
                case SnapMessageCode.GetAccountRange:
                    GetAccountRangeMessage getAccountRangeMessage = Deserialize<GetAccountRangeMessage>(message.Content);
                    ReportIn(getAccountRangeMessage);
                    Handle(getAccountRangeMessage);
                    break;
                case SnapMessageCode.AccountRange:
                    AccountRangeMessage accountRangeMessage = Deserialize<AccountRangeMessage>(message.Content);
                    ReportIn(accountRangeMessage);
                    Handle(accountRangeMessage, size);
                    break;
                case SnapMessageCode.GetStorageRanges:
                    GetStorageRangeMessage getStorageRangesMessage = Deserialize<GetStorageRangeMessage>(message.Content);
                    ReportIn(getStorageRangesMessage);
                    Handle(getStorageRangesMessage);
                    break;
                case SnapMessageCode.StorageRanges:
                    StorageRangeMessage storageRangesMessage = Deserialize<StorageRangeMessage>(message.Content);
                    ReportIn(storageRangesMessage);
                    Handle(storageRangesMessage, size);
                    break;
                case SnapMessageCode.GetByteCodes:
                    GetByteCodesMessage getByteCodesMessage = Deserialize<GetByteCodesMessage>(message.Content);
                    ReportIn(getByteCodesMessage);
                    Handle(getByteCodesMessage);
                    break;
                case SnapMessageCode.ByteCodes:
                    ByteCodesMessage byteCodesMessage = Deserialize<ByteCodesMessage>(message.Content);
                    ReportIn(byteCodesMessage);
                    Handle(byteCodesMessage, size);
                    break;
                case SnapMessageCode.GetTrieNodes:
                    GetTrieNodesMessage getTrieNodesMessage = Deserialize<GetTrieNodesMessage>(message.Content);
                    ReportIn(getTrieNodesMessage);
                    Handle(getTrieNodesMessage);
                    break;
                case SnapMessageCode.TrieNodes:
                    TrieNodesMessage trieNodesMessage = Deserialize<TrieNodesMessage>(message.Content);
                    ReportIn(trieNodesMessage);
                    Handle(trieNodesMessage, size);
                    break;
            }
        }

        private void Handle(AccountRangeMessage msg, long size)
        {
            Metrics.SnapAccountRangeReceived++;
            _getAccountRangeRequests.Handle(msg, size);
        }

        private void Handle(StorageRangeMessage msg, long size)
        {
            Metrics.SnapStorageRangesReceived++;
            _getStorageRangeRequests.Handle(msg, size);
        }

        private void Handle(ByteCodesMessage msg, long size)
        {
            Metrics.SnapByteCodesReceived++;
            _getByteCodesRequests.Handle(msg, size);
        }

        private void Handle(TrieNodesMessage msg, long size)
        {
            Metrics.SnapTrieNodesReceived++;
            _getTrieNodesRequests.Handle(msg, size);
        }

        private void Handle(GetAccountRangeMessage msg)
        {
            Metrics.SnapGetAccountRangeReceived++;
            var response = FulfillAccountRangeMessage(msg);
            Send(response);
        }

        private void Handle(GetStorageRangeMessage getStorageRangesMessage)
        {
            Metrics.SnapGetStorageRangesReceived++;
            var response = FulfillStorageRangeMessage(getStorageRangesMessage);
            Send(response);
        }

        private void Handle(GetByteCodesMessage getByteCodesMessage)
        {
            Metrics.SnapGetByteCodesReceived++;
            var response = FulfillByteCodesMessage(getByteCodesMessage);
            Send(response);
        }

        private void Handle(GetTrieNodesMessage getTrieNodesMessage)
        {
            Metrics.SnapGetTrieNodesReceived++;
            var response = FulfillTrieNodesMessage(getTrieNodesMessage);
            Send(response);
        }

        public override void DisconnectProtocol(DisconnectReason disconnectReason, string details)
        {
            Dispose();
        }

        protected TrieNodesMessage FulfillTrieNodesMessage(GetTrieNodesMessage getTrieNodesMessage)
        {
            var trieNodes = SyncServer.GetTrieNodes(getTrieNodesMessage.Paths, getTrieNodesMessage.RootHash);
            return new TrieNodesMessage(trieNodes);
        }

        protected AccountRangeMessage FulfillAccountRangeMessage(GetAccountRangeMessage getAccountRangeMessage)
        {

            AccountRange? accountRange = getAccountRangeMessage.AccountRange;
            (PathWithAccount[]? ranges, byte[][]? proofs) = SyncServer.GetAccountRanges(accountRange.RootHash, accountRange.StartingHash,
                accountRange.LimitHash, getAccountRangeMessage.ResponseBytes);
            AccountRangeMessage? response = new() {Proofs = proofs, PathsWithAccounts = ranges};
            return response;
        }
        protected StorageRangeMessage FulfillStorageRangeMessage(GetStorageRangeMessage getStorageRangeMessage)
        {
            StorageRange? storageRange = getStorageRangeMessage.StoragetRange;
            (PathWithStorageSlot[][]? ranges, byte[][]? proofs) = SyncServer.GetStorageRanges(storageRange.RootHash, storageRange.Accounts,
                storageRange.StartingHash, storageRange.LimitHash, getStorageRangeMessage.ResponseBytes);
            StorageRangeMessage? response = new() {Proofs = proofs, Slots = ranges};
            return response;
        }
        protected ByteCodesMessage FulfillByteCodesMessage(GetByteCodesMessage getByteCodesMessage)
        {
            var byteCodes = SyncServer.GetByteCodes(getByteCodesMessage.Hashes);
            return new ByteCodesMessage(byteCodes);
        }

        public async Task<AccountsAndProofs> GetAccountRange(AccountRange range, CancellationToken token)
        {
            var request = new GetAccountRangeMessage()
            {
                AccountRange = range,
                ResponseBytes = _currentBytesLimit
            };

            AccountRangeMessage response = await AdjustBytesLimit(() =>
                SendRequest(request, _getAccountRangeRequests, token));

            Metrics.SnapGetAccountRangeSent++;

            return new AccountsAndProofs() { PathAndAccounts = response.PathsWithAccounts, Proofs = response.Proofs };
        }

        public async Task<SlotsAndProofs> GetStorageRange(StorageRange range, CancellationToken token)
        {
            var request = new GetStorageRangeMessage()
            {
                StoragetRange = range,
                ResponseBytes = _currentBytesLimit
            };

            StorageRangeMessage response = await AdjustBytesLimit(() =>
                SendRequest(request, _getStorageRangeRequests, token));

            Metrics.SnapGetStorageRangesSent++;

            return new SlotsAndProofs() { PathsAndSlots = response.Slots, Proofs = response.Proofs };
        }

        public async Task<byte[][]> GetByteCodes(Keccak[] codeHashes, CancellationToken token)
        {
            var request = new GetByteCodesMessage()
            {
                Hashes = codeHashes,
                Bytes = _currentBytesLimit
            };

            ByteCodesMessage response = await AdjustBytesLimit(() =>
                SendRequest(request, _getByteCodesRequests, token));

            Metrics.SnapGetByteCodesSent++;

            return response.Codes;
        }

        public async Task<byte[][]> GetTrieNodes(AccountsToRefreshRequest request, CancellationToken token)
        {
            PathGroup[] groups = GetPathGroups(request);

            return await GetTrieNodes(request.RootHash, groups, token);
        }

        public async Task<byte[][]> GetTrieNodes(GetTrieNodesRequest request, CancellationToken token)
        {
            return await GetTrieNodes(request.RootHash, request.AccountAndStoragePaths, token);
        }

        private async Task<byte[][]> GetTrieNodes(Keccak rootHash, PathGroup[] groups, CancellationToken token)
        {
            GetTrieNodesMessage reqMsg = new()
            {
                RootHash = rootHash,
                Paths = groups,
                Bytes = _currentBytesLimit
            };

            TrieNodesMessage response = await AdjustBytesLimit(() =>
                SendRequest(reqMsg, _getTrieNodesRequests, token));

            Metrics.SnapGetTrieNodesSent++;

            return response.Nodes;
        }

        private PathGroup[] GetPathGroups(AccountsToRefreshRequest request)
        {
            PathGroup[] groups = new PathGroup[request.Paths.Length];

            for (int i = 0; i < request.Paths.Length; i++)
            {
                AccountWithStorageStartingHash path = request.Paths[i];
                // we want the storage root for the account and {0} is the path to the root node
                groups[i] = new PathGroup() { Group = new[] { path.PathAndAccount.Path.Bytes, _rootNodePath } };
            }

            return groups;
        }

        private async Task<TOut> SendRequest<TIn, TOut>(TIn msg, MessageQueue<TIn, TOut> requestQueue, CancellationToken token)
            where TIn : SnapMessageBase
            where TOut : SnapMessageBase
        {
            return await SendRequestGeneric(
                requestQueue,
                msg,
                TransferSpeedType.SnapRanges,
                static (_) => $"{nameof(TIn)}",
                token);
        }

        /// <summary>
        /// Adjust the _currentBytesLimit depending on the latency of the request and if the request failed.
        /// </summary>
        /// <param name="func"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        private async Task<T> AdjustBytesLimit<T>(Func<Task<T>> func)
        {
            // Record bytes limit so that in case multiple concurrent request happens, we do not multiply the
            // limit on top of other adjustment, so only the last adjustment will stick, which is fine.
            int startingBytesLimit = _currentBytesLimit;
            bool failed = false;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                return await func();
            }
            catch (Exception)
            {
                failed = true;
                throw;
            }
            finally
            {
                sw.Stop();
                if (failed)
                {
                    _currentBytesLimit = MinBytesLimit;
                }
                else if (sw.Elapsed < LowerLatencyThreshold)
                {
                    _currentBytesLimit = Math.Min((int)(startingBytesLimit * BytesLimitAdjustmentFactor), MaxBytesLimit);
                }
                else if (sw.Elapsed > UpperLatencyThreshold && startingBytesLimit > MinBytesLimit)
                {
                    _currentBytesLimit = (int)(startingBytesLimit / BytesLimitAdjustmentFactor);
                }
            }
        }

    }
}
