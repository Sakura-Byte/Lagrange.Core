using Lagrange.Core.Message;
using Lagrange.Core.Message.Entities;
using Lagrange.Milky.Api.Handlers.Message;
using Lagrange.Milky.Converters;
using Lagrange.Milky.Models.Messages;
using Lagrange.Milky.Models.Segments;
using Lagrange.Milky.Serialization;

namespace Lagrange.Milky.Test;

public class FaceSegmentTest
{
    [TestCase("friend")]
    [TestCase("group")]
    public async Task RequestsConvertMixedNativeFaces(string scene)
    {
        const string segments = "\"message\":[{\"type\":\"text\",\"data\":{\"text\":\"before\"}},{\"type\":\"face\",\"data\":{\"face_id\":\"277\"}},{\"type\":\"face\",\"data\":{\"face_id\":\"5\",\"is_large\":false}}]";
        IReadOnlyList<OutgoingSegmentBase> message = scene == "friend"
            ? Serializer.JsonDeserialize<SendPrivateMessageHandler.Request>("{\"user_id\":123," + segments + "}")!.Message
            : Serializer.JsonDeserialize<SendGroupMessageHandler.Request>("{\"group_id\":123," + segments + "}")!.Message;
        var type = scene == "friend" ? MessageType.Private : MessageType.Group;
        var converter = new MilkyConverter(null!, null!, null!);
        var chain = await converter.FromOutgoingSegmentsAsync(message, type, 123);
        Assert.That(chain.Select(x => x.GetType()), Is.EqualTo(new[] { typeof(TextEntity), typeof(FaceEntity), typeof(FaceEntity) }));
        Assert.That(((FaceEntity)chain[1]).FaceId, Is.EqualTo(277));
        Assert.That(((FaceEntity)chain[2]).FaceId, Is.EqualTo(5));
        var incoming = await converter.ToIncomingSegmentsAsync(chain, type, 123);
        var friend = new FriendIncomingMessage { PeerId = 123, SenderId = 123, MessageSeq = 1, Time = 0, Segments = incoming, Friend = null! };
        var json = Serializer.JsonSerialize<IncomingMessageBase>(friend);
        Assert.That(json, Does.Contain("\"type\":\"face\""));
        Assert.That(json, Does.Contain("\"face_id\":\"277\""));
    }

    [TestCase("-1", typeof(FormatException))]
    [TestCase("65536", typeof(OverflowException))]
    [TestCase("旺柴", typeof(FormatException))]
    public void InvalidFaceIdsAreRejected(string id, Type exception)
    {
        var converter = new MilkyConverter(null!, null!, null!);
        Assert.ThrowsAsync(exception, async () =>
            await converter.FromOutgoingSegmentsAsync([new FaceOutgoingSegment { Data = new FaceOutgoingSegmentData { FaceId = id } }], MessageType.Private, 123));
    }

    [Test]
    public void LargeOutgoingFacesAreRejectedExplicitly()
    {
        var converter = new MilkyConverter(null!, null!, null!);
        Assert.ThrowsAsync<NotSupportedException>(async () =>
            await converter.FromOutgoingSegmentsAsync([new FaceOutgoingSegment { Data = new FaceOutgoingSegmentData { FaceId = "318", IsLarge = true } }], MessageType.Private, 123));
    }
}
