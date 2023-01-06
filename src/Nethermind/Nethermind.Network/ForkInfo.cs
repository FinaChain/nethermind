// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Force.Crc32;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Network
{
    public class ForkInfo : IForkInfo
    {
        private (ForkActivation Activation, ForkId Id)[] Forks { get; }

        private IBlockTree _blockTree;

        public ForkInfo(ISpecProvider specProvider, Keccak genesisHash, IBlockTree blockTree)
        {
            _blockTree = blockTree;

            ForkActivation[] transitionActivations = specProvider.TransitionActivations;
            Forks = new (ForkActivation Activation, ForkId Id)[transitionActivations.Length + 1];
            byte[] blockNumberBytes = new byte[8];
            uint crc = 0;
            byte[] hash = CalculateHash(ref crc, genesisHash.Bytes);
            // genesis fork activation
            Forks[0] = ((0, null), new ForkId(hash, transitionActivations.Length > 0 ? transitionActivations[0].Activation : 0));
            for (int index = 0; index < transitionActivations.Length; index++)
            {
                ForkActivation forkActivation = transitionActivations[index];
                BinaryPrimitives.WriteUInt64BigEndian(blockNumberBytes, forkActivation.Activation);
                hash = CalculateHash(ref crc, blockNumberBytes);
                Forks[index + 1] = (forkActivation, new ForkId(hash, GetNextActivation(index, transitionActivations)));
            }
        }

        private static byte[] CalculateHash(ref uint crc, byte[] bytes)
        {
            crc = Crc32Algorithm.Append(crc, bytes);
            byte[] forkHash = new byte[4];
            BinaryPrimitives.TryWriteUInt32BigEndian(forkHash, crc);
            return forkHash;
        }

        private static ulong GetNextActivation(int index, ForkActivation[] transitionActivations)
        {
            ulong nextActivation;
            int nextIndex = index + 1;
            if (nextIndex < transitionActivations.Length)
            {
                ForkActivation nextForkActivation = transitionActivations[nextIndex];
                nextActivation = nextForkActivation.Timestamp ?? (ulong)nextForkActivation.BlockNumber;
            }
            else
            {
                nextActivation = 0;
            }

            return nextActivation;
        }

        public ForkId GetForkId(long headNumber, ulong headTimestamp)
        {
            return Forks.TryGetSearchedItem(
                new ForkActivation(headNumber, headTimestamp),
                CompareTransitionOnActivation, out (ForkActivation Activation, ForkId Id) fork)
                ? fork.Id
                : throw new InvalidOperationException("Fork not found");
        }

        private static int CompareTransitionOnActivation(ForkActivation activation, (ForkActivation Activation, ForkId _) transition) =>
            activation.CompareTo(transition.Activation);

        /// <summary>
        /// Verify that the forkid from peer matches our forks. Code is largely copied from Geth.
        /// </summary>
        /// <param name="peerId"></param>
        /// <returns></returns>
        public IForkInfo.ValidationResult ValidateForkId(ForkId peerId)
        {
            // Run the fork checksum validation ruleset:
            //   1. If local and remote FORK_CSUM matches, compare local head to FORK_NEXT.
            //        The two nodes are in the same fork state currently. They might know
            //        of differing future forks, but that's not relevant until the fork
            //        triggers (might be postponed, nodes might be updated to match).
            //      1a. A remotely announced but remotely not passed block is already passed
            //          locally, disconnect, since the chains are incompatible.
            //      1b. No remotely announced fork; or not yet passed locally, connect.
            //   2. If the remote FORK_CSUM is a subset of the local past forks and the
            //      remote FORK_NEXT matches with the locally following fork block number,
            //      connect.
            //        Remote node is currently syncing. It might eventually diverge from
            //        us, but at this current point in time we don't have enough information.
            //   3. If the remote FORK_CSUM is a superset of the local past forks and can
            //      be completed with locally known future forks, connect.
            //        Local node is currently syncing. It might eventually diverge from
            //        the remote, but at this current point in time we don't have enough
            //        information.
            //   4. Reject in all other cases.
            BlockHeader? head = _blockTree.Head?.Header;
            if (head == null) return IForkInfo.ValidationResult.Valid;

            for (int i = 0; i < Forks.Length; i++)
            {
                (ForkActivation forkActivation, ForkId forkId) = Forks[i];
                bool usingTimestamp = forkActivation.Timestamp != null || (i+1 < Forks.Length && Forks[i+1].Activation.Timestamp != null);
                ulong headActivation = (usingTimestamp ? head.Timestamp : (ulong)head.Number);

                // If our head is beyond this fork, continue to the next (we have a dummy
                // fork of maxuint64 as the last item to always fail this check eventually).
                if (i+1 < Forks.Length && headActivation >= Forks[i+1].Activation.Activation) continue;

                // Found the first unpassed fork block, check if our current state matches
                // the remote checksum (rule #1).
                if (Bytes.AreEqual(forkId.ForkHash, peerId.ForkHash))
                {
                    // Fork checksum matched, check if a remote future fork block already passed
                    // locally without the local node being aware of it (rule #1a).
                    if (peerId.Next > 0 && headActivation >= peerId.Next)
                    {
                        return IForkInfo.ValidationResult.IncompatibleOrStale;
                    }
                    // Haven't passed locally a remote-only fork, accept the connection (rule #1b).
                    return IForkInfo.ValidationResult.Valid;
                }

                // The local and remote nodes are in different forks currently, check if the
                // remote checksum is a subset of our local forks (rule #2).
                for (int j = 0; j < i; j++)
                {
                    if (Bytes.AreEqual(Forks[j].Id.ForkHash, peerId.ForkHash))
                    {
                        // Remote checksum is a subset, validate based on the announced next fork
                        if (Forks[j+1].Activation.Activation != peerId.Next)
                        {
                            return IForkInfo.ValidationResult.RemoteStale;
                        }

                        return IForkInfo.ValidationResult.Valid;
                    }
                }

                // Remote chain is not a subset of our local one, check if it's a superset by
                // any chance, signalling that we're simply out of sync (rule #3).
                for (int j = i + 1; j < Forks.Length; j++)
                {
                    if (Bytes.AreEqual(Forks[j].Id.ForkHash, peerId.ForkHash))
                    {
                        // Yay, remote checksum is a superset, ignore upcoming forks
                        return IForkInfo.ValidationResult.Valid;
                    }
                }
                // No exact, subset or superset match. We are on differing chains, reject.
                return IForkInfo.ValidationResult.IncompatibleOrStale;
            }

            throw new InvalidOperationException();
        }
    }
}
