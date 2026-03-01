using Nebula;
using Godot;

namespace Nebula.Utility.Nodes;

[Tool]
public partial class InterestArea3D : Area3D
{
    [Export]
    public bool RemoveInterestOnExit = true;

    [Export]
    public NetNode3D InterestTarget { get; set; }

    [Export]
    public Shape3D Shape
    {
        get => CollisionShape.Shape;
        set
        {
            CollisionShape.Shape = value;
        }
    }

    public CollisionShape3D CollisionShape
    {
        get
        {
            if (_collisionShape == null)
            {
                _collisionShape = GetNode<CollisionShape3D>("CollisionShape3D");
            }
            return _collisionShape;
        }
    }
    private CollisionShape3D _collisionShape;

    [Export(PropertyHint.Layers3DPhysics)]
    public int InterestMask = 0;

    public void OnAreaEntered(Area3D area)
    {
        if (area is not InterestArea3D interestArea) return;
        if (interestArea.InterestTarget == null) return;
        if (!interestArea.InterestTarget.Network.InputAuthority.IsSet) return;

        interestArea.InterestTarget.Network.AddPeerInterest(interestArea.InterestTarget.Network.InputAuthority, InterestMask);
    }

    public void OnAreaExited(Area3D area)
    {
        if (!RemoveInterestOnExit) return;
        if (area is not InterestArea3D interestArea) return;
        if (interestArea.InterestTarget == null) return;
        if (!interestArea.InterestTarget.Network.InputAuthority.IsSet) return;

        interestArea.InterestTarget.Network.RemovePeerInterest(interestArea.InterestTarget.Network.InputAuthority, InterestMask);
    }
}