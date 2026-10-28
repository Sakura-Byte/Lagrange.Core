using Lagrange.Core.Internal.Packets.Message;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entities;
using Lagrange.Core.Utility;

namespace Lagrange.Core.Test;

public class FaceTest
{
    [TestCase((ushort)0)]
    [TestCase((ushort)5)]
    [TestCase((ushort)9)]
    [TestCase((ushort)259)]
    [TestCase((ushort)260)]
    [TestCase((ushort)277)]
    [TestCase(ushort.MaxValue)]
    public void FaceRoundTripsThroughProtobuf(ushort id)
    {
        IMessageEntity source = new FaceEntity(id);
        var wire = ProtoHelper.Serialize(source.Build()[0]);
        var element = ProtoHelper.Deserialize<Elem>(wire.Span);
        var parsed = (FaceEntity)source.Parse([element], element)!;
        Assert.That(parsed.FaceId, Is.EqualTo(id));
        Assert.That(parsed.IsLargeFace, Is.False);
        if (id < 260) Assert.That(element.Face!.Index, Is.EqualTo(id));
        else Assert.That(element.CommonElem!.ServiceType, Is.EqualTo(33));
    }

    [Test]
    public void ParsesLegacyWireWithoutOldCompatibilityField()
    {
        // Elem field 2 contains Face field 1, index 9 (crying).
        var element = ProtoHelper.Deserialize<Elem>(new byte[] { 0x12, 0x02, 0x08, 0x09 });
        IMessageEntity factory = new FaceEntity();
        var parsed = (FaceEntity)factory.Parse([element], element)!;
        Assert.That(parsed.FaceId, Is.EqualTo(9));
    }

    [Test]
    public void ParsesModernWireAndIgnoresUnrelatedCommonElements()
    {
        IMessageEntity factory = new FaceEntity();
        var element = new Elem { CommonElem = new CommonElem { ServiceType = 33, PbElem = new byte[] { 0x08, 0x95, 0x02 } } };
        Assert.That(((FaceEntity)factory.Parse([element], element)!).FaceId, Is.EqualTo(277));
        element.CommonElem.ServiceType = 48;
        Assert.That(factory.Parse([element], element), Is.Null);
    }

    [Test]
    public void ParsesLargeIncomingFaceWithoutPretendingItCanBeSent()
    {
        IMessageEntity factory = new FaceEntity();
        var element = new Elem { CommonElem = new CommonElem { ServiceType = 37, PbElem = new byte[] { 0x18, 0xBE, 0x02 } } };
        var parsed = (FaceEntity)factory.Parse([element], element)!;
        Assert.That(parsed.FaceId, Is.EqualTo(318));
        Assert.That(parsed.IsLargeFace, Is.True);
        Assert.Throws<NotSupportedException>(() => ((IMessageEntity)parsed).Build());
    }

    [Test]
    public void FactoryAndBuilderIncludeFaces()
    {
        Assert.That(MessageEntityRegistry.Create().OfType<FaceEntity>().Count(), Is.EqualTo(1));
        Assert.That(new MessageBuilder().Text("before").Face(277).Text("after").Build()[1], Is.TypeOf<FaceEntity>());
    }

}
