namespace Amethyst;

public sealed record ProfileProperty(string Name, string Value, string? Signature);
public readonly record struct PlayerPose(double X, double Y, double Z, float Yaw, float Pitch, bool OnGround);
