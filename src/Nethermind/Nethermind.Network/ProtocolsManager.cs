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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Eth.V64;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Les;
using Nethermind.Network.P2P.Subprotocols.Wit;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;

namespace Nethermind.Network
{
    public class ProtocolsManager : IProtocolsManager
    {
        private readonly ConcurrentDictionary<Guid, SyncPeerProtocolHandlerBase> _syncPeers = new();

        private readonly ConcurrentDictionary<Node, ConcurrentDictionary<Guid, ProtocolHandlerBase>> _hangingSatelliteProtocols =
            new();
        
        private readonly ConcurrentDictionary<Guid, ISession> _sessions = new();
        private readonly ISyncPeerPool _syncPool;
        private readonly ISyncServer _syncServer;
        private readonly ITxPool _txPool;
        private readonly IPooledTxsRequestor _pooledTxsRequestor;
        private readonly IDiscoveryApp _discoveryApp;
        private readonly IMessageSerializationService _serializer;
        private readonly IRlpxHost _rlpxHost;
        private readonly INodeStatsManager _stats;
        private readonly IProtocolValidator _protocolValidator;
        private readonly INetworkStorage _peerStorage;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly IDictionary<string, Func<ISession, int, IProtocolHandler>> _protocolFactories;
        private readonly IList<Capability> _capabilities = new List<Capability>();
        public event EventHandler<ProtocolInitializedEventArgs> P2PProtocolInitialized;

        public ProtocolsManager(
            ISyncPeerPool syncPeerPool,
            ISyncServer syncServer,
            ITxPool txPool,
            IPooledTxsRequestor pooledTxsRequestor,
            IDiscoveryApp discoveryApp,
            IMessageSerializationService serializationService,
            IRlpxHost rlpxHost,
            INodeStatsManager nodeStatsManager,
            IProtocolValidator protocolValidator,
            INetworkStorage peerStorage,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _syncPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _syncServer = syncServer ?? throw new ArgumentNullException(nameof(syncServer));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _pooledTxsRequestor = pooledTxsRequestor ?? throw new ArgumentNullException(nameof(pooledTxsRequestor));
            _discoveryApp = discoveryApp ?? throw new ArgumentNullException(nameof(discoveryApp));
            _serializer = serializationService ?? throw new ArgumentNullException(nameof(serializationService));
            _rlpxHost = rlpxHost ?? throw new ArgumentNullException(nameof(rlpxHost));
            _stats = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _protocolValidator = protocolValidator ?? throw new ArgumentNullException(nameof(protocolValidator));
            _peerStorage = peerStorage ?? throw new ArgumentNullException(nameof(peerStorage));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = _logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _protocolFactories = GetProtocolFactories();
            rlpxHost.SessionCreated += SessionCreated;
        }

        private void SessionCreated(object sender, SessionEventArgs e)
        {
            _sessions.TryAdd(e.Session.SessionId, e.Session);
            e.Session.Initialized += SessionInitialized;
            e.Session.Disconnected += SessionDisconnected;
        }

        private void SessionDisconnected(object sender, DisconnectEventArgs e)
        {
            ISession session = (ISession) sender;
            session.Initialized -= SessionInitialized;
            session.Disconnected -= SessionDisconnected;

            if (_syncPeers.TryRemove(session.SessionId, out var removed))
            {
                _syncPool.RemovePeer(removed);
                _txPool.RemovePeer(removed.Node.Id);
                if (session.BestStateReached == SessionState.Initialized)
                {
                    if (_logger.IsDebug) _logger.Debug($"{session.Direction} {session.Node:s} disconnected {e.DisconnectType} {e.DisconnectReason} {e.Details}");
                }
            }

            if (_hangingSatelliteProtocols.TryGetValue(session.Node, out var registrations))
            {
                registrations.TryRemove(session.SessionId, out _);
            }
            
            _sessions.TryRemove(session.SessionId, out session);
        }

