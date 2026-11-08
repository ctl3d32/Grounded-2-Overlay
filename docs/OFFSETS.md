# Offsets — Grounded 2 (UE 5.7, build Augusta-CL-3207493)

Fonte primária: dump do SDK `Grounded2Steam-Win64-Shipping-dump-20261108-175841.jsonl`
(`ue_version: 507`, 629.200 objetos, 9.134 classes, 7.709 structs, 2.695 enums, 0 erros; mesma build do executável,
`pe_hash C935E7990B27C000`). Os dumps anteriores (30/10 e o refeito em 03/11) têm exatamente os mesmos offsets e RVAs.
O novo só traz mais blueprints carregados (ex.: escorpiões, louva-a-deus, abelha), o que ampliou a tabela de pontos fracos.

Os offsets **refletidos** foram conferidos por script contra o dump: os 45 offsets refletidos dos 54 valores padrão do código
(`UnrealSDK/Offsets.cs`) batem exatamente com o dump (0 divergências). Os **não refletidos** não aparecem
no dump e por isso **não são assumidos**: o `LayoutDetector` testa candidatos e só aceita o que passa na validação.

## Globais (resolvidas por AOB — ver ASSINATURAS.md)

| Global | RVA nesta build | Endereço no dump (base 0x7FF7C0E00000) | Validação em runtime |
|---|---|---|---|
| GWorld (`UWorld*`) | `0xA6C76E0` | `0x7FF7CB4C76E0` | `*GWorld` é objeto cuja classe se chama `World` |
| GNames (`FNamePool`) | `0xA499E40` | `0x7FF7CB299E40` | entrada 0 decodifica para `"None"` |
| GObjects (`FUObjectArray`) | `0xA53BC20` | `0x7FF7CB33BC20` | `NumElements` entre 1.000 e 50.000.000 |

> As RVAs servem apenas como referência/documentação. O overlay **nunca** as usa diretamente: sempre resolve por assinatura.

## Cadeia de ponteiros

```
GWorld ─► UWorld
           ├─ +0x30   PersistentLevel ─► ULevel ─ +0xA0 Actors (TArray<AActor*>)
           ├─ +0x1D0  Levels (TArray<ULevel*>)   ← níveis de streaming / World Partition
           └─ +0x230  OwningGameInstance ─► UGameInstance
                          └─ +0x38 LocalPlayers[0] ─► ULocalPlayer
                                       └─ +0x30 PlayerController ─► APlayerController
                                                   ├─ +0x380 AcknowledgedPawn  (jogador local)
                                                   └─ +0x390 PlayerCameraManager ─► APlayerCameraManager
                                                                └─ +0x1560 CameraCachePrivate (FCameraCacheEntry)
                                                                      └─ +0x10 POV (FMinimalViewInfo)
                                                                            ├─ +0x00 Location (FVector, double)
                                                                            ├─ +0x18 Rotation (FRotator, double)
                                                                            └─ +0x30 FOV (float, horizontal)

Criatura (ASurvivalCreature : ASurvivalCharacter : ACharacter : APawn : AActor)
  ├─ +0x1C0  RootComponent ─► UCapsuleComponent
  │             ├─ +0x150 RelativeLocation · +0x180 RelativeScale3D · +0xD8 AttachParent
  │             ├─ +0x1F0 ComponentToWorld (FTransform)   [runtime]
  │             └─ +0x550 CapsuleHalfHeight · +0x554 CapsuleRadius
  ├─ +0x340  Mesh ─► USkeletalMeshComponent ─ +0x118 Bounds (FBoxSphereBounds) [runtime]
  ├─ +0x12C8 CharacterData (FDataTableRowHandle) ─ +0x8 RowName (FName = espécie)
  ├─ +0x13B8 HealthComponent ─► UHealthComponent
  │             ├─ +0x318 HealthState (0 Alive · 1 Incapacitated · 2 Dead)
  │             ├─ +0x330 MaxHealth (float)
  │             ├─ +0x334 CurrentDamage (float)   →  Vida = MaxHealth × mult − CurrentDamage
  │             └─ +0x4B8/+0x4BC/+0x4C0 multiplicadores (NG+, modo personalizado, propriedade)
  └─ +0x13D8 TeamComponent ─► UTeamComponent ─ +0xE0 TeamDataTable ─ +0x8 RowName (FName = time)
```

## Tabela completa

