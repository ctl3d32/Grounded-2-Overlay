using System.Globalization;
using System.Text.Json;
using G2Overlay.Utils;

namespace G2Overlay.UnrealSDK;

/// <summary>Definição de um offset: chave "Classe.Membro", origem e valor padrão.</summary>
public sealed record OffsetDef(string Key, string Class, string? Property, int Default, OffsetSource Source, string Description);

public enum OffsetSource
{
    /// <summary>Propriedade refletida (UPROPERTY) — importada automaticamente do dump por nome.</summary>
    Reflected,

    /// <summary>Layout fixo da engine (UObjectBase, FNamePool…), igual em todas as builds UE 5.x.</summary>
    Engine,

    /// <summary>Membro não refletido — detectado e validado em tempo de execução (LayoutDetector).</summary>
    RuntimeDetected,
}

/// <summary>
/// Tabela de offsets usada pelo overlay. Os valores padrão vêm do dump
/// Grounded2Steam-Win64-Shipping-dump-20261030-221514.jsonl (UE 5.7, Augusta-CL-3207493).
/// A tabela é carregada de Config/offsets.json e pode ser regenerada automaticamente a partir de um novo dump.
/// </summary>
public static class Off
{
    private const string Cat = "Offsets";

    // ---------------- Engine (layout fixo UE 5.x)
    public static int UObject_ClassPrivate;
    public static int UObject_NamePrivate;
    public static int UObject_OuterPrivate;
    public static int UStruct_SuperStruct;
    public static int FNamePool_Blocks;
    public static int FUObjectArray_NumElements;
    public static int FUObjectArray_ObjObjects;
    public static int UObject_ObjectFlags;
    public static int UObject_InternalIndex;
    public static int SurvivalDamageType_bCanPerfectDodge;
    public static int SurvivalDamageType_bCanBlock;
    public static int SurvivalCharacter_DodgeComponent;
    public static int DodgeComponent_PerfectDodgeWindow;
    public static int HealthComponent_LastDamageInfo;
    public static int HealthComponent_ColliderConfigs;
    public static int SurvivalCharacter_StatusEffectComponent;
    public static int StatusEffectComponent_StatusEffects;
    public static int StatusEffectComponent_InfiniteDefaultStatusEffects;
    public static int StatusEffect_RowHandle;
    public static int StatusEffectData_Type;
    public static int StatusEffectData_Value;
    public static int StatusEffectData_DamageTypeFlags;
    public static int StatusEffectData_DamageType;
    public static int HealthColliderConfig_Size;
    public static int HealthColliderConfig_ComponentName;
    public static int HealthColliderConfig_DamageMultiplier;
    public static int HealthColliderConfig_DamageTypeFlags;
    public static int HealthColliderConfig_DamageType;
    public static int DamageInfo_DamageType;
    public static int DamageInfo_BlockType;

    // ---------------- Mundo / jogador
    public static int World_PersistentLevel;
    public static int World_GameState;
    public static int World_Levels;
    public static int World_OwningGameInstance;
    public static int Level_Actors;
    public static int Level_OwningWorld;
    public static int GameInstance_LocalPlayers;
    public static int Player_PlayerController;
    public static int PlayerController_AcknowledgedPawn;
    public static int PlayerController_PlayerCameraManager;
    public static int PlayerCameraManager_CameraCachePrivate;
    public static int CameraCacheEntry_POV;
    public static int MinimalViewInfo_Location;
    public static int MinimalViewInfo_Rotation;
    public static int MinimalViewInfo_FOV;

    // ---------------- Atores / componentes
    public static int Actor_RootComponent;
    public static int SceneComponent_AttachParent;
    public static int SceneComponent_RelativeLocation;
    public static int SceneComponent_RelativeScale3D;
    public static int SceneComponent_Bounds;
    public static int SceneComponent_ComponentToWorld;
    public static int Character_Mesh;
    public static int Character_CapsuleComponent;
    public static int CapsuleComponent_CapsuleHalfHeight;
    public static int CapsuleComponent_CapsuleRadius;

    // ---------------- Grounded 2 (módulo "Maine")
    public static int SurvivalCharacter_CharacterData;
    public static int SurvivalCharacter_HealthComponent;
    public static int SurvivalCharacter_TeamComponent;
    public static int HealthComponent_HealthState;
    public static int HealthComponent_MaxHealth;
    public static int HealthComponent_CurrentDamage;
    public static int HealthComponent_NewGamePlusHealthMultiplier;
    public static int HealthComponent_CustomGameModeHealthMultiplier;
    public static int HealthComponent_CustomPropertyHealthMultiplier;
    public static int TeamComponent_TeamDataTable;
    public static int DataTableRowHandle_RowName;
    public static int HarvestNode_NodeData;
    public static int HarvestNode_HealthComponent;
    public static int MilkNugget_MolarType;
    public static int SurvivalCharacter_MotionWarpingAttackTarget;
    public static int SurvivalCharacter_BlockComponent;
    public static int SurvivalCharacter_CurrentAttackIsRanged;
    public static int BlockComponent_PerfectBlockWindow;
    public static int SkeletalMeshComponent_AnimScriptInstance;
    public static int AnimInstance_MontageInstances;
    public static int AnimMontageInstance_Montage;
    public static int AnimMontageInstance_bPlaying;
    public static int AnimMontageInstance_Position;
    public static int AnimMontageInstance_PlayRate;
    public static int AnimSequenceBase_Notifies;
    public static int AnimSequenceBase_SequenceLength;
    public static int AnimNotifyEvent_Size;
    public static int AnimNotifyEvent_LinkMethod;
    public static int AnimNotifyEvent_SegmentBeginTime;
    public static int AnimNotifyEvent_SegmentLength;
    public static int AnimNotifyEvent_LinkValue;
    public static int AnimNotifyEvent_TriggerTimeOffset;
    public static int AnimNotifyEvent_Notify;
    public static int AnimNotifyEvent_NotifyStateClass;
    public static int AnimNotifyEvent_Duration;
    public static int AnimSequenceBase_RateScale;
    public static int AnimMontage_SlotAnimTracks;
    public static int SlotAnimationTrack_Size;
    public static int SlotAnimationTrack_AnimTrack;
    public static int AnimTrack_AnimSegments;
    public static int AnimSegment_Size;
    public static int AnimSegment_AnimReference;
    public static int AnimSegment_StartPos;
    public static int AnimSegment_AnimStartTime;
    public static int AnimSegment_AnimEndTime;
    public static int AnimSegment_AnimPlayRate;
    public static int AnimSegment_LoopingCount;
    public static int ScienceCollectable_ScienceAmount;
    public static int ScienceCollectable_LiteData;
    public static int ScienceLiteData_ScienceAmount;
    public static int SpawnedItem_PlacedItemData;
    public static int SpawnedItem_Item;

