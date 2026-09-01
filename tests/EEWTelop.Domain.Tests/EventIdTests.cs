using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Domain.Tests;

[TestClass]
public sealed class EventIdTests
{
    [TestMethod]
    public void CreateTrimsValue()
    {
        EventId id = EventId.Create("  quake-001  ");

        Assert.AreEqual("quake-001", id.Value);
    }

    [TestMethod]
    public void CreateRejectsWhitespace()
    {
        Assert.ThrowsExactly<ArgumentException>(() => EventId.Create("   "));
    }
}
