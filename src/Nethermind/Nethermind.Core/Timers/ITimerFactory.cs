// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System;

namespace Nethermind.Core.Timers
{
    public interface ITimerFactory
    {
        ITimer CreateTimer(TimeSpan interval);
    }
}