| Chave | Classe | Membro | Offset | Origem | Descrição |
|---|---|---|---|---|---|
| `UObject.ClassPrivate` | UObjectBase | — | `0x10` | Layout fixo UE 5.x | UClass* do objeto |
| `UObject.NamePrivate` | UObjectBase | — | `0x18` | Layout fixo UE 5.x | FName do objeto |
| `UObject.OuterPrivate` | UObjectBase | — | `0x20` | Layout fixo UE 5.x | UObject* Outer |
| `UStruct.SuperStruct` | UStruct | — | `0x40` | Detectado em runtime | UStruct* SuperStruct (herança de classes) |
| `FNamePool.Blocks` | FNamePool | — | `0x10` | Detectado em runtime | uint8* Blocks[] do FNameEntryAllocator |
| `FUObjectArray.NumElements` | FUObjectArray | — | `0x24` | Layout fixo UE 5.x | ObjObjects(0x10).NumElements(0x14) |
| `World.PersistentLevel` | World | PersistentLevel | `0x30` | Dump (UPROPERTY) | ULevel* PersistentLevel |
| `World.GameState` | World | GameState | `0x1B0` | Dump (UPROPERTY) | AGameStateBase* GameState |
| `World.Levels` | World | Levels | `0x1D0` | Dump (UPROPERTY) | TArray<ULevel*> Levels (inclui níveis de streaming/World Partition) |
| `World.OwningGameInstance` | World | OwningGameInstance | `0x230` | Dump (UPROPERTY) | UGameInstance* |
| `Level.Actors` | Level | — | `0xA0` | Detectado em runtime | TArray<AActor*> Actors (não refletido; validado em 0xA0 no jogo) |
| `Level.OwningWorld` | Level | OwningWorld | `0xC0` | Dump (UPROPERTY) | UWorld* OwningWorld |
| `GameInstance.LocalPlayers` | GameInstance | LocalPlayers | `0x38` | Dump (UPROPERTY) | TArray<ULocalPlayer*> |
| `Player.PlayerController` | Player | PlayerController | `0x30` | Dump (UPROPERTY) | APlayerController* |
| `PlayerController.AcknowledgedPawn` | PlayerController | AcknowledgedPawn | `0x380` | Dump (UPROPERTY) | APawn* (jogador local) |
| `PlayerController.PlayerCameraManager` | PlayerController | PlayerCameraManager | `0x390` | Dump (UPROPERTY) | APlayerCameraManager* |
| `PlayerCameraManager.CameraCachePrivate` | PlayerCameraManager | CameraCachePrivate | `0x1560` | Dump (UPROPERTY) | FCameraCacheEntry |
| `CameraCacheEntry.POV` | CameraCacheEntry | POV | `0x10` | Dump (UPROPERTY) | FMinimalViewInfo |
| `MinimalViewInfo.Location` | MinimalViewInfo | Location | `0x0` | Dump (UPROPERTY) | FVector |
| `MinimalViewInfo.Rotation` | MinimalViewInfo | Rotation | `0x18` | Dump (UPROPERTY) | FRotator |
| `MinimalViewInfo.FOV` | MinimalViewInfo | FOV | `0x30` | Dump (UPROPERTY) | float (FOV horizontal) |
| `Actor.RootComponent` | Actor | RootComponent | `0x1C0` | Dump (UPROPERTY) | USceneComponent* |
| `SceneComponent.AttachParent` | SceneComponent | AttachParent | `0xD8` | Dump (UPROPERTY) | USceneComponent* |
| `SceneComponent.RelativeLocation` | SceneComponent | RelativeLocation | `0x150` | Dump (UPROPERTY) | FVector |
| `SceneComponent.RelativeScale3D` | SceneComponent | RelativeScale3D | `0x180` | Dump (UPROPERTY) | FVector |
| `SceneComponent.Bounds` | SceneComponent | — | `0x118` | Detectado em runtime | FBoxSphereBounds (não refletido; validado em 0x118 no jogo) |
| `SceneComponent.ComponentToWorld` | SceneComponent | — | `0x1F0` | Detectado em runtime | FTransform (não refletido) |
| `Character.Mesh` | Character | Mesh | `0x340` | Dump (UPROPERTY) | USkeletalMeshComponent* |
| `Character.CapsuleComponent` | Character | CapsuleComponent | `0x350` | Dump (UPROPERTY) | UCapsuleComponent* |
| `CapsuleComponent.CapsuleHalfHeight` | CapsuleComponent | CapsuleHalfHeight | `0x550` | Dump (UPROPERTY) | float |
| `CapsuleComponent.CapsuleRadius` | CapsuleComponent | CapsuleRadius | `0x554` | Dump (UPROPERTY) | float |
| `SurvivalCharacter.CharacterData` | SurvivalCharacter | CharacterData | `0x12C8` | Dump (UPROPERTY) | FDataTableRowHandle (RowName = espécie) |
| `SurvivalCharacter.HealthComponent` | SurvivalCharacter | HealthComponent | `0x13B8` | Dump (UPROPERTY) | UHealthComponent* |
| `SurvivalCharacter.TeamComponent` | SurvivalCharacter | TeamComponent | `0x13D8` | Dump (UPROPERTY) | UTeamComponent* |
| `HealthComponent.HealthState` | HealthComponent | HealthState | `0x318` | Dump (UPROPERTY) | EHealthState (0 Alive, 1 Incapacitated, 2 Dead) |
| `HealthComponent.MaxHealth` | HealthComponent | MaxHealth | `0x330` | Dump (UPROPERTY) | float |
| `HealthComponent.CurrentDamage` | HealthComponent | CurrentDamage | `0x334` | Dump (UPROPERTY) | float (vida = Max - Dano) |
| `HealthComponent.NewGamePlusHealthMultiplier` | HealthComponent | NewGamePlusHealthMultiplier | `0x4B8` | Dump (UPROPERTY) | float |
| `HealthComponent.CustomGameModeHealthMultiplier` | HealthComponent | CustomGameModeHealthMultiplier | `0x4BC` | Dump (UPROPERTY) | float |
| `HealthComponent.CustomPropertyHealthMultiplier` | HealthComponent | CustomPropertyHealthMultiplier | `0x4C0` | Dump (UPROPERTY) | float |
| `TeamComponent.TeamDataTable` | TeamComponent | TeamDataTable | `0xE0` | Dump (UPROPERTY) | FDataTableRowHandle (RowName = time/facção) |
| `DataTableRowHandle.RowName` | DataTableRowHandle | RowName | `0x8` | Dump (UPROPERTY) | FName |
| `HarvestNode.NodeData` | HarvestNode | NodeData | `0x360` | Dump (UPROPERTY) | FDataTableRowHandle (RowName = tipo do nó) |
| `HarvestNode.HealthComponent` | HarvestNode | HealthComponent | `0x378` | Dump (UPROPERTY) | UHealthComponent* (Dead = já quebrado/coletado) |
| `MilkNugget.EMolarTypeForTooltip` | BP_Milk_Nugget_Personal_C | EMolarTypeForTooltip | `0x618` | Dump (UPROPERTY) | ECollectibleType (2 Upgrade, 3 UpgradeParty, 4 UpgradeBuggy) |
| `SpawnedItem.PlacedItemData` | SpawnedItem | PlacedItemData | `0x4E0` | Dump (UPROPERTY) | FDataTableRowHandle do item posicionado (RowName = item) |
| `SpawnedItem.Item` | SpawnedItem | Item | `0x548` | Dump (UPROPERTY) | UItem* do item no chão |
| `Item.ItemDataRowHandle` | Item | ItemDataRowHandle | `0x168` | Dump (UPROPERTY) | FDataTableRowHandle_NetCrc32 (RowName @+0x8 = item) |
| `SurvivalCharacter.TameableComponent` | SurvivalCharacter | TameableComponent | `0x14A8` | Dump (UPROPERTY) | UTameableComponent* |
| `TameableComponent.TameData` | TameableComponent | TameData | `0x120` | Dump (UPROPERTY) | UTamedCreatureData* (null = não domado) |
| `TamedCreatureData.TameState` | TamedCreatureData | TameState | `0x168` | Dump (UPROPERTY) | ETamedState (0 None, 1 New, 2 Tame) |
| `BossManager.bBossActive` | BossManager | bBossActive | `0x438` | Dump (UPROPERTY) | bool (luta em andamento) |
| `Actor.BlueprintCreatedComponents` | Actor | BlueprintCreatedComponents | `0x2A8` | Dump (UPROPERTY) | TArray<UActorComponent*> |
| `Actor.InstanceComponents` | Actor | InstanceComponents | `0x298` | Dump (UPROPERTY) | TArray<UActorComponent*> |