    // ---------------- Versão leve dos atores (sistema de LOD do Grounded: BaseLODActor é um UObject, não um ator)
    public static int SurvivalGameState_LODActorManagerComponent;
    public static int LODActorManagerComponent_LODActors;
    public static int BaseLODActor_FullActor;
    public static int BaseLODActor_ProxyActor;
    public static int BaseLODActor_LODComponents;
    public static int BaseLODActor_Location;
    public static int ProxySpawnedItemActor_Item;
    public static int ScanSwitch_bScanned;
    public static int BaseLODActor_LiteData;
    public static int LootBag_InventoryComponent;
    public static int InventoryComponent_Items;
    public static int ProxyLootBagActor_Items;
    public static int Item_ItemDataRowHandle;
    public static int SurvivalCharacter_TameableComponent;
    public static int TameableComponent_TameData;
    public static int TamedCreatureData_TameState;
    public static int BossManager_bBossActive;
    public static int Actor_BlueprintCreatedComponents;
    public static int Actor_InstanceComponents;

    public static readonly OffsetDef[] Defs =
    {
        new("UObject.ClassPrivate", "UObjectBase", null, 0x10, OffsetSource.Engine, "UClass* do objeto"),
        new("UObject.NamePrivate", "UObjectBase", null, 0x18, OffsetSource.Engine, "FName do objeto"),
        new("UObject.OuterPrivate", "UObjectBase", null, 0x20, OffsetSource.Engine, "UObject* Outer"),
        new("UStruct.SuperStruct", "UStruct", null, 0x40, OffsetSource.RuntimeDetected, "UStruct* SuperStruct (herança de classes)"),
        new("FNamePool.Blocks", "FNamePool", null, 0x10, OffsetSource.RuntimeDetected, "uint8* Blocks[] do FNameEntryAllocator"),
        new("FUObjectArray.NumElements", "FUObjectArray", null, 0x24, OffsetSource.Engine, "ObjObjects(0x10).NumElements(0x14)"),
        new("FUObjectArray.ObjObjects", "FUObjectArray", null, 0x10, OffsetSource.Engine, "FUObjectItem** (chunks de 64K itens)"),
        new("UObject.ObjectFlags", "UObjectBase", null, 0x8, OffsetSource.Engine, "EObjectFlags (RF_ClassDefaultObject = 0x10)"),
        new("UObject.InternalIndex", "UObjectBase", null, 0xC, OffsetSource.Engine, "índice em GObjects"),
        new("SurvivalDamageType.bCanPerfectDodge", "SurvivalDamageType", "bCanPerfectDodge", 0x64, OffsetSource.Reflected, "bool"),
        new("SurvivalDamageType.bCanBlock", "SurvivalDamageType", "bCanBlock", 0x65, OffsetSource.Reflected, "bool: false = ataque imbloqueável"),
        new("SurvivalCharacter.DodgeComponent", "SurvivalCharacter", "DodgeComponent", 0x13E8, OffsetSource.Reflected, "UDodgeComponent*"),
        new("SurvivalCharacter.StatusEffectComponent", "SurvivalCharacter", "StatusEffectComponent", 0x13F0, OffsetSource.Reflected, "UStatusEffectComponent*"),
        new("StatusEffectComponent.StatusEffects", "StatusEffectComponent", "StatusEffects", 0x1D0, OffsetSource.Reflected, "TArray<UStatusEffect*>"),
        new("StatusEffectComponent.InfiniteDefaultStatusEffects", "StatusEffectComponent", "InfiniteDefaultStatusEffects", 0x1E0, OffsetSource.Reflected, "TArray<UStatusEffect*> (permanentes: fraquezas/resistências)"),
        new("StatusEffect.StatusEffectRowHandle", "StatusEffect", "StatusEffectRowHandle", 0x70, OffsetSource.Reflected, "FDataTableRowHandle (DataTable + RowName)"),
        new("StatusEffectData.Type", "StatusEffectData", "Type", 0x30, OffsetSource.Reflected, "EStatusEffectType (29 DamageReduction, 30 DamageReductionMultiplier)"),
        new("StatusEffectData.Value", "StatusEffectData", "Value", 0x34, OffsetSource.Reflected, "float"),
        new("StatusEffectData.DamageTypeFlags", "StatusEffectData", "DamageTypeFlags", 0x48, OffsetSource.Reflected, "uint32: bits de ESurvivalDamageTypeFlags"),
        new("StatusEffectData.DamageType", "StatusEffectData", "DamageType", 0x50, OffsetSource.Reflected, "TSubclassOf<UDamageType>"),
        new("HealthComponent.ColliderConfigs", "HealthComponent", "ColliderConfigs", 0x130, OffsetSource.Reflected, "TArray<FHealthColliderConfig>: multiplicadores de dano (fraquezas/resistências)"),
        new("HealthColliderConfig.Size", "HealthColliderConfig", null, 0x58, OffsetSource.Engine, "sizeof(FHealthColliderConfig) (dump: 88)"),
        new("HealthColliderConfig.ComponentName", "HealthColliderConfig", "ComponentName", 0x0, OffsetSource.Reflected, "FString"),
        new("HealthColliderConfig.DamageMultiplier", "HealthColliderConfig", "DamageMultiplier", 0x10, OffsetSource.Reflected, "float"),
        new("HealthColliderConfig.DamageTypeFlags", "HealthColliderConfig", "DamageTypeFlags", 0x48, OffsetSource.Reflected, "uint32: bits de ESurvivalDamageTypeFlags"),
        new("HealthColliderConfig.DamageType", "HealthColliderConfig", "DamageType", 0x50, OffsetSource.Reflected, "TSubclassOf<UDamageType>"),
        new("HealthComponent.LastDamageInfo", "HealthComponent", "LastDamageInfo", 0x3B0, OffsetSource.Reflected, "FDamageInfo do último dano recebido"),
        new("DamageInfo.DamageType", "DamageInfo", "DamageType", 0x40, OffsetSource.Reflected, "TSubclassOf<UDamageType>"),
        new("DamageInfo.BlockType", "DamageInfo", "BlockType", 0x69, OffsetSource.Reflected, "EBlockType: 0 None, 1 Blocked, 2 PerfectBlock, 3 BlockBreak"),
        new("DodgeComponent.PerfectDodgeWindow", "DodgeComponent", "PerfectDodgeWindow", 0x1C4, OffsetSource.Reflected, "float (s): janela da esquiva perfeita"),

        new("World.PersistentLevel", "World", "PersistentLevel", 0x30, OffsetSource.Reflected, "ULevel* PersistentLevel"),
        new("World.GameState", "World", "GameState", 0x1B0, OffsetSource.Reflected, "AGameStateBase* GameState"),
        new("World.Levels", "World", "Levels", 0x1D0, OffsetSource.Reflected, "TArray<ULevel*> Levels (inclui níveis de streaming/World Partition)"),
        new("World.OwningGameInstance", "World", "OwningGameInstance", 0x230, OffsetSource.Reflected, "UGameInstance*"),
        new("Level.Actors", "Level", null, 0xA0, OffsetSource.RuntimeDetected, "TArray<AActor*> Actors (não refletido; validado em 0xA0 no jogo)"),
        new("Level.OwningWorld", "Level", "OwningWorld", 0xC0, OffsetSource.Reflected, "UWorld* OwningWorld"),
        new("GameInstance.LocalPlayers", "GameInstance", "LocalPlayers", 0x38, OffsetSource.Reflected, "TArray<ULocalPlayer*>"),
        new("Player.PlayerController", "Player", "PlayerController", 0x30, OffsetSource.Reflected, "APlayerController*"),
        new("PlayerController.AcknowledgedPawn", "PlayerController", "AcknowledgedPawn", 0x380, OffsetSource.Reflected, "APawn* (jogador local)"),
        new("PlayerController.PlayerCameraManager", "PlayerController", "PlayerCameraManager", 0x390, OffsetSource.Reflected, "APlayerCameraManager*"),
        new("PlayerCameraManager.CameraCachePrivate", "PlayerCameraManager", "CameraCachePrivate", 0x1560, OffsetSource.Reflected, "FCameraCacheEntry"),
        new("CameraCacheEntry.POV", "CameraCacheEntry", "POV", 0x10, OffsetSource.Reflected, "FMinimalViewInfo"),
        new("MinimalViewInfo.Location", "MinimalViewInfo", "Location", 0x0, OffsetSource.Reflected, "FVector"),
        new("MinimalViewInfo.Rotation", "MinimalViewInfo", "Rotation", 0x18, OffsetSource.Reflected, "FRotator"),
        new("MinimalViewInfo.FOV", "MinimalViewInfo", "FOV", 0x30, OffsetSource.Reflected, "float (FOV horizontal)"),

        new("Actor.RootComponent", "Actor", "RootComponent", 0x1C0, OffsetSource.Reflected, "USceneComponent*"),
        new("SceneComponent.AttachParent", "SceneComponent", "AttachParent", 0xD8, OffsetSource.Reflected, "USceneComponent*"),
        new("SceneComponent.RelativeLocation", "SceneComponent", "RelativeLocation", 0x150, OffsetSource.Reflected, "FVector"),
        new("SceneComponent.RelativeScale3D", "SceneComponent", "RelativeScale3D", 0x180, OffsetSource.Reflected, "FVector"),
        new("SceneComponent.Bounds", "SceneComponent", null, 0x118, OffsetSource.RuntimeDetected, "FBoxSphereBounds (não refletido; validado em 0x118 no jogo)"),
        new("SceneComponent.ComponentToWorld", "SceneComponent", null, 0x1F0, OffsetSource.RuntimeDetected, "FTransform (não refletido)"),
        new("Character.Mesh", "Character", "Mesh", 0x340, OffsetSource.Reflected, "USkeletalMeshComponent*"),
        new("Character.CapsuleComponent", "Character", "CapsuleComponent", 0x350, OffsetSource.Reflected, "UCapsuleComponent*"),
        new("CapsuleComponent.CapsuleHalfHeight", "CapsuleComponent", "CapsuleHalfHeight", 0x550, OffsetSource.Reflected, "float"),
        new("CapsuleComponent.CapsuleRadius", "CapsuleComponent", "CapsuleRadius", 0x554, OffsetSource.Reflected, "float"),

        new("SurvivalCharacter.CharacterData", "SurvivalCharacter", "CharacterData", 0x12C8, OffsetSource.Reflected, "FDataTableRowHandle (RowName = espécie)"),
        new("SurvivalCharacter.HealthComponent", "SurvivalCharacter", "HealthComponent", 0x13B8, OffsetSource.Reflected, "UHealthComponent*"),
        new("SurvivalCharacter.TeamComponent", "SurvivalCharacter", "TeamComponent", 0x13D8, OffsetSource.Reflected, "UTeamComponent*"),
        new("HealthComponent.HealthState", "HealthComponent", "HealthState", 0x318, OffsetSource.Reflected, "EHealthState (0 Alive, 1 Incapacitated, 2 Dead)"),
        new("HealthComponent.MaxHealth", "HealthComponent", "MaxHealth", 0x330, OffsetSource.Reflected, "float"),
        new("HealthComponent.CurrentDamage", "HealthComponent", "CurrentDamage", 0x334, OffsetSource.Reflected, "float (vida = Max - Dano)"),
        new("HealthComponent.NewGamePlusHealthMultiplier", "HealthComponent", "NewGamePlusHealthMultiplier", 0x4B8, OffsetSource.Reflected, "float"),
        new("HealthComponent.CustomGameModeHealthMultiplier", "HealthComponent", "CustomGameModeHealthMultiplier", 0x4BC, OffsetSource.Reflected, "float"),
        new("HealthComponent.CustomPropertyHealthMultiplier", "HealthComponent", "CustomPropertyHealthMultiplier", 0x4C0, OffsetSource.Reflected, "float"),
        new("TeamComponent.TeamDataTable", "TeamComponent", "TeamDataTable", 0xE0, OffsetSource.Reflected, "FDataTableRowHandle (RowName = time/facção)"),
        new("DataTableRowHandle.RowName", "DataTableRowHandle", "RowName", 0x8, OffsetSource.Reflected, "FName"),

        // Coletáveis (Milk Molars = BP_Milk_Nugget_* : BP_ToppleHarvestNode_C : ... : HarvestNode)
        new("HarvestNode.NodeData", "HarvestNode", "NodeData", 0x360, OffsetSource.Reflected, "FDataTableRowHandle (RowName = tipo do nó)"),
        new("HarvestNode.HealthComponent", "HarvestNode", "HealthComponent", 0x378, OffsetSource.Reflected, "UHealthComponent* (Dead = já quebrado/coletado)"),
        new("MilkNugget.EMolarTypeForTooltip", "BP_Milk_Nugget_Personal_C", "EMolarTypeForTooltip", 0x618, OffsetSource.Reflected, "ECollectibleType (2 Upgrade, 3 UpgradeParty, 4 UpgradeBuggy)"),
        new("ScienceCollectable.ScienceAmount", "ScienceCollectable", "ScienceAmount", 0x358, OffsetSource.Reflected, "int32: ciência que o Raw Science dá ao ser coletado"),
        new("ScienceCollectable.LiteData", "ScienceCollectable", "LiteData", 0x360, OffsetSource.Reflected, "UScienceCollectableLiteData* (valor padrão da classe)"),
        new("ScienceCollectableLiteData.ScienceAmount", "ScienceCollectableLiteData", "ScienceAmount", 0x90, OffsetSource.Reflected, "int32: ciência padrão (usado se o do ator for 0)"),
        // Bloqueio automático: ataques de criaturas (montagem em execução + notifies de golpe)
        new("SurvivalCharacter.MotionWarpingAttackTarget", "SurvivalCharacter", "MotionWarpingAttackTarget", 0x10D8, OffsetSource.Reflected, "ASurvivalCharacter* alvo do ataque atual (warp)"),
        new("SurvivalCharacter.BlockComponent", "SurvivalCharacter", "BlockComponent", 0x13C0, OffsetSource.Reflected, "UBlockComponent*"),
        new("SurvivalCharacter.CurrentAttackIsRanged", "SurvivalCharacter", "CurrentAttackIsRanged", 0x1C24, OffsetSource.Reflected, "bool: ataque atual é à distância"),
        new("BlockComponent.PerfectBlockWindow", "BlockComponent", "PerfectBlockWindow", 0x14C, OffsetSource.Reflected, "float (s): janela do bloqueio perfeito"),
        new("SkeletalMeshComponent.AnimScriptInstance", "SkeletalMeshComponent", "AnimScriptInstance", 0x918, OffsetSource.Reflected, "UAnimInstance*"),
        new("AnimInstance.MontageInstances", "AnimInstance", null, 0x0, OffsetSource.RuntimeDetected, "TArray<FAnimMontageInstance*> (não refletido; detectado em runtime, 0 = ainda não detectado)"),
        new("AnimMontageInstance.Montage", "AnimMontageInstance", "Montage", 0x0, OffsetSource.Reflected, "UAnimMontage*"),
        new("AnimMontageInstance.bPlaying", "AnimMontageInstance", "bPlaying", 0x48, OffsetSource.Reflected, "bool"),
        new("AnimMontageInstance.Position", "AnimMontageInstance", "position", 0x158, OffsetSource.Reflected, "float (s): posição atual na montagem"),
        new("AnimMontageInstance.PlayRate", "AnimMontageInstance", "PlayRate", 0x15C, OffsetSource.Reflected, "float"),
        new("AnimSequenceBase.Notifies", "AnimSequenceBase", "Notifies", 0x80, OffsetSource.Reflected, "TArray<FAnimNotifyEvent>"),
        new("AnimSequenceBase.SequenceLength", "AnimSequenceBase", "SequenceLength", 0x90, OffsetSource.Reflected, "float (s)"),
        new("AnimNotifyEvent.Size", "AnimNotifyEvent", null, 0xB8, OffsetSource.Engine, "sizeof(FAnimNotifyEvent) (dump: 184)"),
        new("AnimNotifyEvent.LinkMethod", "AnimNotifyEvent", "LinkMethod", 0x18, OffsetSource.Reflected, "EAnimLinkMethod (0 Absolute, 1 Relative, 2 Proportional)"),
        new("AnimNotifyEvent.SegmentBeginTime", "AnimNotifyEvent", "SegmentBeginTime", 0x1C, OffsetSource.Reflected, "float"),
        new("AnimNotifyEvent.SegmentLength", "AnimNotifyEvent", "SegmentLength", 0x20, OffsetSource.Reflected, "float"),
        new("AnimNotifyEvent.LinkValue", "AnimNotifyEvent", "LinkValue", 0x24, OffsetSource.Reflected, "float: instante do notify (Absolute)"),
        new("AnimNotifyEvent.TriggerTimeOffset", "AnimNotifyEvent", "TriggerTimeOffset", 0x30, OffsetSource.Reflected, "float"),
        new("AnimNotifyEvent.Notify", "AnimNotifyEvent", "Notify", 0x48, OffsetSource.Reflected, "UAnimNotify* (ex.: AnimNotify_HitFrame = golpe)"),
        new("AnimNotifyEvent.NotifyStateClass", "AnimNotifyEvent", "NotifyStateClass", 0x50, OffsetSource.Reflected, "UAnimNotifyState* (ex.: AnimNotifyState_HitActive)"),
        new("AnimNotifyEvent.Duration", "AnimNotifyEvent", "Duration", 0x58, OffsetSource.Reflected, "float"),
        new("AnimSequenceBase.RateScale", "AnimSequenceBase", "RateScale", 0xA8, OffsetSource.Reflected, "float: velocidade própria da animação"),
        new("AnimMontage.SlotAnimTracks", "AnimMontage", "SlotAnimTracks", 0x160, OffsetSource.Reflected, "TArray<FSlotAnimationTrack> (golpes ficam nas AnimSequences dos segmentos)"),
        new("SlotAnimationTrack.Size", "SlotAnimationTrack", null, 0x18, OffsetSource.Engine, "sizeof(FSlotAnimationTrack) (dump: 24)"),
        new("SlotAnimationTrack.AnimTrack", "SlotAnimationTrack", "AnimTrack", 0x8, OffsetSource.Reflected, "FAnimTrack"),
        new("AnimTrack.AnimSegments", "AnimTrack", "AnimSegments", 0x0, OffsetSource.Reflected, "TArray<FAnimSegment>"),
        new("AnimSegment.Size", "AnimSegment", null, 0x20, OffsetSource.Engine, "sizeof(FAnimSegment) (dump: 32)"),
        new("AnimSegment.AnimReference", "AnimSegment", "AnimReference", 0x0, OffsetSource.Reflected, "UAnimSequenceBase*"),
        new("AnimSegment.StartPos", "AnimSegment", "StartPos", 0x8, OffsetSource.Reflected, "float: início do segmento na montagem"),
        new("AnimSegment.AnimStartTime", "AnimSegment", "AnimStartTime", 0xC, OffsetSource.Reflected, "float"),
        new("AnimSegment.AnimEndTime", "AnimSegment", "AnimEndTime", 0x10, OffsetSource.Reflected, "float"),
        new("AnimSegment.AnimPlayRate", "AnimSegment", "AnimPlayRate", 0x14, OffsetSource.Reflected, "float"),
        new("AnimSegment.LoopingCount", "AnimSegment", "LoopingCount", 0x18, OffsetSource.Reflected, "int32"),
        new("SpawnedItem.PlacedItemData", "SpawnedItem", "PlacedItemData", 0x4E0, OffsetSource.Reflected, "FDataTableRowHandle do item posicionado (RowName = item)"),
        new("SpawnedItem.Item", "SpawnedItem", "Item", 0x548, OffsetSource.Reflected, "UItem* do item no chão"),
        new("Item.ItemDataRowHandle", "Item", "ItemDataRowHandle", 0x168, OffsetSource.Reflected, "FDataTableRowHandle_NetCrc32 (RowName @+0x8 = item)"),

        // Versão leve (LOD): GameState → LODActorManagerComponent → LODActors[] (BaseLODActor*)
        new("SurvivalGameState.LODActorManagerComponent", "SurvivalGameState", "LODActorManagerComponent", 0x3B8, OffsetSource.Reflected, "ULODActorManagerComponent*"),
        new("LODActorManagerComponent.LODActors", "LODActorManagerComponent", "LODActors", 0xF0, OffsetSource.Reflected, "TArray<UBaseLODActor*> (versões leves de todos os atores rastreados)"),
        new("BaseLODActor.FullActor", "BaseLODActor", null, 0xC0, OffsetSource.RuntimeDetected, "no jogo 0x10 além do dump; conferido em runtime · AActor* versão completa (null enquanto só existe a leve)"),
        new("BaseLODActor.ProxyActor", "BaseLODActor", null, 0xC8, OffsetSource.RuntimeDetected, "no jogo 0x10 além do dump; conferido em runtime · UProxyActor* dados da versão leve"),
        new("BaseLODActor.LODComponents", "BaseLODActor", null, 0xD0, OffsetSource.RuntimeDetected, "no jogo 0x10 além do dump; conferido em runtime · TArray<UBaseLODComponent*> (ex.: EggLODComponent)"),
        new("BaseLODActor.Location", "BaseLODActor", null, 0x120, OffsetSource.RuntimeDetected, "FVector (double) da versão leve (não refletido; FTransform @0x100, conferido com a versão completa em runtime)"),
        new("ProxySpawnedItemActor.Item", "ProxySpawnedItemActor", "Item", 0x1F0, OffsetSource.Reflected, "UItem* do item na versão leve"),
        new("LootBag.InventoryComponent", "LootBag", "InventoryComponent", 0x390, OffsetSource.Reflected, "UInventoryComponent* (corpos com Tech Chip: BP_DeadBody_*TechChip*)"),
        new("InventoryComponent.Items", "InventoryComponent", "Items", 0x1D0, OffsetSource.Reflected, "TArray<UItem*>"),
        new("ProxyLootBagActor.Items", "ProxyLootBagActor", "Items", 0x208, OffsetSource.Reflected, "TArray<UItem*> do corpo na versão leve (conferido em runtime)"),
        new("BaseLODActor.LiteData", "BaseLODActor", null, 0xF0, OffsetSource.RuntimeDetected, "UActorLiteData* (ex.: LD_World_Buried_UniqueChest_IceCart; não refletido, procurado em runtime)"),
        new("ScanSwitch.bScanned", "ScanSwitch", "bScanned", 0x3A4, OffsetSource.Reflected, "bool: já escaneado/ativado (Transmissor ORC); replicado e salvo no save"),

        // Pets / buggies (criaturas domadas)
        new("SurvivalCharacter.TameableComponent", "SurvivalCharacter", "TameableComponent", 0x14A8, OffsetSource.Reflected, "UTameableComponent*"),
        new("TameableComponent.TameData", "TameableComponent", "TameData", 0x120, OffsetSource.Reflected, "UTamedCreatureData* (null = não domado)"),
        new("TamedCreatureData.TameState", "TamedCreatureData", "TameState", 0x168, OffsetSource.Reflected, "ETamedState (0 None, 1 New, 2 Tame)"),

        // Arenas de chefes
        new("BossManager.bBossActive", "BossManager", "bBossActive", 0x438, OffsetSource.Reflected, "bool (luta em andamento)"),

        // Componentes do ator (pontos fracos = componentes com "weak" no nome, ex.: LeftEyeWeakspot)
        new("Actor.BlueprintCreatedComponents", "Actor", "BlueprintCreatedComponents", 0x2A8, OffsetSource.Reflected, "TArray<UActorComponent*> (componentes do blueprint)"),
        new("Actor.InstanceComponents", "Actor", "InstanceComponents", 0x298, OffsetSource.Reflected, "TArray<UActorComponent*>"),
    };

