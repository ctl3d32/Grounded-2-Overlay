-- ============================================================================
-- Grounded 2 (Steam, UE 5.7) — dados do trinket (acessório) equipado
-- Cheat Engine: Table → Show Cheat Table Lua Script → colar → Execute
-- Requer: CE anexado a Grounded2Steam-Win64-Shipping.exe, dentro do mundo,
--         com um trinket equipado.
--
-- Endereços desta build (ver o log do overlay: "GWorld validado … RVA 0x…"):
local MODULE   = "Grounded2Steam-Win64-Shipping.exe"
local GWORLD   = 0xA6D1970   -- RVA do GWorld
local GNAMES   = 0xA4A40C0   -- RVA do FNamePool (GNames)
-- O Grounded 2 não tem slot próprio de trinket (EEquipmentSlot: MainHand, OffHand, Head, Chest, Arms, Legs,
-- Glider…). O script procura, nos slots equipados, o item cujo blueprint fica em …/Trinkets/ ou …/Accessory…;
-- se nenhum bater, usa a mão secundária. Para forçar um slot, troque nil por "OffHand", "Head" etc.
local SLOT     = nil
-- Imprime os dados e cria/atualiza na tabela do CE o grupo "G2 · Trinket equipado"
-- (UPDATE_TABLE = false desliga). Não escreve nada na memória do jogo.
local UPDATE_TABLE = true
-- ============================================================================

local SLOT_OFFSETS = { MainHand = 0x238, OffHand = 0x240, Head = 0x248, Chest = 0x250, Arms = 0x258, Legs = 0x260 }
local SLOT_NAMES   = { MainHand = "mão principal", OffHand = "mão secundária", Head = "cabeça", Chest = "corporal",
                       Arms = "braços", Legs = "pernas" }
if SLOT ~= nil and SLOT_OFFSETS[SLOT] == nil then print("SLOT inválido: " .. tostring(SLOT)) return end

local base = getAddress(MODULE)
if base == nil or base == 0 then print("Módulo não encontrado. O CE está anexado ao jogo?") return end

local function q(a) if a == nil or a == 0 then return nil end return readQword(a) end
local function i32(a) return readInteger(a) end
local function f32(a) return readFloat(a) end
local function u8(a) return readBytes(a, 1, false) end

-- ---------------------------------------------------------------- FName → texto (FNamePool)
local namePool = base + GNAMES
local nameCache = {}
local function fname(index)
  if index == nil or index <= 0 then return "None" end
  if nameCache[index] then return nameCache[index] end
  local block = index >> 16
  local offset = (index & 0xFFFF) * 2
  local blockPtr = q(namePool + 0x10 + block * 8)
  if blockPtr == nil then return "?" end
  local entry = blockPtr + offset
  local header = readSmallInteger(entry)
  if header == nil then return "?" end
  local len = header >> 6
  local wide = (header & 1) == 1
  local s
  if wide then s = readString(entry + 2, len * 2, true) else s = readString(entry + 2, len, false) end
  s = s or "?"
  nameCache[index] = s
  return s
end
local function objName(obj) if obj == nil or obj == 0 then return "None" end return fname(i32(obj + 0x18)) end

-- ---------------------------------------------------------------- UDataTable.RowMap → linha
local function findRow(dataTable, keyQword)
  if dataTable == nil or keyQword == nil then return nil end
  local data = q(dataTable + 0x30)
  local count = i32(dataTable + 0x38)
  if data == nil or count == nil or count <= 0 or count > 100000 then return nil end
  for i = 0, count - 1 do
    local e = data + i * 0x18
    if readQword(e) == keyQword then return q(e + 8) end
  end
  return nil
end

