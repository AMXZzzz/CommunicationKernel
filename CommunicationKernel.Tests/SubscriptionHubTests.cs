using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Engine.Router;
using CommunicationKernel.Engine.Router.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

[TestClass]
public sealed class SubscriptionHubTests {

    [TestMethod]
    public async Task PublishAsync_ShouldDeliverToMatchingSubscribers() {
        var hub = new SubscriptionHub();
        var topic = new SubscriptionTopic("device", "online", "r1");
        var received = new List<string>();

        hub.Subscribe(topic, async (payload, ct) => {
            await Task.Yield();
            received.Add((string)payload);
        });

        await hub.PublishAsync(topic, "hello", CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "hello" }, received);
    }

    [TestMethod]
    public async Task Unsubscribe_ShouldStopReceivingEvents() {
        var hub = new SubscriptionHub();
        var topic = new SubscriptionTopic("variable", "changed");
        int count = 0;

        Guid id = hub.Subscribe(topic, (payload, ct) => {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        Assert.IsTrue(hub.Unsubscribe(id));
        await hub.PublishAsync(topic, 123, CancellationToken.None);

        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task PublishAsync_WhenOneHandlerThrows_ShouldContinueOthers() {
        var hub = new SubscriptionHub();
        var topic = new SubscriptionTopic("route", "refresh", "r1");
        int okCount = 0;

        hub.Subscribe(topic, (_, ct) => throw new InvalidOperationException("boom"));
        hub.Subscribe(topic, (payload, ct) => {
            Interlocked.Increment(ref okCount);
            return Task.CompletedTask;
        });

        await hub.PublishAsync(topic, new object(), CancellationToken.None);

        Assert.AreEqual(1, okCount);
    }

    [TestMethod]
    public async Task Subscribe_Concurrent_ShouldRemainSafe() {
        var hub = new SubscriptionHub();
        var topic = new SubscriptionTopic("device", "snap");
        var ids = new ConcurrentBag<Guid>();

        await Task.WhenAll(
            Task.Run(() => ids.Add(hub.Subscribe(topic, (_, ct) => Task.CompletedTask))),
            Task.Run(() => ids.Add(hub.Subscribe(topic, (_, ct) => Task.CompletedTask))),
            Task.Run(() => ids.Add(hub.Subscribe(topic, (_, ct) => Task.CompletedTask))));

        Assert.HasCount(3, ids);
        Assert.AreEqual(3, hub.Count);
    }
}
