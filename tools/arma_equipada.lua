-- ============================================================================
-- Grounded 2 (Steam, UE 5.7) — dados da arma na mão principal
-- Cheat Engine: Table → Show Cheat Table Lua Script → colar → Execute
-- Requer: CE anexado a Grounded2Steam-Win64-Shipping.exe, dentro do mundo,
--         com uma arma equipada na mão principal.
--
-- Endereços desta build (ver o log do overlay: "GWorld validado … RVA 0x…"):
local MODULE   = "Grounded2Steam-Win64-Shipping.exe"
local GWORLD   = 0xA6D1970   -- RVA do GWorld
local GNAMES   = 0xA4A40C0   -- RVA do FNamePool (GNames)
-- Imprime os dados e cria/atualiza na tabela do CE o grupo "G2 · Arma equipada"
-- (UPDATE_TABLE = false desliga). Não escreve nada na memória do jogo.
local UPDATE_TABLE = true
-- ============================================================================

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

-- ---------------------------------------------------------------- cadeia até a arma
local chain = {
  {"UWorld", nil}, {"GameInstance", 0x230}, {"LocalPlayers", 0x38}, {"LocalPlayer[0]", 0x0},
  {"PlayerController", 0x30}, {"Pawn", 0x380}, {"EquipmentComponent", 0x1448}, {"MainHand", 0x238},
}
local p = q(base + GWORLD)
if p == nil then print(string.format("Falhou ao ler GWorld (%X). Confira o RVA no log do overlay.", base + GWORLD)) return end
for i = 2, #chain do
  local nxt = q(p + chain[i][2])
  if nxt == nil then
    print(string.format("Falhou em %s (%X + 0x%X). Está num mundo com uma arma na mão principal?", chain[i][1], p, chain[i][2]))
    return
  end
  p = nxt
end

local item = p
local itemTable = q(item + 0x168)
local itemKey = readQword(item + 0x170)
local row = findRow(itemTable, itemKey)
if row == nil then print("Linha da arma não encontrada na tabela de itens.") return end

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

local gRoot  = group(nil, "G2 · Arma equipada")
local gItem  = group(gRoot, "Este item (só esta cópia)")
local gRow   = group(gRoot, "Linha da tabela (todas as armas iguais)")

print("==================================================================")
line("Arma: %s   (UItem %X · linha %X)", fname(i32(item + 0x170)), item, row)
print("==================================================================")

-- ---------------------------------------------------------------- dados do próprio item (instância)
print("-- Este item (só esta cópia)")
line("  Durabilidade restante        %.1f        [%X]", f32(item + 0x298), item + 0x298)
line("  UpgradedTier / CustomTier    %d / %d", i32(item + 0xC0), i32(item + 0x160))
line("  CustomAttackMultiplier       %.3f", f32(item + 0x164))
line("  Aprimoramento base/bônus/lend. %d / %d / %d", i32(item + 0x2C0), i32(item + 0x2C4), i32(item + 0x2D0))
rec(gItem, "Durabilidade restante", item + 0x298, "float")
rec(gItem, "UpgradedTier", item + 0xC0, "int")
rec(gItem, "CustomTier", item + 0x160, "int")
rec(gItem, "CustomAttackMultiplier", item + 0x164, "float")
rec(gItem, "Aprimoramento base", item + 0x2C0, "int")
rec(gItem, "Aprimoramento bônus", item + 0x2C4, "int")
rec(gItem, "Aprimoramento lendário", item + 0x2D0, "int")

-- ---------------------------------------------------------------- linha da tabela (todas as cópias)
print("-- Linha da tabela (vale para todas as armas iguais)")
line("  CritChance                   %.4f      [%X]", f32(addr(0x3C4)), addr(0x3C4))
line("  Tier                         %d          [%X]", i32(addr(0x1B8)), addr(0x1B8))
line("  Durabilidade máxima          %.1f       [%X]", f32(addr(0x338)), addr(0x338))
line("  FlatDamageReduction          %.3f       [%X]", f32(addr(0x33C)), addr(0x33C))
line("  PercentageDamageReduction    %.3f       [%X]", f32(addr(0x340)), addr(0x340))
line("  bCanBlock                    %d          [%X]", u8(addr(0x3C8)), addr(0x3C8))
line("  bCanBlockWhileHauling        %d", u8(addr(0x3C9)))
line("  bCannotBlockWhileAttacking   %d", u8(addr(0x3CA)))
line("  BlockDamageMultiplier        %.3f       [%X]", f32(addr(0x3CC)), addr(0x3CC))
line("  BlockStaminaCost             %.2f       [%X]", f32(addr(0x3D0)), addr(0x3D0))
line("  BlockStaminaRegenMultiplier  %.3f       [%X]", f32(addr(0x3D4)), addr(0x3D4))
line("  TwoHanded                    %d", u8(addr(0x399)))
line("  Slot / Stance / ItemType     %d / %d / %d", u8(addr(0x398)), i32(addr(0x39C)), u8(addr(0xA8)))
line("  bCanUpgradeTier              %d", u8(addr(0x1F0)))
line("  byte de flags 0x1BC (bWorldMarker/bCanEnhance) 0x%02X", u8(addr(0x1BC)))
line("  RecycleReward                %d", i32(addr(0xB0)))
line("  RarityTag                    %s", fname(i32(addr(0xB4))))
line("  BuoyancyForceMultiplier      %.3f", f32(addr(0x504)))
for _, r in ipairs({
  {"CritChance", 0x3C4, "float"}, {"Tier", 0x1B8, "int"}, {"Durabilidade máxima", 0x338, "float"},
  {"FlatDamageReduction", 0x33C, "float"}, {"PercentageDamageReduction", 0x340, "float"},
  {"bCanBlock", 0x3C8, "byte"}, {"bCanBlockWhileHauling", 0x3C9, "byte"}, {"bCannotBlockWhileAttacking", 0x3CA, "byte"},
  {"BlockDamageMultiplier", 0x3CC, "float"}, {"BlockStaminaCost", 0x3D0, "float"},
  {"BlockStaminaRegenMultiplier", 0x3D4, "float"}, {"TwoHanded", 0x399, "byte"}, {"bCanUpgradeTier", 0x1F0, "byte"},
  {"RecycleReward", 0xB0, "int"}, {"BuoyancyForceMultiplier", 0x504, "float"},
}) do rec(gRow, r[1], addr(r[2]), r[3]) end