-- EStatusEffectType (índice = valor do enum)
local EFFECT_TYPES = {
  "None", "MoveSpeed", "CrouchMoveSpeed", "Immunity", "MinHealthPercent", "MaxHealth", "MaxStamina",
  "MaxStun", "StunImmunity", "StaminaSprint", "StaminaDodge", "StaminaAttack", "SwimSpeed", "MaxHauling",
  "FallDamage", "WaterBreathing", "TeamChange", "HungerRate", "ThirstRate", "StaminaRegenRate",
  "DisableMovement", "SpawnActor", "WaterBreathingRate", "AttackDamage", "Health", "HealthPercentage",
  "Stamina", "Food", "Water", "DamageReduction", "DamageReductionMultiplier", "CriticalHitChance",
  "ReviveTime", "ApplyAttackEffect", "AllowAirbornStaminaRegen", "PerfectBlockTime", "ApplyBlockEffect",
  "ReflectDamage", "LifeSteal", "Invisibility", "Stun", "TriggerExtraAttack",
  "TriggerExtraAttackOnDodgeBegin", "EquipmentDurability", "StaminaCapMultiplier", "DirtyWater",
  "GliderDurabilityDrainMultiplier", "AttackSpeed", "Sizzle", "StaminaDelay", "SizzleRateMultiplier",
  "ApplyBlockEffectSelf", "TokenCooldownTime", "AbilityCooldownMultiplier", "ApplyCritEffect",
  "ApplyCritEffectSelf", "ThreatMultiplier", "HealingReceived", "AttackChargeTimeMultiplier",
  "ExhaustionTimeMultiplier", "SummonStatusEffect", "ApplyKillEffect", "CriticalDamage", "StunDamage",
  "WeakpointDamage", "Luck", "FreeAmmoChance", "StealChance", "Mountable", "AdditionalInventorySlots",
  "PetRestriction", "ForwardMoveSpeed", "Recruit", "RecruiteeStart", "RecruiteeEnd", "Recruited", "Chill",
  "ChillRateMultiplier", "Saddleable", "ApplyDodgeEffectSelf", "ApplyDashStrikeEffectSelf",
  "ApplyOnDamagedEffectSelf", "ApplyOnDeathEffectSelf", "ApplyLootCorpseEffectSelf", "Fear",
  "PerfectChargedTime", "BlockStaminaCostMultiplier", "ApplyCritEffectBuggy", "ApplyEffectBuggy",
  "FrozenShatterDamage", "FrozenShatterDamageReduction", "ProximityTriggerSelf", "ProximityTrigger", "Venom",
  "VenomRateMultiplier", "Corrosion", "CorrosionRateMultiplier", "JumpZVelocityMultiplier",
  "PerceptionRangeMultiplier", "ExtraTargetTokens", "ApplyEffectOnBreakingBlock", "PerformAttack",
  "SpawnActorsAttachedToPlayers", "Ghost", "MinimumFallDamageVelocity", "StaminaPercentage",
  "NoStaggerHitReaction", "PoisonGas", "PoisonGasRateMultiplier", "AdditionalBuggyInventorySlots",
  "AdditionalReviveHealthPercentage", "HatchingTimeMultiplier", "AdditionalHotpouchPages", "AttackTier",
  "MaxHealthMultiplier", "SetGravityScale", "ByDamageTypeDamageMultiplier", "BlockDamageMultiplier",
  "ImmunityToForceDismountOnBlockedHit", "ImmunityToStunOnBlockedHit", "RecruitsAdditionalStatusEffect",
  "RecruitsAdditionalNumber", "DisableSwimming", "ApplyStatusEffectSelf", "ApplyStatusEffectMount",
  "DisableAttack", "DisableBlock", "DisableThrow", "TriggerExtraAttackOnHit", "StaminaDodgeSwimming",
  "WaterBreathingRestore", "PassiveAttackPatterns", "ApplyInCombatEffectSelf",
  "TriggerExtraAttackPercentageDamage", "OffsetStatusEffectDuration", "AdditionalAmmoCharges",
  "AttackRangeMultiplier", "JumpZVelocityWater", "JumpZVelocityGround", "ApplySelfReviveEffect",
  "AutoPickUpModeMoveSpeed", "GroundFriction", "BrakingDeceleration", "PerfectDodgeTime",
  "ApplyPerfectDodgeEffectSelf"
}
local DURATION_TYPES = { [0] = "Duração", [1] = "Infinito", [2] = "Instantâneo", [3] = "Dia/noite" }
local function effectType(v) return (v ~= nil and EFFECT_TYPES[v + 1]) or ("Tipo " .. tostring(v)) end

-- ---------------------------------------------------------------- cadeia até o EquipmentComponent
local chain = {
  {"UWorld", nil}, {"GameInstance", 0x230}, {"LocalPlayers", 0x38}, {"LocalPlayer[0]", 0x0},
  {"PlayerController", 0x30}, {"Pawn", 0x380}, {"EquipmentComponent", 0x1448},
}
local p = q(base + GWORLD)
if p == nil then print(string.format("Falhou ao ler GWorld (%X). Confira o RVA no log do overlay.", base + GWORLD)) return end
for i = 2, #chain do
  local nxt = q(p + chain[i][2])
  if nxt == nil then
    print(string.format("Falhou em %s (%X + 0x%X). Está dentro de um mundo?", chain[i][1], p, chain[i][2]))
    return
  end
  p = nxt
end
local equipment = p

-- Caminho do blueprint do item (BaseItemData.WorldActor / PlayerEquippedActor: TSoftClassPtr, PackageName em +0x8).
local function itemPath(r)
  local a = fname(i32(r + 0x158 + 0x8))
  local b = fname(i32(r + 0x200 + 0x8))
  return a .. " | " .. b