## Membros não refletidos — como cada um é validado

| Membro | Padrão | Candidatos testados | Critério de aceitação |
|---|---|---|---|
| `ULevel::Actors` | 0xA0 (validado no jogo; a estimativa inicial era 0x98) | 0x80–0xB8 | TArray plausível e ≥ 80% das amostras têm classe derivada de `Actor` |
| `USceneComponent::ComponentToWorld` | 0x1F0 | 0x1B0–0x260 (passo 16) | Em componente raiz sem `AttachParent`: \|quat\|²≈1, Translation == RelativeLocation (±1), Scale == RelativeScale3D (±0,01) |
| `USceneComponent::Bounds` | 0x118 (validado no jogo; a estimativa inicial era 0x1B8) | ComponentToWorld−0x38, 0x100–0x260 (passo 8) | Origem a < 50 m do ator, extents ∈ (0,1; 50.000), SphereRadius ∈ [maxExtent·0,98 ; \|Extent\|·1,02] |
| `UStruct::SuperStruct` | 0x40 | 0x30–0x58 | Classe de `UWorld` → SuperStruct de nome `Object` → SuperStruct nulo |
| `FNamePool::Blocks` | 0x10 | 0x8, 0x10, 0x18, 0x40 | `FName(0)` == `"None"` |

