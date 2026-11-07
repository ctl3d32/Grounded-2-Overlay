using G2Overlay.Memory;
using G2Overlay.Utils;

namespace G2Overlay.UnrealSDK;

/// <summary>
/// Detecta e valida em tempo de execução os membros NÃO refletidos (que o dump não lista):
///   ULevel::Actors, USceneComponent::ComponentToWorld e USceneComponent::Bounds.
/// Nada é assumido: cada candidato é testado contra dados conhecidos (propriedades refletidas).
/// </summary>
public sealed class LayoutDetector
{
    private const string Cat = "Layout";

    private readonly ProcessMemory _mem;
    private readonly ObjectReader _obj;

    public bool ActorsValidated { get; private set; }
    public bool TransformValidated { get; private set; }
    public bool BoundsValidated { get; private set; }

    public LayoutDetector(ProcessMemory mem, ObjectReader obj)
    {
        _mem = mem;
        _obj = obj;
    }

    public void Reset()
    {
        ActorsValidated = TransformValidated = BoundsValidated = false;
    }

    // ------------------------------------------------------------------ ULevel::Actors

    public bool DetectLevelActors(ulong level)
    {
        if (ActorsValidated) return true;
        if (!ProcessMemory.IsValidObjectPointer(level)) return false;

        int[] candidates = { Off.Level_Actors, 0xA0, 0x98, 0x90, 0xA8, 0xB0, 0x88, 0xB8, 0x80 };
        Span<ulong> sample = stackalloc ulong[32];
        foreach (int off in candidates.Distinct())
        {
            if (!_mem.TryRead(level + (ulong)off, out TArrayHeader arr)) continue;
            if (arr.Num < 1 || !arr.IsPlausible(2_000_000)) continue;

            int n = Math.Min(arr.Num, sample.Length);
            var s = sample[..n];
            if (!_mem.ReadArray(arr.Data, s)) continue;

            int nonNull = 0, actors = 0;
            foreach (ulong p in s)
            {
                if (p == 0) continue;
                nonNull++;
                if (_obj.GetObjectClassInfo(p) is { IsActor: true }) actors++;
            }

            if (nonNull > 0 && actors >= Math.Max(1, (int)(nonNull * 0.8)))
            {
                if (off != Off.Level_Actors)
                    Logger.Warn(Cat, $"ULevel.Actors encontrado em 0x{off:X} (padrão 0x{Off.Level_Actors:X}).");
                Off.Set("Level.Actors", off);
                ActorsValidated = true;
                Logger.Info(Cat, $"ULevel.Actors validado em 0x{off:X}: Num={arr.Num}, {actors}/{nonNull} amostras são AActor.");
                return true;
            }
        }

        Logger.Warn(Cat, "ULevel.Actors ainda não validado (nível sem atores ou layout diferente).");
        return false;
    }

    // ------------------------------------------------------------------ USceneComponent::ComponentToWorld

