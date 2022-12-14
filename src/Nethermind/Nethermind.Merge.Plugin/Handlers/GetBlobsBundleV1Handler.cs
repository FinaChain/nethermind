// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data.V1;

namespace Nethermind.Merge.Plugin.Handlers.V1;

/// <summary>
/// engine_getBlobsBundleV1
///
/// Given a 8 byte payload_id, it returns blobs and kzgs of the most recent version of an execution payload that is available by the time of the call or responds with an error.
///
/// <see cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#engine_getblobsbundlev1"/>
/// </summary>
/// <remarks>
/// This call must be responded immediately. An exception would be the case when no version of the payload is ready yet and in this case there might be a slight delay before the response is done.
/// Execution client should create a payload with empty transaction set to be able to respond as soon as possible.
/// If there were no prior engine_preparePayload call with the corresponding payload_id or the process of building a payload has been cancelled due to the timeout then execution client must respond with error message.
/// Execution client may stop the building process with the corresponding payload_id value after serving this call.
/// </remarks>
public class GetBlobsBundleV1Handler : IAsyncHandler<byte[], BlobsBundleV1?>
{
    private readonly IPayloadPreparationService _payloadPreparationService;
    private readonly ILogger _logger;

    public GetBlobsBundleV1Handler(IPayloadPreparationService payloadPreparationService, ILogManager logManager)
    {
        _payloadPreparationService = payloadPreparationService;
        _logger = logManager.GetClassLogger();
    }

    public async Task<ResultWrapper<BlobsBundleV1?>> HandleAsync(byte[] payloadId)
    {
        string payloadStr = payloadId.ToHexString(true);
        Block? block = (await _payloadPreparationService.GetPayload(payloadStr))?.CurrentBestBlock;

        if (block is null)
        {
            // The call MUST return -38001: Unknown payload error if the build process identified by the payloadId does not exist.
            if (_logger.IsWarn) _logger.Warn($"Block production for payload with id={payloadId.ToHexString()} failed - unknown payload.");
            return ResultWrapper<BlobsBundleV1?>.Fail("unknown payload", MergeErrorCodes.UnknownPayload);
        }

        if (_logger.IsInfo) _logger.Info($"GetBlobsBundleV1 result: {string.Join(", ", block.Transactions.Select(x=>x.BlobVersionedHashes).SelectMany(x => x!).Select(x=>x.ToHexString()))} blobs.");

        Metrics.GetBlobsBundleRequests++;
        Metrics.NumberOfTransactionsInGetBlobsBundle = block.Transactions.Length;
        return ResultWrapper<BlobsBundleV1?>.Success(new BlobsBundleV1(block));
    }
}
