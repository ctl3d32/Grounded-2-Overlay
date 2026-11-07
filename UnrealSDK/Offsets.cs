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
    public static int SpawnedItem_PlacedItemData;
    public static int SpawnedItem_Item;
    public static int Item_ItemDataRowHandle;
    public static int SurvivalCharacter_TameableComponent;
    public static int TameableComponent_TameData;
    public static int TamedCreatureData_TameState;
    public static int BossManager_bBossActive;

    public static readonly OffsetDef[] Defs =
    {
        new("UObject.ClassPrivate", "UObjectBase", null, 0x10, OffsetSource.Engine, "UClass* do objeto"),
        new("UObject.NamePrivate", "UObjectBase", null, 0x18, OffsetSource.Engine, "FName do objeto"),
        new("UObject.OuterPrivate", "UObjectBase", null, 0x20, OffsetSource.Engine, "UObject* Outer"),
        new("UStruct.SuperStruct", "UStruct", null, 0x40, OffsetSource.RuntimeDetected, "UStruct* SuperStruct (herança de classes)"),
        new("FNamePool.Blocks", "FNamePool", null, 0x10, OffsetSource.RuntimeDetected, "uint8* Blocks[] do FNameEntryAllocator"),
        new("FUObjectArray.NumElements", "FUObjectArray", null, 0x24, OffsetSource.Engine, "ObjObjects(0x10).NumElements(0x14)"),

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
        new("SpawnedItem.PlacedItemData", "SpawnedItem", "PlacedItemData", 0x4E0, OffsetSource.Reflected, "FDataTableRowHandle do item posicionado (RowName = item)"),
        new("SpawnedItem.Item", "SpawnedItem", "Item", 0x548, OffsetSource.Reflected, "UItem* do item no chão"),
        new("Item.ItemDataRowHandle", "Item", "ItemDataRowHandle", 0x168, OffsetSource.Reflected, "FDataTableRowHandle_NetCrc32 (RowName @+0x8 = item)"),

        // Pets / buggies (criaturas domadas)
        new("SurvivalCharacter.TameableComponent", "SurvivalCharacter", "TameableComponent", 0x14A8, OffsetSource.Reflected, "UTameableComponent*"),
        new("TameableComponent.TameData", "TameableComponent", "TameData", 0x120, OffsetSource.Reflected, "UTamedCreatureData* (null = não domado)"),
        new("TamedCreatureData.TameState", "TamedCreatureData", "TameState", 0x168, OffsetSource.Reflected, "ETamedState (0 None, 1 New, 2 Tame)"),

        // Arenas de chefes
        new("BossManager.bBossActive", "BossManager", "bBossActive", 0x438, OffsetSource.Reflected, "bool (luta em andamento)"),
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
        SpawnedItem_PlacedItemData = Values["SpawnedItem.PlacedItemData"];
        SpawnedItem_Item = Values["SpawnedItem.Item"];
        Item_ItemDataRowHandle = Values["Item.ItemDataRowHandle"];
        SurvivalCharacter_TameableComponent = Values["SurvivalCharacter.TameableComponent"];
        TameableComponent_TameData = Values["TameableComponent.TameData"];
        TamedCreatureData_TameState = Values["TamedCreatureData.TameState"];
        BossManager_bBossActive = Values["BossManager.bBossActive"];
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