-- efeitos ao equipar (EquippableData.StatusEffects)
local function listHandles(off, title)
  local data = q(addr(off)); local n = i32(addr(off + 8))
  if data == nil or n == nil or n <= 0 then return end
  local names = {}
  for k = 0, math.min(n, 16) - 1 do names[#names + 1] = fname(i32(data + k * 0x10 + 8)) end
  line("  %-28s %s", title, table.concat(names, ", "))
end
listHandles(0x348, "Efeitos ao equipar")
listHandles(0x358, "Efeitos ocultos")

-- ---------------------------------------------------------------- golpes (tabela de ataques)
local function dumpCombo(off, title)
  local data = q(addr(off)); local n = i32(addr(off + 8))
  local gCombo = group(gRoot, title)
  local keep = {}
  if data == nil or n == nil or n <= 0 then prune(gCombo, keep) return end
  line("-- %s (%d golpes)", title, n)
  for k = 0, math.min(n, 16) - 1 do
    local h = data + k * 0x10
    local atkTable = q(h)
    local atkKey = readQword(h + 8)
    local atk = findRow(atkTable, atkKey)
    if atk == nil then
      line("  %d. %s: linha não encontrada", k + 1, fname(i32(h + 8)))
    else
      line("  %d. %s   [linha %X]", k + 1, fname(i32(h + 8)), atk)
      line("       Dano %.2f (%s)  [%X]   HitStun %d   Poise %.2f   Pushback %.2f",
           f32(atk + 0x08), objName(q(atk + 0x10)), atk + 0x08, i32(atk + 0x1C), f32(atk + 0x20), f32(atk + 0x24))
      line("       Carregado: dano %.2f (%s)  [%X]   Perfeito: dano %.2f",
           f32(atk + 0x78), objName(q(atk + 0x80)), atk + 0x78, f32(atk + 0xD8))
      line("       Alcance %.1f / carregado %.1f   Energia %d / carregado %d  [%X]   Distância: %d",
           f32(atk + 0x148), f32(atk + 0x14C), i32(atk + 0x204), i32(atk + 0x208), atk + 0x204, u8(atk + 0x322))

      local gName = string.format("%d. %s", k + 1, fname(i32(h + 8)))
      keep[gName] = true
      local gAtk = group(gCombo, gName)
      if gAtk ~= nil then
        local keepAtk = {}
        for _, r in ipairs({
          {"Dano", 0x08, "float"}, {"HitStun", 0x1C, "int"}, {"PoiseDamage", 0x20, "float"}, {"Pushback", 0x24, "float"},
          {"Dano carregado", 0x78, "float"}, {"Dano carregado perfeito", 0xD8, "float"},
          {"Alcance", 0x148, "float"}, {"Alcance carregado", 0x14C, "float"},
          {"Energia", 0x204, "int"}, {"Energia carregado", 0x208, "int"},
        }) do rec(gAtk, r[1], atk + r[2], r[3]); keepAtk[r[1]] = true end
        prune(gAtk, keepAtk)
      end
    end
  end
  prune(gCombo, keep)
end
dumpCombo(0x480, "Combo principal")
dumpCombo(0x4A0, "Combo alternativo")
dumpCombo(0x4C0, "Combo na água")
print("==================================================================")
if UPDATE_TABLE then
  print("Tabela do CE atualizada: grupo \"G2 · Arma equipada\". Rode de novo ao trocar de arma.")
else
  print("Os valores entre [ ] são endereços: dá para adicioná-los à tabela do CE (Float / 4 Bytes / Byte).")
end
