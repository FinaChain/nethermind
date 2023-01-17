// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.Tracing;
using System.Linq;
using Microsoft.Extensions.Logging.EventSource;
using Prometheus;

namespace Nethermind.Monitoring;

public class LOHListener: EventListener
{
    private Histogram LargeObjectAllocation = Prometheus.Metrics.CreateHistogram("loh_listener_large_object_allcation",
        "LOH",
        new HistogramConfiguration()
        {
            LabelNames = new[] { "type" },
            Buckets = Histogram.PowersOfTenDividedBuckets(0, 9, 10)
        });

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        base.OnEventSourceCreated(eventSource);

        Console.WriteLine($"The event name {eventSource.Name}");
        // base.EnableEvents(eventSource, EventLevel.Verbose);

        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        base.OnEventWritten(eventData);

        if (eventData.EventName == "GCAllocationTick_V4" && (UInt32)eventData.Payload[1] == 1)
        {
            // Console.WriteLine($"Got event {string.Join(", ", eventData.PayloadNames.Select((it, idx) => $"{idx}->{it}"))} {string.Join(", ", eventData.Payload.Select((it, idx) => $"{idx}->{it}"))}");
            LargeObjectAllocation.WithLabels(eventData.Payload[5].ToString()).Observe((UInt32) eventData.Payload[0]);
        }
    }
}