O padrão 0x1F0 para `ComponentToWorld` é dedução de layout (confirmada no jogo). `Bounds` ficou na lacuna
0x110–0x150, entre `ClientAttachedChildren` e `RelativeLocation` (validado em 0x118), e `FTransform` (0x60, alinhado em 16) em 0x1F0–0x250, que é
exatamente o tamanho de `USceneComponent` no dump (592 = 0x250). Mesmo assim o valor só é usado depois de validado.

## Layout fixo da engine (UE 5.x)

| Estrutura | Campo | Offset |
|---|---|---|
| UObjectBase | VTable · ObjectFlags · InternalIndex | 0x00 · 0x08 · 0x0C |
| UObjectBase | ClassPrivate · NamePrivate · OuterPrivate | 0x10 · 0x18 · 0x20 (sizeof = 0x28, confirmado: `Object.props_size = 40`) |
| FName | ComparisonIndex · Number | 0x0 · 0x4 (8 bytes) |
| FNameEntryHandle | Block = idx >> 16 · Offset = (idx & 0xFFFF) × 2 | — |
| FNameEntryHeader | bIsWide:1 · LowercaseProbeHash:5 · Len:10 | uint16 |
| FUObjectArray | ObjObjects (FChunkedFixedUObjectArray) | 0x10 → NumElements em 0x24 |
| TArray | Data · Num · Max | 0x0 · 0x8 · 0xC |
| FVector / FRotator | X,Y,Z / Pitch,Yaw,Roll (double) | 24 bytes |
| FTransform | Rotation(FQuat) · Translation · Scale3D | 0x00 · 0x20 · 0x40 (96 bytes) |
| FBoxSphereBounds | Origin · BoxExtent · SphereRadius | 0x00 · 0x18 · 0x30 (56 bytes) |

## Classes relevantes do jogo (módulo `Maine`)

| Classe | Super | Observação |
|---|---|---|
| `SurvivalCharacter` | Character | base de criaturas e jogadores (7.952 bytes) |
| `SurvivalCreature` | SurvivalCharacter | **filtro do ESP** (qualquer classe cuja cadeia contenha este nome) |
| `FlySwimSurvivalCharacter`, `FlyingSurvivalCharacter`, `SwimmingSurvivalCharacter` | SurvivalCreature | voadores / nadadores |
| `SurvivalPlayerCharacter` | SurvivalCharacter | jogadores (opção "Mostrar jogadores") |
| `HealthComponent` | ActorComponent | vida; validado pela cadeia de classes ao criar cada criatura |
| `TeamComponent` | ActorComponent | time/facção via DataTableRowHandle |
| `CreatureLODActor` / `HealthLODComponent` | BaseLODActor (UObject) | *proxies* de criaturas distantes — **não** são AActor e não têm posição; fora do ESP |

Exemplos de blueprints de criaturas presentes no dump: `BP_Ant_Soldier_Augusta_C`, `BP_Spider_Web_Augusta_C`,
`BP_Ladybug_Augusta_C`, `BP_Weevil_C`, `BP_Mite_C`, `BP_Aphid_C`, `BP_Gnat_C`, `BP_Grub_C`, `BP_Termite_Nymph_Augusta_C`, `BP_SubjectV_C`.