        private void SessionInitialized(object sender, EventArgs e)
        {
            ISession session = (ISession) sender;
            InitProtocol(session, Protocol.P2P, session.P2PVersion, true);
        }

        private void InitProtocol(ISession session, string protocolCode, int version, bool addCapabilities = false)
        {
            if (session.State < SessionState.Initialized)
            {
                throw new InvalidOperationException($"{nameof(InitProtocol)} called on {session}");
            }

            if (session.State != SessionState.Initialized)
            {
                return;
            }

            string code = protocolCode.ToLowerInvariant();
            if (!_protocolFactories.TryGetValue(code, out Func<ISession, int, IProtocolHandler> protocolFactory))
            {
                throw new NotSupportedException($"Protocol {code} {version} is not supported");
            }

            IProtocolHandler protocolHandler = protocolFactory(session, version);
            protocolHandler.SubprotocolRequested += (s, e) => InitProtocol(session, e.ProtocolCode, e.Version);
            session.AddProtocolHandler(protocolHandler);
            if (addCapabilities)
            {
                foreach (Capability capability in _capabilities)
                {
                    session.AddSupportedCapability(capability);
                }
            }

            protocolHandler.Init();
        }

        public void AddProtocol(string code, Func<ISession, IProtocolHandler> factory)
        {
            if (_protocolFactories.ContainsKey(code))
            {
                throw new InvalidOperationException($"Protocol {code} was already added.");
            }

            _protocolFactories[code] = (session, _) => factory(session);
        }

        private IDictionary<string, Func<ISession, int, IProtocolHandler>> GetProtocolFactories()
            => new Dictionary<string, Func<ISession, int, IProtocolHandler>>
            {
                [Protocol.P2P] = (session, _) =>
                {
                    P2PProtocolHandler handler = new(session, _rlpxHost.LocalNodeId, _stats, _serializer, _logManager);
                    session.PingSender = handler;
                    InitP2PProtocol(session, handler);

                    return handler;
                },
                [Protocol.Eth] = (session, version) =>
                {
                    var ethHandler = version switch
                    {
                        62 => new Eth62ProtocolHandler(session, _serializer, _stats, _syncServer, _txPool, _logManager),
                        63 => new Eth63ProtocolHandler(session, _serializer, _stats, _syncServer, _txPool, _logManager),
                        64 => new Eth64ProtocolHandler(session, _serializer, _stats, _syncServer, _txPool, _specProvider, _logManager),
                        65 => new Eth65ProtocolHandler(session, _serializer, _stats, _syncServer, _txPool, _pooledTxsRequestor, _specProvider, _logManager),
                        66 => new Eth66ProtocolHandler(session, _serializer, _stats, _syncServer, _txPool, _pooledTxsRequestor, _specProvider, _logManager),
                        _ => throw new NotSupportedException($"Eth protocol version {version} is not supported.")
                    };

                    InitSyncPeerProtocol(session, ethHandler);
                    return ethHandler;
                },
                [Protocol.Wit] = (session, version) =>
                {
                    var handler = version switch
                    {
                        0 => new WitProtocolHandler(session, _serializer, _stats, _syncServer, _logManager),
                        _ => throw new NotSupportedException($"{Protocol.Wit}.{version} is not supported.")
                    };
                    InitSatelliteProtocol(session, handler);

                    return handler;
                },
                [Protocol.Les] = (session, version) =>
                {
                    LesProtocolHandler handler = new(session, _serializer, _stats, _syncServer, _logManager);
                    InitSyncPeerProtocol(session, handler);

                    return handler;
                }
            };

