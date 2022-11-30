// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages
{
    public class GetPooledTransactionsMessageSerializer : HashesMessageSerializer<GetPooledTransactionsMessage>
    {
        public override GetPooledTransactionsMessage Deserialize(IByteBuffer byteBuffer)
        {
            Keccak[] hashes = DeserializeHashes(byteBuffer);
            return new GetPooledTransactionsMessage(hashes);
        }
    }
}