## Coletáveis — Milk Molars

No dump, **nenhum ator** tem "MilkMolar" no nome da classe: o termo só aparece em widgets do menu de upgrade
(`WBP_MilkMolarMenu_C`, `WBP_PersonalMilkMolarButton_C`…) e no áudio do nó (`SFX Obj Milk Molar Loop Cue`).
Os molares do mundo são *harvest nodes*:

```
BP_Milk_Nugget_Personal_C  (Milk Molar)          ┐
BP_Milk_Nugget_Party_C     (Milk Molar de grupo) ├─► BP_ToppleHarvestNode_C ► BP_StaticHarvestNode_C ► HarvestNode ► PlaceableActor ► Actor
BP_Milk_Nugget_Buggies_C   (Milk Molar de buggy) ┘
  +0x618 EMolarTypeForTooltip : ECollectibleType  (0 Invalid · 1 Color · 2 Upgrade · 3 UpgradeParty · 4 UpgradeBuggy)
HarvestNode
  +0x350 DisplayName (LocString) · +0x360 NodeData (DataTableRowHandle) · +0x378 HealthComponent · +0x3A8 LiteData
```

Critério do ESP (`EntityManager.TryBuildCollectible`): o nome de alguma classe da cadeia, normalizado (minúsculas, sem
`_` e espaços), contém um termo de `CollectibleFilters` (padrão `milkmolar`, `milknugget`), **ou**, para HarvestNodes,
o `NodeData.RowName` contém o termo. `HealthState == Dead` no `HealthComponent` do nó = molar já quebrado/coletado.

Como os demais harvest nodes, os molares distantes podem existir só como *proxy* LOD (`HarvestNodeLiteData`, sem AActor);
eles entram no ESP quando o jogo os carrega como ator completo (`LODFullActorMaxDistance`).

## Coletáveis — Tech Chips

Identificado pelo diagnóstico F9 no jogo (a 3,8 m do jogador):

```
BP_SuperTechChip_Ominent_Stolen_C ► BP_SuperTechChip_Ominent_Base_C ► BP_TechChip_Super_Base_C ► BP_TechChip_Base_C ► BP_WorldItem_C ► SpawnedItem
  obj  = BP_SuperTechChip_Ominent_Stolen_C_2147228128
  SpawnedItem +0x4E0 PlacedItemData.RowName = 'TechChip_Ominent_Super_Stolen'
  SpawnedItem +0x548 Item ─► UItem +0x168 ItemDataRowHandle.RowName = 'TechChip_Ominent_Super_Stolen'
```

Todos os Tech Chips derivam de `BP_TechChip_Base_C`, então o termo `techchip` casa pela cadeia de classes (passo 1,
cacheado por classe, sem leitura extra por ator). O RowName é lido só para o nome exibido:
`TechChip_Ominent_Super_Stolen` → "Super Tech Chip (Ominent · Stolen)".

O dump lista 17 dados de spawn: 5 Super Tech Chips (`SD_Augusta_SuperTechChip_Ominent_{BlackAnthill, Greenhouse,
NetworkCenter, Stolen, Waterlogged}`), 9 Tech Chips Ominent (`Alternate_Bridge/Mountain/Pond, Hedge, PergolaNest, Pergola,
Pineroot, PumpkinPatch, Toxic`) e 3 em corpos (`DeadBody_IceCart_Scientist`, `DeadBody_PineRoots`, `DeadBody_ToxixAnthill_Ranger`).

O termo "opticaldisk", usado numa versão anterior, não corresponde a nenhum objeto do mundo
(só a um dado de spawn, `..._DeadTreeOpticalDisk_C`). Ele é removido automaticamente de `config.json`.

Quando o chip é pego, o ator é destruído e sai do ESP. Chips dentro de baús ou inventários não aparecem.

## Pets e buggies (criaturas domadas)

```
SurvivalCharacter +0x14A8 TameableComponent ─► UTameableComponent
                               +0x120 TameData ─► UTamedCreatureData
                                          +0x168 TameState : ETamedState (0 None · 1 New · 2 Tame)
```