    private static readonly Dictionary<string, int> Values = new(StringComparer.OrdinalIgnoreCase);

    public static string LoadedFrom { get; private set; } = "padrões internos";

    static Off()
    {
        foreach (var d in Defs) Values[d.Key] = d.Default;
        Bind();
    }

    public static int Get(string key) => Values.TryGetValue(key, out int v) ? v : throw new KeyNotFoundException(key);

    public static void Set(string key, int value)
    {
        if (!Values.ContainsKey(key)) throw new KeyNotFoundException(key);
        Values[key] = value;
        Bind();
    }

    public static IReadOnlyDictionary<string, int> All => Values;

    private static void Bind()
    {
        UObject_ClassPrivate = Values["UObject.ClassPrivate"];
        UObject_NamePrivate = Values["UObject.NamePrivate"];
        UObject_OuterPrivate = Values["UObject.OuterPrivate"];
        UStruct_SuperStruct = Values["UStruct.SuperStruct"];
        FNamePool_Blocks = Values["FNamePool.Blocks"];
        FUObjectArray_NumElements = Values["FUObjectArray.NumElements"];
        FUObjectArray_ObjObjects = Values["FUObjectArray.ObjObjects"];
        UObject_ObjectFlags = Values["UObject.ObjectFlags"];
        UObject_InternalIndex = Values["UObject.InternalIndex"];
        SurvivalDamageType_bCanPerfectDodge = Values["SurvivalDamageType.bCanPerfectDodge"];
        SurvivalDamageType_bCanBlock = Values["SurvivalDamageType.bCanBlock"];
        SurvivalCharacter_DodgeComponent = Values["SurvivalCharacter.DodgeComponent"];
        DodgeComponent_PerfectDodgeWindow = Values["DodgeComponent.PerfectDodgeWindow"];
        HealthComponent_LastDamageInfo = Values["HealthComponent.LastDamageInfo"];
        HealthComponent_ColliderConfigs = Values["HealthComponent.ColliderConfigs"];
        SurvivalCharacter_StatusEffectComponent = Values["SurvivalCharacter.StatusEffectComponent"];
        StatusEffectComponent_StatusEffects = Values["StatusEffectComponent.StatusEffects"];
        StatusEffectComponent_InfiniteDefaultStatusEffects = Values["StatusEffectComponent.InfiniteDefaultStatusEffects"];
        StatusEffect_RowHandle = Values["StatusEffect.StatusEffectRowHandle"];
        StatusEffectData_Type = Values["StatusEffectData.Type"];
        StatusEffectData_Value = Values["StatusEffectData.Value"];
        StatusEffectData_DamageTypeFlags = Values["StatusEffectData.DamageTypeFlags"];
        StatusEffectData_DamageType = Values["StatusEffectData.DamageType"];
        HealthColliderConfig_Size = Values["HealthColliderConfig.Size"];
        HealthColliderConfig_ComponentName = Values["HealthColliderConfig.ComponentName"];
        HealthColliderConfig_DamageMultiplier = Values["HealthColliderConfig.DamageMultiplier"];
        HealthColliderConfig_DamageTypeFlags = Values["HealthColliderConfig.DamageTypeFlags"];
        HealthColliderConfig_DamageType = Values["HealthColliderConfig.DamageType"];
        DamageInfo_DamageType = Values["DamageInfo.DamageType"];
        DamageInfo_BlockType = Values["DamageInfo.BlockType"];

        World_PersistentLevel = Values["World.PersistentLevel"];
        World_GameState = Values["World.GameState"];
        World_Levels = Values["World.Levels"];
        World_OwningGameInstance = Values["World.OwningGameInstance"];
        Level_Actors = Values["Level.Actors"];
        Level_OwningWorld = Values["Level.OwningWorld"];
        GameInstance_LocalPlayers = Values["GameInstance.LocalPlayers"];
        Player_PlayerController = Values["Player.PlayerController"];
        PlayerController_AcknowledgedPawn = Values["PlayerController.AcknowledgedPawn"];
        PlayerController_PlayerCameraManager = Values["PlayerController.PlayerCameraManager"];
        PlayerCameraManager_CameraCachePrivate = Values["PlayerCameraManager.CameraCachePrivate"];
        CameraCacheEntry_POV = Values["CameraCacheEntry.POV"];
        MinimalViewInfo_Location = Values["MinimalViewInfo.Location"];
        MinimalViewInfo_Rotation = Values["MinimalViewInfo.Rotation"];
        MinimalViewInfo_FOV = Values["MinimalViewInfo.FOV"];

        Actor_RootComponent = Values["Actor.RootComponent"];
        SceneComponent_AttachParent = Values["SceneComponent.AttachParent"];
        SceneComponent_RelativeLocation = Values["SceneComponent.RelativeLocation"];
        SceneComponent_RelativeScale3D = Values["SceneComponent.RelativeScale3D"];
        SceneComponent_Bounds = Values["SceneComponent.Bounds"];
        SceneComponent_ComponentToWorld = Values["SceneComponent.ComponentToWorld"];
        Character_Mesh = Values["Character.Mesh"];
        Character_CapsuleComponent = Values["Character.CapsuleComponent"];
        CapsuleComponent_CapsuleHalfHeight = Values["CapsuleComponent.CapsuleHalfHeight"];
        CapsuleComponent_CapsuleRadius = Values["CapsuleComponent.CapsuleRadius"];

        SurvivalCharacter_CharacterData = Values["SurvivalCharacter.CharacterData"];
        SurvivalCharacter_HealthComponent = Values["SurvivalCharacter.HealthComponent"];
        SurvivalCharacter_TeamComponent = Values["SurvivalCharacter.TeamComponent"];
        HealthComponent_HealthState = Values["HealthComponent.HealthState"];
        HealthComponent_MaxHealth = Values["HealthComponent.MaxHealth"];
        HealthComponent_CurrentDamage = Values["HealthComponent.CurrentDamage"];
        HealthComponent_NewGamePlusHealthMultiplier = Values["HealthComponent.NewGamePlusHealthMultiplier"];
        HealthComponent_CustomGameModeHealthMultiplier = Values["HealthComponent.CustomGameModeHealthMultiplier"];
        HealthComponent_CustomPropertyHealthMultiplier = Values["HealthComponent.CustomPropertyHealthMultiplier"];
        TeamComponent_TeamDataTable = Values["TeamComponent.TeamDataTable"];
        DataTableRowHandle_RowName = Values["DataTableRowHandle.RowName"];
        HarvestNode_NodeData = Values["HarvestNode.NodeData"];
        HarvestNode_HealthComponent = Values["HarvestNode.HealthComponent"];
        MilkNugget_MolarType = Values["MilkNugget.EMolarTypeForTooltip"];
        SurvivalCharacter_MotionWarpingAttackTarget = Values["SurvivalCharacter.MotionWarpingAttackTarget"];
        SurvivalCharacter_BlockComponent = Values["SurvivalCharacter.BlockComponent"];
        SurvivalCharacter_CurrentAttackIsRanged = Values["SurvivalCharacter.CurrentAttackIsRanged"];
        BlockComponent_PerfectBlockWindow = Values["BlockComponent.PerfectBlockWindow"];
        SkeletalMeshComponent_AnimScriptInstance = Values["SkeletalMeshComponent.AnimScriptInstance"];
        AnimInstance_MontageInstances = Values["AnimInstance.MontageInstances"];
        AnimMontageInstance_Montage = Values["AnimMontageInstance.Montage"];
        AnimMontageInstance_bPlaying = Values["AnimMontageInstance.bPlaying"];
        AnimMontageInstance_Position = Values["AnimMontageInstance.Position"];
        AnimMontageInstance_PlayRate = Values["AnimMontageInstance.PlayRate"];
        AnimSequenceBase_Notifies = Values["AnimSequenceBase.Notifies"];
        AnimSequenceBase_SequenceLength = Values["AnimSequenceBase.SequenceLength"];
        AnimNotifyEvent_Size = Values["AnimNotifyEvent.Size"];
        AnimNotifyEvent_LinkMethod = Values["AnimNotifyEvent.LinkMethod"];
        AnimNotifyEvent_SegmentBeginTime = Values["AnimNotifyEvent.SegmentBeginTime"];
        AnimNotifyEvent_SegmentLength = Values["AnimNotifyEvent.SegmentLength"];
        AnimNotifyEvent_LinkValue = Values["AnimNotifyEvent.LinkValue"];
        AnimNotifyEvent_TriggerTimeOffset = Values["AnimNotifyEvent.TriggerTimeOffset"];
        AnimNotifyEvent_Notify = Values["AnimNotifyEvent.Notify"];
        AnimNotifyEvent_NotifyStateClass = Values["AnimNotifyEvent.NotifyStateClass"];
        AnimNotifyEvent_Duration = Values["AnimNotifyEvent.Duration"];
        AnimSequenceBase_RateScale = Values["AnimSequenceBase.RateScale"];
        AnimMontage_SlotAnimTracks = Values["AnimMontage.SlotAnimTracks"];
        SlotAnimationTrack_Size = Values["SlotAnimationTrack.Size"];
        SlotAnimationTrack_AnimTrack = Values["SlotAnimationTrack.AnimTrack"];
        AnimTrack_AnimSegments = Values["AnimTrack.AnimSegments"];
        AnimSegment_Size = Values["AnimSegment.Size"];
        AnimSegment_AnimReference = Values["AnimSegment.AnimReference"];
        AnimSegment_StartPos = Values["AnimSegment.StartPos"];
        AnimSegment_AnimStartTime = Values["AnimSegment.AnimStartTime"];
        AnimSegment_AnimEndTime = Values["AnimSegment.AnimEndTime"];
        AnimSegment_AnimPlayRate = Values["AnimSegment.AnimPlayRate"];
        AnimSegment_LoopingCount = Values["AnimSegment.LoopingCount"];
        ScienceCollectable_ScienceAmount = Values["ScienceCollectable.ScienceAmount"];
        ScienceCollectable_LiteData = Values["ScienceCollectable.LiteData"];
        ScienceLiteData_ScienceAmount = Values["ScienceCollectableLiteData.ScienceAmount"];
        SpawnedItem_PlacedItemData = Values["SpawnedItem.PlacedItemData"];
        SpawnedItem_Item = Values["SpawnedItem.Item"];
        Item_ItemDataRowHandle = Values["Item.ItemDataRowHandle"];
        SurvivalGameState_LODActorManagerComponent = Values["SurvivalGameState.LODActorManagerComponent"];
        LODActorManagerComponent_LODActors = Values["LODActorManagerComponent.LODActors"];
        BaseLODActor_FullActor = Values["BaseLODActor.FullActor"];
        BaseLODActor_ProxyActor = Values["BaseLODActor.ProxyActor"];
        BaseLODActor_LODComponents = Values["BaseLODActor.LODComponents"];
        BaseLODActor_Location = Values["BaseLODActor.Location"];
        ProxySpawnedItemActor_Item = Values["ProxySpawnedItemActor.Item"];
        ScanSwitch_bScanned = Values["ScanSwitch.bScanned"];
        BaseLODActor_LiteData = Values["BaseLODActor.LiteData"];
        LootBag_InventoryComponent = Values["LootBag.InventoryComponent"];
        InventoryComponent_Items = Values["InventoryComponent.Items"];
        ProxyLootBagActor_Items = Values["ProxyLootBagActor.Items"];
        SurvivalCharacter_TameableComponent = Values["SurvivalCharacter.TameableComponent"];
        TameableComponent_TameData = Values["TameableComponent.TameData"];
        TamedCreatureData_TameState = Values["TamedCreatureData.TameState"];
        BossManager_bBossActive = Values["BossManager.bBossActive"];
        Actor_BlueprintCreatedComponents = Values["Actor.BlueprintCreatedComponents"];
        Actor_InstanceComponents = Values["Actor.InstanceComponents"];
    }

