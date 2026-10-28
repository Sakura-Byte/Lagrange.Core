using Lagrange.Core.Internal.Packets.Message;
using Lagrange.Core.Utility;

namespace Lagrange.Core.Message.Entities;

/// <summary>A native QQ system face, including the newer common-element encoding.</summary>
public class FaceEntity : IMessageEntity
{
    public ushort FaceId { get; }

    public bool IsLargeFace { get; private init; }

    public FaceEntity() { }

    public FaceEntity(ushort faceId) => FaceId = faceId;

    Elem[] IMessageEntity.Build()
    {
        if (IsLargeFace) throw new NotSupportedException("Sending animated large faces is not supported.");

        return FaceId < 260
            ? [new Elem { Face = new Face { Index = FaceId } }]
            : [new Elem
            {
                CommonElem = new CommonElem
                {
                    ServiceType = 33,
                    BusinessType = 1,
                    PbElem = ProtoHelper.Serialize(new QSmallFaceExtra { FaceId = FaceId }),
                }
            }];
    }

    IMessageEntity? IMessageEntity.Parse(List<Elem> elements, Elem target)
    {
        if (target.Face is { } face)
            return new FaceEntity(checked((ushort)face.Index));

        if (target.CommonElem is { ServiceType: 33 } small)
            return new FaceEntity(checked((ushort)ProtoHelper.Deserialize<QSmallFaceExtra>(small.PbElem.Span).FaceId));

        if (target.CommonElem is { ServiceType: 37 } large)
            return new FaceEntity(checked((ushort)ProtoHelper.Deserialize<QBigFaceExtra>(large.PbElem.Span).FaceId)) { IsLargeFace = true };

        return null;
    }

    string IMessageEntity.ToPreviewString() => $"[Face:{FaceId}]";
}
