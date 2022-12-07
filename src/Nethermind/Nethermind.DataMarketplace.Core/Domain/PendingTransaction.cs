// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class ResourceTransaction
    {
        public string ResourceId { get; }
        public string Type { get; }
        public TransactionInfo Transaction { get; }

        public ResourceTransaction(string resourceId, string type, TransactionInfo transaction)
        {
            ResourceId = resourceId;
            Type = type;
            Transaction = transaction;
        }
    }
}