end
local function isTrinketPath(path)
  local l = path:lower()
  return l:find("trinket", 1, true) ~= nil or l:find("accessory", 1, true) ~= nil
end

-- ---------------------------------------------------------------- acha o trinket
local item, row, slotName, path
local order = { "OffHand", "Head", "Chest", "Arms", "Legs", "MainHand" }
for _, sn in ipairs(SLOT and { SLOT } or order) do
  local it = q(equipment + SLOT_OFFSETS[sn])
  if it ~= nil then
    local r = findRow(q(it + 0x168), readQword(it + 0x170))
    if r ~= nil then
      local pth = itemPath(r)
      if SLOT ~= nil or isTrinketPath(pth) then item, row, slotName, path = it, r, sn, pth break end
    end
  end
end
if item == nil then
  local it = q(equipment + SLOT_OFFSETS.OffHand)
  local r = it and findRow(q(it + 0x168), readQword(it + 0x170))
  if r == nil then print("Nenhum trinket equipado (nem item na mão secundária).") return end
  item, row, slotName, path = it, r, "OffHand", itemPath(r)
  print("Aviso: nenhum item com blueprint de trinket/acessório; usando o item da mão secundária.")
end
local SLOT_LABEL = SLOT_NAMES[slotName]

local function line(fmt, ...) print(string.format(fmt, ...)) end
local function addr(off) return row + off end

-- ---------------------------------------------------------------- tabela do Cheat Engine
-- Grupos e entradas são achados pela descrição dentro do grupo-pai: se já existem, só o endereço/tipo é
-- atualizado (valores congelados, cores e hotkeys do usuário são mantidos); se não, são criados.
local AL = getAddressList()
local VT = { byte = vtByte, int = vtDword, float = vtSingle }

local function childByDesc(parent, desc)
  if parent == nil then
    for i = 0, AL.Count - 1 do
      local mr = AL[i]
      if mr.Parent == nil and mr.Description == desc then return mr end
    end
    return nil
  end
  for i = 0, parent.Count - 1 do
    local mr = parent.Child[i]
    if mr.Description == desc then return mr end
  end
  return nil
end

local function group(parent, desc)
  if not UPDATE_TABLE then return nil end
  local g = childByDesc(parent, desc)
  if g == nil then
    g = AL.createMemoryRecord()
    g.Description = desc
    g.IsGroupHeader = true
    g.options = "[moHideChildren]"
    if parent ~= nil then g.appendToEntry(parent) end
  end
  return g
end

local function rec(parent, desc, address, kind)
  if not UPDATE_TABLE or parent == nil or address == nil then return end
  local mr = childByDesc(parent, desc)
  if mr == nil then
    mr = AL.createMemoryRecord()
    mr.Description = desc
    mr.appendToEntry(parent)
  end
  mr.Address = string.format("%X", address)
  mr.Type = VT[kind]
  if kind == "byte" then mr.ShowAsHex = false end
end

-- Remove filhos de um grupo que não foram (re)criados nesta execução (ex.: arma nova com menos golpes).
local function prune(parent, keep)
  if parent == nil then return end
  for i = parent.Count - 1, 0, -1 do
    local mr = parent.Child[i]
    if not keep[mr.Description] then mr.destroy() end
  end
end

local gRoot  = group(nil, "G2 · Trinket equipado")
local gItem  = group(gRoot, "Este item (só esta cópia)")
local gRow   = group(gRoot, "Linha da tabela (todos os trinkets iguais)")

print("==================================================================")
line("Trinket (%s): %s   (UItem %X · linha %X)", SLOT_LABEL, fname(i32(item + 0x170)), item, row)
line("Blueprint: %s", path)
print("==================================================================")

-- ---------------------------------------------------------------- dados do próprio item (instância)
print("-- Este item (só esta cópia)")
line("  Durabilidade restante        %.1f        [%X]", f32(item + 0x298), item + 0x298)
line("  CustomMaxDurability          %.1f        [%X]", f32(item + 0x158), item + 0x158)
line("  CustomUnbreakable            %d", u8(item + 0x15C))
line("  UpgradedTier / CustomTier    %d / %d", i32(item + 0xC0), i32(item + 0x160))
line("  Aprimoramento base/bônus/lend. %d / %d / %d", i32(item + 0x2C0), i32(item + 0x2C4), i32(item + 0x2D0))
for _, r in ipairs({
  {"Durabilidade restante", 0x298, "float"}, {"CustomMaxDurability", 0x158, "float"}, {"CustomUnbreakable", 0x15C, "byte"},
  {"UpgradedTier", 0xC0, "int"}, {"CustomTier", 0x160, "int"},
  {"Aprimoramento base", 0x2C0, "int"}, {"Aprimoramento bônus", 0x2C4, "int"}, {"Aprimoramento lendário", 0x2D0, "int"},
}) do rec(gItem, r[1], item + r[2], r[3]) end