        private void InitSatelliteProtocol(ISession session, ProtocolHandlerBase handler)
        {
            session.Node.EthDetails = handler.Name;
            handler.ProtocolInitialized += (sender, args) =>
            {
                if (!RunBasicChecks(session, handler.ProtocolCode, handler.ProtocolVersion)) return;
                // SyncPeerProtocolInitializedEventArgs typedArgs = (SyncPeerProtocolInitializedEventArgs)args;
                // _stats.ReportSyncPeerInitializeEvent(handler.ProtocolCode, session.Node, new SyncPeerNodeDetails
                // {
                //     ChainId = typedArgs.ChainId,
                //     BestHash = typedArgs.BestHash,
                //     GenesisHash = typedArgs.GenesisHash,
                //     ProtocolVersion = typedArgs.ProtocolVersion,
                //     TotalDifficulty = (BigInteger)typedArgs.TotalDifficulty
                // });
                bool isValid = _protocolValidator.DisconnectOnInvalid(handler.ProtocolCode, session, args);
                if (isValid)
                {
                    var peer = _syncPool.GetPeer(session.Node);
                    if (peer != null)
                    {
                        peer.SyncPeer.RegisterSatelliteProtocol(handler.ProtocolCode, handler);
                        if (handler.IsPriority) _syncPool.SetPeerPriority(session.Node.Id);
                        if (_logger.IsDebug) _logger.Debug($"{handler.ProtocolCode} satellite protocol registered for sync peer {session}.");
                    }
                    else
                    {
                        _hangingSatelliteProtocols.AddOrUpdate(session.Node, 
                            new ConcurrentDictionary<Guid, ProtocolHandlerBase>(new[] {new KeyValuePair<Guid, ProtocolHandlerBase>(session.SessionId, handler)}),
                            (node, dict) =>
                        {
                            dict[session.SessionId] = handler;
                            return dict;
                        });
                        
                        if (_logger.IsDebug) _logger.Debug($"{handler.ProtocolCode} satellite protocol sync peer {session} not found.");
                    }

                    if (_logger.IsTrace) _logger.Trace($"Finalized {handler.ProtocolCode.ToUpper()} protocol initialization on {session} - adding sync peer {session.Node:s}");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {handler.ProtocolCode}{handler.ProtocolVersion} is invalid on {session}");
                }
            };
        }

        private void InitP2PProtocol(ISession session, P2PProtocolHandler handler)
        {
            handler.ProtocolInitialized += (sender, args) =>
            {
                P2PProtocolInitializedEventArgs typedArgs = (P2PProtocolInitializedEventArgs) args;
                if (!RunBasicChecks(session, Protocol.P2P, handler.ProtocolVersion)) return;

                if (handler.ProtocolVersion >= 5)
                {
                    if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode}.{handler.ProtocolVersion} established on {session} - enabling snappy");
                    session.EnableSnappy();
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"{handler.ProtocolCode}.{handler.ProtocolVersion} established on {session} - disabling snappy");
                }

                _stats.ReportP2PInitializationEvent(session.Node, new P2PNodeDetails
                {
                    ClientId = typedArgs.ClientId,
                    Capabilities = typedArgs.Capabilities.ToArray(),
                    P2PVersion = typedArgs.P2PVersion,
                    ListenPort = typedArgs.ListenPort
                });

                AddNodeToDiscovery(session, typedArgs);

                _protocolValidator.DisconnectOnInvalid(Protocol.P2P, session, args);

