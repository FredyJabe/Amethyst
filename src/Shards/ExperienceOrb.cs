namespace Amethyst;

public readonly record struct ExperienceOrb(
    int EntityId, Guid Uuid, int Value, double X, double Y, double Z,
    double VelocityY, bool OnGround, long CreatedAtMs);