-- ---------------------------------------------------------------- linha da tabela (todas as cópias)
print("-- Linha da tabela (vale para todos os trinkets iguais)")
line("  Tier                         %d          [%X]", i32(addr(0x1B8)), addr(0x1B8))
line("  Durabilidade máxima          %.1f       [%X]", f32(addr(0x338)), addr(0x338))
line("  FlatDamageReduction          %.3f       [%X]", f32(addr(0x33C)), addr(0x33C))
line("  PercentageDamageReduction    %.3f       [%X]", f32(addr(0x340)), addr(0x340))
line("  RandomEffectType (Tier)      %d", u8(addr(0x380)))
line("  bCanBlock                    %d", u8(addr(0x3C8)))
line("  Slot                         %d", u8(addr(0x398)))
line("  bCanUpgradeTier              %d", u8(addr(0x1F0)))
line("  byte de flags 0x1BC (bWorldMarker/bCanEnhance) 0x%02X", u8(addr(0x1BC)))
line("  RecycleReward                %d", i32(addr(0xB0)))
line("  RarityTag                    %s", fname(i32(addr(0xB4))))
line("  BuoyancyForceMultiplier      %.3f", f32(addr(0x504)))
for _, r in ipairs({
  {"Tier", 0x1B8, "int"}, {"Durabilidade máxima", 0x338, "float"},
  {"FlatDamageReduction", 0x33C, "float"}, {"PercentageDamageReduction", 0x340, "float"},
  {"bCanUpgradeTier", 0x1F0, "byte"}, {"RecycleReward", 0xB0, "int"}, {"BuoyancyForceMultiplier", 0x504, "float"},
}) do rec(gRow, r[1], addr(r[2]), r[3]) end

-- ---------------------------------------------------------------- efeitos da armadura (StatusEffects)
-- Cada efeito é uma linha da tabela de status effects: alterar Value muda o efeito em todo item que o usa.
local function dumpEffects(off, title)
  local data = q(addr(off)); local n = i32(addr(off + 8))
  if data == nil or n == nil or n <= 0 then
    local old = UPDATE_TABLE and gRoot ~= nil and childByDesc(gRoot, title) or nil
    if old ~= nil then old.destroy() end
    return
  end
  line("-- %s (%d)", title, n)
  local gEff = group(gRoot, title)
  local keep = {}
  for k = 0, math.min(n, 32) - 1 do
    local h = data + k * 0x10
    local rowName = fname(i32(h + 8))
    local eff = findRow(q(h), readQword(h + 8))
    if eff == nil then
      line("  %d. %s: linha não encontrada", k + 1, rowName)
    else
      local typ = effectType(u8(eff + 0x30))
      local dmgType = q(eff + 0x50)
      line("  %d. %s   [linha %X]", k + 1, rowName, eff)
      line("       Tipo %s   Valor %.4f  [%X]   %s %.1fs   Intervalo %.2f   Pilhas %d%s",
           typ, f32(eff + 0x34), eff + 0x34, DURATION_TYPES[u8(eff + 0x38)] or "?", f32(eff + 0x3C),
           f32(eff + 0x40), i32(eff + 0x44), dmgType and ("   Dano: " .. objName(dmgType)) or "")

      local gName = string.format("%d. %s (%s)", k + 1, rowName, typ)
      keep[gName] = true
      local gE = group(gEff, gName)
      if gE ~= nil then
        local keepE = {}
        for _, r in ipairs({
          {"Valor", 0x34, "float"}, {"Duração", 0x3C, "float"}, {"Intervalo", 0x40, "float"}, {"MaxStackCount", 0x44, "int"},
        }) do rec(gE, r[1], eff + r[2], r[3]); keepE[r[1]] = true end
        prune(gE, keepE)
      end
    end
  end
  prune(gEff, keep)
end
dumpEffects(0x348, "Efeitos ao equipar")
dumpEffects(0x358, "Efeitos ocultos")

print("==================================================================")
if UPDATE_TABLE then
  print("Tabela do CE atualizada: grupo \"G2 · Trinket equipado\". Rode de novo ao trocar de trinket.")
else
  print("Os valores entre [ ] são endereços: dá para adicioná-los à tabela do CE (Float / 4 Bytes / Byte).")
end