A criatura é tratada como domada quando `TameState == Tame`. Esse estado é lido a cada frame, porque muda quando
você doma a criatura. Também conta como domada quando alguma classe da cadeia tem "Buggy" ou "Pet" como palavra do nome
(ex.: `BP_Ant_Soldier_Augusta_Buggy_C`, `BP_Ladybug_Augusta_Buggy_C`). Com `AimIgnoreTamed` (padrão), o AimBot não
mira essas criaturas, e o ESP marca o nome com "[pet]".

## Coletáveis — Keys e Recipes

Identificados pelo diagnóstico F9 (log de 06/11):

```
BP_Key_CoaltanaMats_C   ► BP_WorldItem_C ► SpawnedItem      item = 'KeyCoaltanaMats'
BP_Recipe_SpicyKatana_C ► BP_WorldNote_C ► BP_WorldItem_C   item = 'RecipeSpicyKatana'
```

Essas classes não aparecem no dump, porque o jogo só as carrega quando o item existe no mundo. Uma busca por
substring, como a usada para molars e chips, daria falsos positivos:

| Termo | Casaria por engano com | Motivo |
|---|---|---|
| key | `BP_KeyLime_SowSpot_C`, `BP_KeylimeTree_C`, `BP_Haulable_KeyLime_Seed_C` | limão-taiti |
| recipe | `BP_Recipe_Workbench_C` | bancada construída |

Por isso `CollectibleItemTokens` usa outra regra: o termo precisa ser uma **palavra inteira** do nome de uma classe da cadeia
(separada por `_`, sem diferenciar maiúsculas), e só vale para atores derivados de `SpawnedItem`.
`Key` casa `BP_Key_*`, mas não `KeyLime`. `Recipe` casa `BP_Recipe_SpicyKatana_C`, mas não a bancada (que não é item).
O nome exibido vem do RowName do item: `KeyCoaltanaMats` → "Key Coaltana Mats".

## Coletáveis — Unique Chests (baús enterrados)

Identificados pelo diagnóstico F9 (log de 07/11):

```
BP_World_Buried_UniqueChest_IceCart_C  ┐
BP_World_Buried_UniqueChest_Coaltana_C ┴► BP_World_Buried_UniqueChest_Base_C ► BP_World_BASE_Buried_Item_C ► BP_StaticHarvestNode_C ► HarvestNode
```

O termo `uniquechest` casa pela cadeia de classes (busca por substring, cacheada por classe). No dump não há falsos
positivos: além dos baús, só aparecem os dados de spawn `SD_Augusta_World_Buried_UniqueChest_{Coaltana, IceCart}_C`,
que não são atores. Como os baús são HarvestNodes, `HealthComponent.HealthState == Dead` indica baú já desenterrado.
Nome exibido: `BP_World_Buried_UniqueChest_IceCart_C` → "Unique Chest (Ice Cart)".

## Arenas de chefes — BossManager

```
BP_BossManager_{Centipede, Lizard, SubjectV, SubjectV_Garden_PROTOWALL_5thMounted, Tarantula}_C ► BP_BossManager_C ► BossManager ► Actor
BossManager +0x438 bBossActive (bool) · +0x444 BossActor (TWeakObjectPtr<SurvivalCharacter>) · +0x340 SummonRecipe
```

O `BossManager` é o ator que controla a arena de cada chefe: o volume da arena, o encontro e o altar de invocação.
A posição dele (RootComponent) fica no altar. O termo `bossmanager` casa pela cadeia de classes, sem falsos positivos
no dump. O nome exibido vem da classe: `BP_BossManager_Tarantula_C` → "Boss: Tarantula". Durante o combate
(`bBossActive != 0`), o rótulo ganha "(luta ativa)". Não existe campo de "chefe derrotado" na classe, então a arena
continua marcada depois da vitória. Para escondê-la, remova `bossmanager` de `CollectibleFilters` e inclua-o em
`RemovedDefaultFilters`.

## Pontos fracos (weak spots)

No jogo, os pontos fracos são **componentes de colisão do blueprint de cada criatura**, nomeados pelo designer:

| Classe (dump) | Componentes | Tipo |
|---|---|---|
| `BP_Ant_C` (e derivadas: soldado, operária, buggy…) | `LeftEyeWeakspot`, `RightEyeWeakspot` | BoxComponent |
| `BP_Ladybug_Augusta_C` | `LeftLegWeakpoint1/2`, `RightLegWeakpoint1/2` | BoxComponent |
| `BP_Bee_C` | `LeftEyeWeakspot`, `RightEyeWeakspot` | BoxComponent |
| `BP_Snail_Base_C` (caracóis) | `LeftEyeWeakPoint`, `RightEyeWeakPoint` | BoxComponent |
| `BP_Scorpion_Adult_Northern_C`, `BP_Scorpion_Jr_Northern_C` | `TailWeakpoint`, `BackWeakpoint` | BoxComponent |

