using System.Runtime.InteropServices;

namespace G2Overlay.UnrealSDK;

// Estruturas da Unreal Engine 5.7 (Large World Coordinates: vetores em double).
// Tamanhos confirmados pelo dump: FVector 24, FRotator 24, FQuat 32, FTransform 96, FBoxSphereBounds 56.

[StructLayout(LayoutKind.Sequential)]
public struct FVector
{
    public double X;
    public double Y;
    public double Z;

    public FVector(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static FVector operator +(FVector a, FVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static FVector operator -(FVector a, FVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static FVector operator *(FVector a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public readonly double Dot(FVector b) => X * b.X + Y * b.Y + Z * b.Z;
    public readonly double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public readonly double DistanceTo(FVector b) => (this - b).Length;

    public readonly bool IsFinite =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z) &&
        Math.Abs(X) < 1e8 && Math.Abs(Y) < 1e8 && Math.Abs(Z) < 1e8;

    public readonly bool IsZero => X == 0 && Y == 0 && Z == 0;

    public override readonly string ToString() => $"({X:F1}, {Y:F1}, {Z:F1})";
}

[StructLayout(LayoutKind.Sequential)]
public struct FRotator
{
    public double Pitch;
    public double Yaw;
    public double Roll;

    public override readonly string ToString() => $"(P={Pitch:F1}, Y={Yaw:F1}, R={Roll:F1})";
}

[StructLayout(LayoutKind.Sequential)]
public struct FQuat
{
    public double X;
    public double Y;
    public double Z;
    public double W;

    public readonly double SizeSquared => X * X + Y * Y + Z * Z + W * W;
}

/// <summary>FTransform (96 bytes, alinhado em 16): Rotation @0x00, Translation @0x20, Scale3D @0x40.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x60)]
public struct FTransform
{
    [FieldOffset(0x00)] public FQuat Rotation;
    [FieldOffset(0x20)] public FVector Translation;
    [FieldOffset(0x40)] public FVector Scale3D;
}

/// <summary>FBoxSphereBounds (56 bytes): Origin @0x00, BoxExtent @0x18, SphereRadius @0x30.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FBoxSphereBounds
{
    public FVector Origin;
    public FVector BoxExtent;
    public double SphereRadius;
}

/// <summary>Início de FMinimalViewInfo: Location @0x00, Rotation @0x18, FOV @0x30.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x38)]
public struct FMinimalViewInfoHead
{
    [FieldOffset(0x00)] public FVector Location;
    [FieldOffset(0x18)] public FRotator Rotation;
    [FieldOffset(0x30)] public float FOV;
    [FieldOffset(0x34)] public float DesiredFOV;
}

/// <summary>TArray&lt;T&gt;: Data*, Num, Max.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TArrayHeader
{
    public ulong Data;
    public int Num;
    public int Max;

    public readonly bool IsPlausible(int maxCount) =>
        Num >= 0 && Num <= Max && Max <= maxCount && (Num == 0 || Memory.ProcessMemory.IsValidObjectPointer(Data));
}

/// <summary>FName (8 bytes em builds Shipping): ComparisonIndex + Number.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FName
{
    public int ComparisonIndex;
    public int Number;
}
