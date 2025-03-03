// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Handlers;

public class ExchangeCapabilitiesHandler : IHandler<IEnumerable<string>, IEnumerable<string>>
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;
    private readonly IRpcCapabilitiesProvider _engineRpcCapabilitiesProvider;

    public ExchangeCapabilitiesHandler(IRpcCapabilitiesProvider engineRpcCapabilitiesProvider, ISpecProvider specProvider, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(specProvider);
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = logManager.GetClassLogger();
        _engineRpcCapabilitiesProvider = engineRpcCapabilitiesProvider;
    }

    public ResultWrapper<IEnumerable<string>> Handle(IEnumerable<string> methods)
    {
        var capabilities = _engineRpcCapabilitiesProvider.GetEngineCapabilities();
        CheckCapabilities(methods, capabilities);

        return ResultWrapper<IEnumerable<string>>.Success(capabilities.Keys);
    }

    private void CheckCapabilities(IEnumerable<string> methods, IReadOnlyDictionary<string, bool> capabilities)
    {
        var missing = new List<string>();

        foreach (var capability in capabilities)
        {
            var found = false;

            foreach (var method in methods)
                if (method.Equals(capability.Key, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }

            // Warn if not found and capability activated
            if (!found && capability.Value)
                missing.Add(capability.Key);
        }

        if (missing.Any())
        {
            if (_logger.IsWarn) _logger.Warn($"Consensus client missing capabilities: {string.Join(", ", missing)}");
        }
    }
}