Também confirmados em jogo (log de 08/11): `BP_Ant_HillWorker_C`, `BP_Ant_Soldier_Augusta_C`, `BP_Bee_C`, `BP_Snail_Big_C`.
Entre as 41 classes de criatura carregadas no dump, as demais (pulgão, lagarta, barata, larva, ácaro, mosquito, aranhas,
cupim, Subject V…) não têm componente de ponto fraco, então a mira vai no corpo delas. O louva-a-deus (`BP_PrayingMantis_C`) tem os olhos
em `LeftEyeCollision`/`RightEyeCollision` (BoxComponent). Por isso `EyeCollision` faz parte dos termos padrão de `WeakSpotTerms`.

O dump só contém os blueprints carregados no momento em que foi gerado. Por isso a detecção é feita **em tempo de execução**,
para qualquer criatura:

```
Actor +0x2A8 BlueprintCreatedComponents (TArray<UActorComponent*>)  ┐  nome do componente contém "weak" (WeakSpotTerms)
Actor +0x298 InstanceComponents         (TArray<UActorComponent*>)  ┘  e a classe deriva de SceneComponent
      → posição por frame: componente +0x1F0 ComponentToWorld.Translation (+0x20)
```

A busca roda uma vez por criatura, quando ela entra no scan. O log registra uma linha por classe:
`Pontos fracos de BP_Ant_Soldier_Augusta_C: LeftEyeWeakspot, RightEyeWeakspot` ou `... nenhum componente com weak no nome`.
Se alguma criatura usar outro padrão de nome, acrescente o termo em `WeakSpotTerms` no `config.json`.

AimBot (`AimWeakSpots`): mira no ponto fraco **mais próximo da mira** entre os visíveis; se a criatura não tiver nenhum,
volta para o corpo (`AimHeight`). ESP (`EspWeakSpots`): círculo amarelo em cada ponto fraco.

## Flechas no chão — Arrow

Identificadas pelo diagnóstico F9 (logs de 08/11): `BP_World_Arrow_C ► BP_WorldItem_C ► SpawnedItem`, item `Arrow`.
O dump também tem os dados de spawn `SD_Augusta_World_Arrow{, _Mint, _Poison, _Spicy}_C`.

Regra: "Arrow" como **palavra inteira** (entre `_`) do nome de uma classe da cadeia, só em itens no chão (`SpawnedItem`).
Isso exclui `ArrowComponent` (componente de editor), `…CenterNarrow…` (widget) e qualquer outro objeto que não seja item.
Categoria própria (`EntityManager.ArrowCategory = "arrow"`), com opção independente `EspArrows`, que mostra as flechas
mesmo com os demais coletáveis desligados. Cor: verde-limão (`CollectibleColors.arrow`).

## Gotas — Droplet (exceto Dirty)

Classes no dump (todas `… ► SpawnedItemDroplet ► SpawnedItem`):

| Classe | No ESP |
|---|---|
| `BP_World_Droplet_Water_C` | sim — "Droplet: Water" |
| `BP_World_Droplet_Water_Rain_C` | sim — "Droplet: Water (Rain)" |
| `BP_World_Droplet_Water_GrassBlade_A_C` | sim — "Droplet: Water (Grass Blade A)" |
| `BP_World_Droplet_LemonJuice_C` | sim — "Droplet: Lemon Juice" |
| `BP_World_Droplet_Soda_Cola_C` | sim — "Droplet: Soda (Cola)" |
| `BP_World_Droplet_Water_Dirty_C` | **não** (contém "Dirty") |
| `BP_DropletInstanceGroup_C` | **não** (grupo de gotas instanciadas, não é `SpawnedItem`) |

Regra: o ator deriva de `SpawnedItem`, o nome da classe contém "Droplet", nenhuma classe da cadeia contém "Dirty" e o RowName
do item também não. Categoria própria (`DropletCategory = "droplet"`, opção `EspDroplets`), cor azul-claro. A gota some do ESP
quando é bebida (o ator é destruído). Gotas distantes podem existir só como proxy (`ProxySpawnedItemDropletActor`) até o jogo carregá-las.
