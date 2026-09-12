// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Threading.Channels;

namespace Kuestenlogik.Bowire.Protocol.TacticalApi.Sample.Services;

/// <summary>
/// Hands each open server-streaming call its own unbounded channel and
/// writes one frame into all of them.
/// </summary>
/// <remarks>
/// All three TacticalAPI services have the same subscribe shape — hold a
/// list of writers behind a lock, push a frame per tick, drop the writer
/// when the caller goes away — and getting the drop wrong leaks a writer
/// per cancelled subscription. Writing that out three times would have
/// meant three chances to get it wrong, so the list, the lock and the
/// removal live here and the services only decide what a frame contains.
/// </remarks>
/// <typeparam name="T">The response message the stream carries.</typeparam>
internal sealed class SubscriberFanout<T>
{
    private readonly object _gate = new();
    private readonly List<ChannelWriter<T>> _writers = [];

    /// <summary>
    /// Open a subscription. Dispose the handle — a <c>using</c> in the
    /// service's stream method — to close it and unregister the writer.
    /// </summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<T>(
            new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            _writers.Add(channel.Writer);
        }
        return new Subscription(this, channel);
    }

    /// <summary>
    /// Offer a frame to every open subscription. Non-blocking: a writer
    /// whose reader has already gone is completed, so the write simply
    /// fails and the subscription's own disposal removes it.
    /// </summary>
    public void Broadcast(T frame)
    {
        ChannelWriter<T>[] writers;
        lock (_gate)
        {
            writers = [.. _writers];
        }
        foreach (var writer in writers)
        {
            writer.TryWrite(frame);
        }
    }

    private void Remove(ChannelWriter<T> writer)
    {
        lock (_gate)
        {
            _writers.Remove(writer);
        }
    }

    /// <summary>One open server-streaming call's end of the fanout.</summary>
    internal sealed class Subscription(SubscriberFanout<T> owner, Channel<T> channel) : IDisposable
    {
        /// <summary>Frames broadcast since this subscription opened.</summary>
        public ChannelReader<T> Reader => channel.Reader;

        public void Dispose()
        {
            owner.Remove(channel.Writer);
            channel.Writer.TryComplete();
        }
    }
}