    // ------------------------------------------------------------------ persistência

    public sealed class OffsetFile
    {
        public string Source { get; set; } = "";
        public string DumpFile { get; set; } = "";
        public long DumpFileTicks { get; set; }
        public int UeVersion { get; set; }
        public DateTime GeneratedAt { get; set; }
        public Dictionary<string, string> Offsets { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static OffsetFile? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Logger.Warn(Cat, $"{path} não existe; usando offsets internos.");
                return null;
            }

            var file = JsonSerializer.Deserialize<OffsetFile>(File.ReadAllText(path));
            if (file == null) return null;

            int applied = 0;
            foreach (var kv in file.Offsets)
            {
                if (!Values.ContainsKey(kv.Key))
                {
                    Logger.Warn(Cat, $"Chave desconhecida em offsets.json ignorada: {kv.Key}");
                    continue;
                }

                if (TryParseHex(kv.Value, out int v))
                {
                    Values[kv.Key] = v;
                    applied++;
                }
                else
                {
                    Logger.Warn(Cat, $"Valor inválido para {kv.Key}: '{kv.Value}'");
                }
            }

            Bind();
            _dumpFile = file.DumpFile;
            _dumpTicks = file.DumpFileTicks;
            LoadedFrom = $"{Path.GetFileName(path)} ({file.Source})";
            Logger.Info(Cat, $"{applied} offsets carregados de {path} (origem: {file.Source}, UE {file.UeVersion}).");
            return file;
        }
        catch (Exception ex)
        {
            Logger.Error(Cat, $"Falha ao ler {path}; usando offsets internos.", ex);
            return null;
        }
    }

    private static string _dumpFile = "";
    private static long _dumpTicks;

    /// <summary>Grava a tabela atual. Sem dumpFile, preserva a referência ao último dump importado.</summary>
    public static void Save(string path, string source, string dumpFile = "", long dumpTicks = 0, int ueVersion = 507)
    {
        if (dumpFile.Length > 0)
        {
            _dumpFile = dumpFile;
            _dumpTicks = dumpTicks;
        }
        else
        {
            dumpFile = _dumpFile;
            dumpTicks = _dumpTicks;
        }

        try
        {
            var file = new OffsetFile
            {
                Source = source,
                DumpFile = dumpFile,
                DumpFileTicks = dumpTicks,
                UeVersion = ueVersion,
                GeneratedAt = DateTime.UtcNow,
            };
            foreach (var d in Defs) file.Offsets[d.Key] = $"0x{Values[d.Key]:X}";
            File.WriteAllText(path, JsonSerializer.Serialize(file, Json));
            Logger.Info(Cat, $"offsets.json gravado ({source}).");
        }
        catch (Exception ex)
        {
            Logger.Error(Cat, $"Falha ao gravar {path}.", ex);
        }
    }

    public static bool TryParseHex(string s, out int value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public static void LogAll()
    {
        foreach (var d in Defs)
            Logger.Info(Cat, $"  {d.Key,-48} = 0x{Values[d.Key]:X4}  [{d.Source}]");
    }
}
