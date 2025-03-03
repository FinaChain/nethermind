// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NodeStatsTests
    {
        private INodeStats _nodeStats;
        private Node _node;

        [SetUp]
        public void Initialize()
        {
            _node = new Node(TestItem.PublicKeyA, "192.1.1.1", 3333);
        }

        [TestCase(TransferSpeedType.Bodies)]
        [TestCase(TransferSpeedType.Headers)]
        [TestCase(TransferSpeedType.Receipts)]
        [TestCase(TransferSpeedType.Latency)]
        [TestCase(TransferSpeedType.NodeData)]
        public void TransferSpeedCaptureTest(TransferSpeedType speedType)
        {
            _nodeStats = new NodeStatsLight(_node, 0.5m);

            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 30);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 51);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 140);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 110);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 133);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 51);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 140);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 110);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 133);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 51);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 140);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 110);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 133);

            var av = _nodeStats.GetAverageTransferSpeed(speedType);
            Assert.AreEqual(122, av);

            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 0);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 0);

            av = _nodeStats.GetAverageTransferSpeed(speedType);
            Assert.AreEqual(30, av);
        }

        [Test]
        public async Task DisconnectDelayTest()
        {
            _nodeStats = new NodeStatsLight(_node);

            var isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed.Result, "before disconnect");

            _nodeStats.AddNodeStatsDisconnectEvent(DisconnectType.Remote, DisconnectReason.Other);
            isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsTrue(isConnDelayed.Result, "just after disconnect");
            Assert.AreEqual(NodeStatsEventType.Disconnect, isConnDelayed.DelayReason);
            await Task.Delay(125);
            isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed.Result, "125ms after disconnect");
        }

        [TestCase(NodeStatsEventType.Connecting, true)]
        [TestCase(NodeStatsEventType.None, false)]
        [TestCase(NodeStatsEventType.ConnectionFailedTargetUnreachable, true)]
        [TestCase(NodeStatsEventType.ConnectionFailed, true)]
        public void DisconnectDelayDueToNodeStatsEvent(NodeStatsEventType eventType, bool connectionDelayed)
        {
            _nodeStats = new NodeStatsLight(_node);

            (bool isConnDelayed, NodeStatsEventType? _) = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed, "before disconnect");

            _nodeStats.AddNodeStatsEvent(eventType);
            (isConnDelayed, _) = _nodeStats.IsConnectionDelayed();
            isConnDelayed.Should().Be(connectionDelayed);
        }

        [TestCase(DisconnectType.Local, DisconnectReason.UselessPeer, true)]
        [TestCase(DisconnectType.Remote, DisconnectReason.ClientQuitting, true)]
        public async Task DisconnectDelayDueToDisconnect(DisconnectType disconnectType, DisconnectReason reason, bool connectionDelayed)
        {
            _nodeStats = new NodeStatsLight(_node);

            (bool isConnDelayed, NodeStatsEventType? _) = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed, "before disconnect");

            _nodeStats.AddNodeStatsDisconnectEvent(disconnectType, reason);
            await Task.Delay(125); // Standard disconnect delay without specific handling
            (isConnDelayed, _) = _nodeStats.IsConnectionDelayed();
            isConnDelayed.Should().Be(connectionDelayed);
        }
    }
}