    /// <summary>
    /// Usa um componente raiz SEM AttachParent: nesse caso ComponentToWorld == (RelativeRotation, RelativeLocation, RelativeScale3D).
    /// </summary>
    public bool DetectComponentToWorld(ulong rootComponent)
    {
        if (TransformValidated) return true;
        if (!ProcessMemory.IsValidObjectPointer(rootComponent)) return false;
        if (_mem.ReadPtr(rootComponent + (ulong)Off.SceneComponent_AttachParent) != 0) return false;

        var rel = _mem.Read<FVector>(rootComponent + (ulong)Off.SceneComponent_RelativeLocation);
        var scale = _mem.Read<FVector>(rootComponent + (ulong)Off.SceneComponent_RelativeScale3D);
        if (!rel.IsFinite || rel.IsZero || !scale.IsFinite || scale.IsZero) return false;

        var candidates = new List<int> { Off.SceneComponent_ComponentToWorld };
        for (int o = 0x1B0; o <= 0x260; o += 0x10) candidates.Add(o);

        foreach (int off in candidates.Distinct())
        {
            if (!_mem.TryRead(rootComponent + (ulong)off, out FTransform t)) continue;
            if (Math.Abs(t.Rotation.SizeSquared - 1.0) > 0.01) continue;
            if (!t.Translation.IsFinite || !t.Scale3D.IsFinite) continue;
            if (Math.Abs(t.Translation.X - rel.X) > 1.0 || Math.Abs(t.Translation.Y - rel.Y) > 1.0 || Math.Abs(t.Translation.Z - rel.Z) > 1.0) continue;
            if (Math.Abs(t.Scale3D.X - scale.X) > 0.01 || Math.Abs(t.Scale3D.Y - scale.Y) > 0.01 || Math.Abs(t.Scale3D.Z - scale.Z) > 0.01) continue;

            if (off != Off.SceneComponent_ComponentToWorld)
                Logger.Warn(Cat, $"ComponentToWorld encontrado em 0x{off:X} (padrão 0x{Off.SceneComponent_ComponentToWorld:X}).");
            Off.Set("SceneComponent.ComponentToWorld", off);
            TransformValidated = true;
            Logger.Info(Cat, $"USceneComponent.ComponentToWorld validado em 0x{off:X} (T={t.Translation} == RelativeLocation={rel}).");
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ USceneComponent::Bounds

    /// <summary>
    /// Valida FBoxSphereBounds no componente de mesh: origem próxima ao ator, extents positivos
    /// e SphereRadius coerente com |BoxExtent|.
    /// </summary>
    public bool DetectBounds(ulong meshComponent, FVector actorLocation)
    {
        if (BoundsValidated) return true;
        if (!ProcessMemory.IsValidObjectPointer(meshComponent)) return false;

        var candidates = new List<int> { Off.SceneComponent_Bounds };
        if (TransformValidated)
        {
            candidates.Add(Off.SceneComponent_ComponentToWorld - 0x38);
            candidates.Add(Off.SceneComponent_ComponentToWorld - 0x40);
        }

        // 0x110–0x148: lacuna entre ClientAttachedChildren e RelativeLocation; 0x1B8–0x1F0: antes do FTransform.
        for (int o = 0x100; o <= 0x260; o += 8) candidates.Add(o);

        foreach (int off in candidates.Distinct())
        {
            if (TransformValidated && off + 0x38 > Off.SceneComponent_ComponentToWorld && off < Off.SceneComponent_ComponentToWorld + 0x60)
                continue; // sobrepõe o FTransform
            if (!_mem.TryRead(meshComponent + (ulong)off, out FBoxSphereBounds b)) continue;
            if (!IsPlausibleBounds(b, actorLocation)) continue;
            // Não pode cair sobre membros refletidos conhecidos.
            if (Overlaps(off, 0x38, Off.SceneComponent_RelativeLocation, 0x48) || Overlaps(off, 0x38, Off.SceneComponent_AttachParent, 0x8)) continue;

            if (off != Off.SceneComponent_Bounds)
                Logger.Warn(Cat, $"Bounds encontrado em 0x{off:X} (padrão 0x{Off.SceneComponent_Bounds:X}).");
            Off.Set("SceneComponent.Bounds", off);
            BoundsValidated = true;
            Logger.Info(Cat, $"USceneComponent.Bounds validado em 0x{off:X}: Origin={b.Origin} Extent={b.BoxExtent} R={b.SphereRadius:F1}.");
            return true;
        }

        return false;
    }

    private static bool Overlaps(int a, int aLen, int b, int bLen) => a < b + bLen && b < a + aLen;

    public static bool IsPlausibleBounds(in FBoxSphereBounds b, FVector actorLocation)
    {
        if (!b.Origin.IsFinite || !b.BoxExtent.IsFinite || !double.IsFinite(b.SphereRadius)) return false;
        var e = b.BoxExtent;
        if (e.X <= 0.1 || e.Y <= 0.1 || e.Z <= 0.1 || e.X > 50000 || e.Y > 50000 || e.Z > 50000) return false;
        if (b.Origin.DistanceTo(actorLocation) > 5000) return false;
        double maxE = Math.Max(e.X, Math.Max(e.Y, e.Z));
        double len = e.Length;
        return b.SphereRadius >= maxE * 0.98 && b.SphereRadius <= len * 1.02 + 1.0;
    }
}