                if (_logger.IsTrace) _logger.Trace($"Finalized P2P protocol initialization on {session}");
                P2PProtocolInitialized?.Invoke(this, typedArgs);
            };
        }

        private void InitSyncPeerProtocol(ISession session, SyncPeerProtocolHandlerBase handler)
        {
            session.Node.EthDetails = handler.Name;
            handler.ProtocolInitialized += async (sender, args) =>
            {
                if (!RunBasicChecks(session, handler.ProtocolCode, handler.ProtocolVersion)) return;
                SyncPeerProtocolInitializedEventArgs typedArgs = (SyncPeerProtocolInitializedEventArgs)args;
                _stats.ReportSyncPeerInitializeEvent(handler.ProtocolCode, session.Node, new SyncPeerNodeDetails
                {
                    ChainId = typedArgs.ChainId,
                    BestHash = typedArgs.BestHash,
                    GenesisHash = typedArgs.GenesisHash,
                    ProtocolVersion = typedArgs.ProtocolVersion,
                    TotalDifficulty = (BigInteger)typedArgs.TotalDifficulty
                });
                bool isValid = _protocolValidator.DisconnectOnInvalid(handler.ProtocolCode, session, args);
                if (isValid)
                {
                    if (_syncPeers.TryAdd(session.SessionId, handler))
                    {
                        if (_hangingSatelliteProtocols.TryGetValue(handler.Node, out var handlerDictionary))
                        {
                            foreach (KeyValuePair<Guid, ProtocolHandlerBase> registration in handlerDictionary)
                            {
                                handler.RegisterSatelliteProtocol(registration.Value);
                                if (registration.Value.IsPriority) handler.IsPriority = true;
                                if (_logger.IsDebug) _logger.Debug($"{handler.ProtocolCode} satellite protocol registered for sync peer {session}. Sync peer has priority: {handler.IsPriority}");
                            }
                        }
                        
                        await _syncPool.AddPeer(handler);
                        isValid = _protocolValidator.DisconnectOnInvalidFork(_specProvider, handler.ProtocolCode, handler.HeadNumber, session, args);
                        if (isValid)
                        {
                            if (handler.IncludeInTxPool) _txPool.AddPeer(handler);
                            if (_logger.IsDebug) _logger.Debug($"{handler.ClientId} sync peer {session} created.");
                        }
                        else
                        {
                            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {handler.ProtocolCode}{handler.ProtocolVersion} is invalid on {session}");
                        }
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace($"Not able to add a sync peer on {session} for {session.Node:s}");
                        session.InitiateDisconnect(DisconnectReason.AlreadyConnected, "sync peer");
                    }

                    if (_logger.IsTrace) _logger.Trace($"Finalized {handler.ProtocolCode.ToUpper()} protocol initialization on {session} - adding sync peer {session.Node:s}");

                    //Add/Update peer to the storage and to sync manager
                    _peerStorage.UpdateNode(new NetworkNode(session.Node.Id, session.Node.Host, session.Node.Port, _stats.GetOrAdd(session.Node).NewPersistedNodeReputation));
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {handler.ProtocolCode}{handler.ProtocolVersion} is invalid on {session}");
                }
            };
        }

        private bool RunBasicChecks(ISession session, string protocolCode, int protocolVersion)
        {
            if (session.IsClosing)
            {
                if (_logger.IsDebug) _logger.Debug($"|NetworkTrace| {protocolCode}.{protocolVersion} initialized in {session}");
                return false;
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {protocolCode}.{protocolVersion} initialized in {session}");
            return true;
        }

        /// <summary>
        /// In case of IN connection we don't know what is the port node is listening on until we receive the Hello message
        /// </summary>
        private void AddNodeToDiscovery(ISession session, P2PProtocolInitializedEventArgs eventArgs)
        {
            if (eventArgs.ListenPort == 0)
            {
                if (_logger.IsTrace) _logger.Trace($"Listen port is 0, node is not listening: {session}");
                return;
            }

            if (session.Node.Port != eventArgs.ListenPort)
            {
                if (_logger.IsDebug) _logger.Debug($"Updating listen port for {session:s} to: {eventArgs.ListenPort}");
                session.Node.Port = eventArgs.ListenPort;
            }
            
            //In case peer was initiated outside of discovery and discovery is enabled, we are adding it to discovery for future use (e.g. trusted peer)
            _discoveryApp.AddNodeToDiscovery(session.Node);
        }

        public void AddSupportedCapability(Capability capability)
        {
            if (_capabilities.Contains(capability))
            {
                return;
            }

            _capabilities.Add(capability);
        }

        public void SendNewCapability(Capability capability)
        {
            AddCapabilityMessage message = new(capability);
            foreach ((Guid _, ISession session) in _sessions)
            {
                if (session.HasAgreedCapability(capability))
                {
                    continue;
                }

                if (!session.HasAvailableCapability(capability))
                {
                    continue;
                }

                session.DeliverMessage(message);
            }
        }
    }
}
