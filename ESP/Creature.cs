using G2Overlay.UnrealSDK;

namespace G2Overlay.ESP;

/// <summary>
/// Criatura rastreada. Ponteiros de componentes são lidos uma vez (no scan);
/// posição, bounds e vida são atualizados a cada frame.
/// </summary>
public sealed class Creature
{
    // ---------------- estáticos (scan)
    public ulong Actor;
    public ulong ClassPtr;
    public ClassInfo Class = null!;
    public ulong RootComponent;
    public ulong Mesh;
    public ulong Capsule;
    public ulong HealthComponent;
    public ulong TeamComponent;
    public string Name = "";
    public string Team = "";
    public float HealthMultiplier = 1f;
    public float CapsuleHalfHeight;
    public float CapsuleRadius;
    public bool IsPlayer;
    public ulong TameableComponent;

    /// <summary>Classe com "Buggy" ou "Pet" como parte do nome (ex.: BP_Ant_Soldier_Augusta_Buggy_C).</summary>
    public bool IsTamedByName;

    /// <summary>Pontos fracos: componentes de cena com "weak" no nome (ex.: LeftEyeWeakspot, RightLegWeakpoint1).</summary>
    public ulong[] WeakSpots = Array.Empty<ulong>();
    public string[] WeakSpotNames = Array.Empty<string>();

    // ---------------- dinâmicos (frame)
    public FVector Location;
    public FVector BoxCenter;
    public FVector BoxExtent;
    public float MaxHealth;
    public float CurrentHealth;
    public byte HealthState;
    public double DistanceMeters;
    public bool Visible;

    /// <summary>Domada/aliada: TameData.TameState == Tame, ou classe de buggy/pet.</summary>
    public bool IsTamed;

    /// <summary>Posição de cada ponto fraco neste frame (ComponentToWorld.Translation) e se a leitura é válida.</summary>
    public FVector[] WeakSpotPos = Array.Empty<FVector>();
    public bool[] WeakSpotValid = Array.Empty<bool>();
    public bool HasWeakSpots => WeakSpots.Length > 0;

    public float HealthRatio => MaxHealth > 0 ? Math.Clamp(CurrentHealth / MaxHealth, 0f, 1f) : 0f;
    public bool IsDead => HealthState == 2;
    public bool IsIncapacitated => HealthState == 1;
}
