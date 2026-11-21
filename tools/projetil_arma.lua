-- ============================================================================
-- Grounded 2 (Steam, UE 5.7) — projétil da arma na mão principal
-- Velocidade (InitialSpeed / MaxSpeed), gravidade (ProjectileGravityScale × gravidade do mundo),
-- tempo de vida e distância máxima (configurada no ataque e calculada pela balística).
-- Cheat Engine: Table → Show Cheat Table Lua Script → colar → Execute
-- Requer: CE anexado a Grounded2Steam-Win64-Shipping.exe, dentro do mundo, com a arma equipada.
--
-- Duas etapas:
--  1) lê o que a tabela do ataque aponta (funciona para o arco: classe de projétil na munição);
--  2) captura ao vivo: depois de executar, DISPARE a arma em até CAPTURE_SECONDS segundos. O script pega o
--     projétil recém-criado pelo seu personagem (funciona para cajados e qualquer arma cujo projétil não
--     está na tabela do ataque) e lê velocidade real, gravidade e tempo de vida.
--
-- Endereços desta build (ver o log do overlay):
local MODULE   = "Grounded2Steam-Win64-Shipping.exe"
local GWORLD   = 0xA6D1970   -- RVA do GWorld
local GNAMES   = 0xA4A40C0   -- RVA do FNamePool (GNames)
local GOBJECTS = 0xA545EA0   -- RVA do FUObjectArray (GObjects)
-- Imprime os dados e cria/atualiza na tabela do CE o grupo "G2 · Projétil da arma"
-- (UPDATE_TABLE = false desliga). Não escreve nada na memória do jogo.
local UPDATE_TABLE = true
local CAPTURE_SECONDS = 15   -- 0 desliga a captura ao vivo
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

-- ---------------------------------------------------------------- GObjects
local objArray = base + GOBJECTS
local objChunks = q(objArray + 0x10)
local objNum = i32(objArray + 0x24) or 0
local itemSize, itemObjOff
local chunkCache = {}
local function rawObjAt(idx, size, off)
  local ci = idx // 0x10000
  local chunk = chunkCache[ci]
  if chunk == nil then chunk = q(objChunks + ci * 8); chunkCache[ci] = chunk end
  if chunk == nil then return nil end
  return q(chunk + (idx % 0x10000) * size + off)
end
if objChunks ~= nil then
  for size = 0x10, 0x40, 8 do
    for _, off in ipairs({ 0, 8 }) do
      local ok = 0
      for i = 1, 24 do
        local o = rawObjAt(i, size, off)
        if o ~= nil and i32(o + 0xC) == i then ok = ok + 1 end
      end
      if ok >= 20 then itemSize, itemObjOff = size, off break end
    end
    if itemSize then break end
  end
end
local function objAt(idx)
  if itemSize == nil or idx == nil or idx < 0 or idx >= objNum then return nil end
  return rawObjAt(idx, itemSize, itemObjOff)
end

-- TSoftClassPtr (40 bytes): FWeakObjectPtr {ObjectIndex, Serial} em +0; FSoftObjectPath: PackageName +0x8, AssetName +0x10.
local classCache = {}
local function resolveSoftClass(sp)
  local assetKey = readQword(sp + 0x10)
  if assetKey == nil or assetKey == 0 then return nil, "None" end
  local assetName = fname(i32(sp + 0x10))
  if classCache[assetKey] ~= nil then return classCache[assetKey], assetName end
  -- 1) ponteiro fraco em cache
  local o = objAt(i32(sp))
  if o ~= nil and readQword(o + 0x18) == assetKey then classCache[assetKey] = o return o, assetName end
  -- 2) varre GObjects pelo nome (alguns segundos)
  for i = 0, objNum - 1 do
    o = objAt(i)
    if o ~= nil and readQword(o + 0x18) == assetKey then classCache[assetKey] = o return o, assetName end
  end
  return nil, assetName
end

-- CDO: ponteiro dentro do UClass para um objeto cuja Class é o próprio UClass e com RF_ClassDefaultObject.
local function findCDO(cls)
  for off = 0x28, 0x300, 8 do
    local p = q(cls + off)
    if p ~= nil and p ~= cls and readQword(p + 0x10) == cls then
      local flags = i32(p + 0x8)
      if flags ~= nil and (flags & 0x10) ~= 0 then return p end
    end
  end
  return nil
end

-- A classe do objeto (ou uma das superclasses, via UStruct.SuperStruct +0x40) tem este nome?
local function isA(obj, className)
  local c = q(obj + 0x10)
  for _ = 1, 32 do
    if c == nil then return false end
    if fname(i32(c + 0x18)) == className then return true end
    c = q(c + 0x40)
  end
  return false
end
local function className(obj) local c = obj and q(obj + 0x10) return c and fname(i32(c + 0x18)) or "?" end

-- Procura dentro de um ator (ou do seu CDO) o ponteiro para o UProjectileMovementComponent. Projectile (nativo)
-- guarda em +0x2F0; outras classes (ex.: projéteis de cajado) guardam em outro lugar ou num componente do blueprint.
local function findPMC(actor)
  if actor == nil then return nil end
  local p0 = q(actor + 0x2F0)
  if p0 ~= nil and isA(p0, "ProjectileMovementComponent") then return p0, 0x2F0 end
  for off = 0x2C0, 0x900, 8 do
    local c = q(actor + off)
    if c ~= nil and c ~= actor and (c & 7) == 0 and c > 0x10000 and isA(c, "ProjectileMovementComponent") then return c, off end
  end
  return nil
end

-- ---------------------------------------------------------------- cadeia até a arma
local chain = {
  {"UWorld", nil}, {"GameInstance", 0x230}, {"LocalPlayers", 0x38}, {"LocalPlayer[0]", 0x0},
  {"PlayerController", 0x30}, {"Pawn", 0x380}, {"EquipmentComponent", 0x1448}, {"MainHand", 0x238},
}
local pawn
local p = q(base + GWORLD)
if p == nil then print(string.format("Falhou ao ler GWorld (%X). Confira o RVA no log do overlay.", base + GWORLD)) return end
for i = 2, #chain do
  local nxt = q(p + chain[i][2])
  if nxt == nil then
    print(string.format("Falhou em %s (%X + 0x%X). Está num mundo com uma arma na mão principal?", chain[i][1], p, chain[i][2]))
    return
  end
  p = nxt
  if chain[i][1] == "Pawn" then pawn = p end
end

local item = p
local itemTable = q(item + 0x168)
local itemKey = readQword(item + 0x170)
local row = findRow(itemTable, itemKey)
if row == nil then print("Linha da arma não encontrada na tabela de itens.") return end
local world = q(base + GWORLD)

local function line(fmt, ...) print(string.format(fmt, ...)) end

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

local gRoot = group(nil, "G2 · Projétil da arma")

-- ---------------------------------------------------------------- gravidade do mundo
-- UWorld.PersistentLevel (+0x30) → ULevel.WorldSettings (+0x2B0) → WorldGravityZ (+0x348) / GlobalGravityZ (+0x34C)
local gravity = -980.0
local level = q(world + 0x30)
local ws = level and q(level + 0x2B0)
if ws ~= nil then
  local wg = f32(ws + 0x348)
  if wg ~= nil and wg ~= 0 then gravity = wg end
end

print("==================================================================")
line("Arma: %s   (UItem %X · linha %X)", fname(i32(item + 0x170)), item, row)
line("Gravidade do mundo: %.1f cm/s²%s", gravity, ws and string.format("   [WorldSettings %X · WorldGravityZ %X]", ws, ws + 0x348) or "  (padrão)")
if itemSize == nil then print("Aviso: layout de GObjects não reconhecido; só dá para resolver classes já em cache.") end
print("==================================================================")
if ws ~= nil then rec(gRoot, "Gravidade do mundo (WorldGravityZ)", ws + 0x348, "float") end

-- ---------------------------------------------------------------- ataques com projétil
local attacks, seenAtk = {}, {}
local function addAttack(handle, label)
  if handle == nil then return end
  local atk = findRow(q(handle), readQword(handle + 8))
  if atk == nil or seenAtk[atk] then return end
  seenAtk[atk] = true
  attacks[#attacks + 1] = { row = atk, name = fname(i32(handle + 8)), label = label }
end

local selectedAmmo = i32(item + 0x1CC)
-- munições aceitas pela arma: BaseItemData.AmmoAttackData (+0x4F0), AttackAmmoData { AmmoItem 0x0, Attack 0x10 }
do
  local data, n = q(row + 0x4F0), i32(row + 0x4F8)
  if data ~= nil and n ~= nil and n > 0 and n < 64 then
    for k = 0, n - 1 do
      local e = data + k * 0x20
      local sel = (i32(e + 8) == selectedAmmo) and " ◄ selecionada" or ""
      addAttack(e + 0x10, "munição " .. fname(i32(e + 8)) .. sel)
    end
  end
end
-- munição selecionada → BaseItemData.AmmoAttackReference (+0x4E0) da própria munição
if selectedAmmo ~= nil and selectedAmmo > 0 then
  local ammoRow = findRow(itemTable, readQword(item + 0x1CC))
  if ammoRow ~= nil then addAttack(ammoRow + 0x4E0, "munição " .. fname(selectedAmmo) .. " ◄ selecionada") end
end
-- referência de ataque da própria arma (+0x4E0) e golpes dos combos
addAttack(row + 0x4E0, "ataque de munição da arma")
for _, c in ipairs({ { 0x480, "combo principal" }, { 0x4A0, "combo alternativo" }, { 0x4C0, "combo na água" } }) do
  local data, n = q(row + c[1]), i32(row + c[1] + 8)
  if data ~= nil and n ~= nil and n > 0 and n < 64 then
    for k = 0, n - 1 do addAttack(data + k * 0x10, string.format("%s, golpe %d", c[2], k + 1)) end
  end
end

local function f(v, fmt) return v ~= nil and string.format(fmt or "%.2f", v) or "?" end

-- Imprime e registra na tabela os dados de um projétil. tmpl = CDO (endereços estáveis); live = ator em voo.
local keepRoot = { ["Gravidade do mundo (WorldGravityZ)"] = true }
local function report(title, groupName, tmpl, live)
  local tPmc = tmpl and findPMC(tmpl)
  local lPmc = live and findPMC(live)
  local src, pmc = tmpl, tPmc
  if pmc == nil then src, pmc = live, lPmc end
  if pmc == nil then line("   %s: ProjectileMovementComponent não encontrado (CDO %s)", title, f(tmpl, "%X")) return end

  local v0, vmax, gs = f32(pmc + 0x130) or 0, f32(pmc + 0x134) or 0, f32(pmc + 0x160) or 1
  local life = f32(src + 0x6C) or 0
  local g = gravity * gs
  line("   %s   [%s %X · PMC %X]", title, (src == tmpl) and "CDO" or "ator em voo", src, pmc)
  if lPmc ~= nil then
    local vx, vy, vz = readDouble(lPmc + 0xE0), readDouble(lPmc + 0xE8), readDouble(lPmc + 0xF0)
    if vx ~= nil then
      local sp = math.sqrt(vx * vx + vy * vy + vz * vz)
      line("     Velocidade medida em voo: %.0f cm/s (%.1f m/s)   vetor (%.0f, %.0f, %.0f)", sp, sp / 100, vx, vy, vz)
      if v0 <= 0 then v0 = sp end
      local lgs = f32(lPmc + 0x160)
      if lgs ~= nil and math.abs(lgs - gs) > 1e-4 then line("     Escala de gravidade no projétil em voo: %.3f (no CDO: %.3f)", lgs, gs) end
    end
  end
  local vEff = (vmax > 0 and v0 > vmax) and vmax or v0
  line("     InitialSpeed %.0f cm/s (%.1f m/s)   MaxSpeed %.0f%s", f32(pmc + 0x130) or 0, (f32(pmc + 0x130) or 0) / 100, vmax,
       (vmax == 0) and " (sem limite)" or "")
  line("     ProjectileGravityScale %.3f → gravidade efetiva %.1f cm/s²   Bounciness %s   Friction %s",
       gs, g, f(f32(pmc + 0x168)), f(f32(pmc + 0x16C)))
  if isA(src, "Projectile") then
    line("     Na água: gravidade ×%s   arrasto %s   para abaixo de %s cm/s", f(f32(src + 0x39C), "%.3f"),
         f(f32(src + 0x398), "%.3f"), f(f32(src + 0x3A0), "%.0f"))
  end
  if life > 0 then
    line("     InitialLifeSpan %.2f s → no máximo %.0f cm (%.1f m) percorridos", life, vEff * life, vEff * life / 100)
  else
    line("     InitialLifeSpan 0 (sem limite de tempo)")
  end
  if vEff > 0 and g ~= 0 then
    local br = vEff * vEff / math.abs(g)
    local cap = (life > 0) and math.min(br, vEff * life * 0.7071) or br
    line("     Alcance balístico máx. (45°, chão plano): %.0f cm (%.1f m)%s", br, br / 100,
         (cap < br) and string.format(" · limitado pelo tempo de vida a ~%.0f cm (%.1f m)", cap, cap / 100) or "")
    line("     Queda após 10 m em linha reta: %.1f cm   Tempo de voo a 45°: %.2f s",
         0.5 * math.abs(g) * (1000 / vEff) ^ 2, 2 * vEff * 0.7071 / math.abs(g))
  elseif vEff > 0 then
    line("     Sem gravidade: segue em linha reta")
  end

  local gP = group(gRoot, groupName)
  keepRoot[groupName] = true
  if gP ~= nil then
    local keep = {}
    local recs = { { "InitialSpeed", pmc + 0x130 }, { "MaxSpeed", pmc + 0x134 }, { "ProjectileGravityScale", pmc + 0x160 },
                   { "InitialLifeSpan", src + 0x6C } }
    if isA(src, "Projectile") then
      recs[#recs + 1] = { "WaterGravityScale", src + 0x39C }; recs[#recs + 1] = { "WaterDrag", src + 0x398 }
    end
    for _, r in ipairs(recs) do rec(gP, r[1], r[2], "float"); keep[r[1]] = true end
    prune(gP, keep)
    if src ~= tmpl then
      line("     (sem CDO com o componente: as entradas da tabela apontam para este projétil e valem só enquanto ele existir)")
    end
  end
end

local found = 0
for _, a in ipairs(attacks) do
  local atk = a.row
  local classes, seenCls = {}, {}
  for _, slot in ipairs({ { 0x228, "normal", 0x2A0 }, { 0x250, "carregado", 0x2A4 }, { 0x278, "carregado perfeito", 0x2A8 },
                          { 0x2E0, "invocação", nil } }) do
    local cls, cname = resolveSoftClass(atk + slot[1])
    if cname ~= "None" then
      if cls ~= nil and seenCls[cls] then
        seenCls[cls].kinds = seenCls[cls].kinds .. " / " .. slot[2]
      else
        local e = { cls = cls, name = cname, kinds = slot[2], num = slot[3] and i32(atk + slot[3]) or 1 }
        classes[#classes + 1] = e
        if cls ~= nil then seenCls[cls] = e end
      end
    end
  end

  if #classes > 0 then
    found = found + 1
    line("-- %s: %s   [linha %X]", a.label, a.name, atk)
    line("   Range %.0f / carregado %.0f   ProjectilePhysicsCheckMaximumDistance %.0f   Homing: dist. %.0f, aceleração %.0f",
         f32(atk + 0x148), f32(atk + 0x14C), f32(atk + 0x210), f32(atk + 0x218), f32(atk + 0x220))
    local gA = group(gRoot, string.format("%d. %s", found, a.name))
    keepRoot[string.format("%d. %s", found, a.name)] = true
    local keepA = {}
    for _, r in ipairs({ { "Range", 0x148 }, { "Range carregado", 0x14C }, { "ProjectilePhysicsCheckMaximumDistance", 0x210 } }) do
      rec(gA, r[1], atk + r[2], "float"); keepA[r[1]] = true
    end
    prune(gA, keepA)
    for _, c in ipairs(classes) do
      if c.cls == nil then
        line("   Projétil (%s): %s — classe não carregada (dispare uma vez; a captura ao vivo cobre este caso)", c.kinds, c.name)
      else
        report(string.format("Projétil (%s): %s ×%d", c.kinds, c.name, c.num or 1),
               string.format("%d. Projétil %s: %s", found, c.kinds, c.name), findCDO(c.cls), nil)
      end
    end
  elseif u8(atk + 0x322) == 1 then
    line("-- %s: %s — ataque à distância sem classe de projétil na tabela (a captura ao vivo resolve)", a.label, a.name)
  end
end
if found == 0 then print("A tabela dos ataques desta arma não aponta para uma classe de projétil.") end

local function finish()
  if UPDATE_TABLE then prune(gRoot, keepRoot) end
  print("==================================================================")
  print("Unidades: cm e cm/s (1 m = 100). Valores no CDO afetam os próximos disparos de todo projétil da classe.")
  if UPDATE_TABLE then print("Tabela do CE atualizada: grupo \"G2 · Projétil da arma\". Rode de novo ao trocar de arma/munição.") end
end

-- ---------------------------------------------------------------- captura ao vivo
-- ULevel.Actors (+0xA0, TArray<AActor*>): procura atores novos cujo Owner (+0x160) ou Instigator (+0x1A8) seja o
-- seu personagem e que tenham um ProjectileMovementComponent.
if CAPTURE_SECONDS <= 0 or level == nil or pawn == nil then finish() return end

local function actorsOf(lvl)
  local data, n = q(lvl + 0xA0), i32(lvl + 0xA8)
  if data == nil or n == nil or n <= 0 or n > 500000 then return nil, 0 end
  return data, n
end
local known = {}
do
  local data, n = actorsOf(level)
  for i = 0, n - 1 do local a = q(data + i * 8); if a then known[a] = true end end
end

local capturedCls, captured = {}, 0
local deadline = getTickCount() + CAPTURE_SECONDS * 1000
line(">> Captura ao vivo: dispare a arma agora (aguardando até %d s)…", CAPTURE_SECONDS)

local timer = createTimer(nil, false)
timer.Interval = 25
timer.OnTimer = function(t)
  local ok, err = pcall(function()
    local data, n = actorsOf(level)
    local streak = 0   -- atores novos entram no fim do array: para depois de 256 já conhecidos seguidos
    for i = n - 1, 0, -1 do
      local a = q(data + i * 8)
      if a ~= nil and known[a] then
        streak = streak + 1
        if streak >= 256 then break end
      elseif a ~= nil then
        streak = 0
        known[a] = true
        local owner, inst = q(a + 0x160), q(a + 0x1A8)
        if owner == pawn or inst == pawn or (owner ~= nil and q(owner + 0x160) == pawn) then
          local cls = q(a + 0x10)
          if cls ~= nil and not capturedCls[cls] and findPMC(a) ~= nil then
            capturedCls[cls] = true
            captured = captured + 1
            line("-- Capturado ao vivo: %s   [ator %X]", fname(i32(cls + 0x18)), a)
            report("Projétil " .. fname(i32(cls + 0x18)), "Capturado: " .. fname(i32(cls + 0x18)), findCDO(cls), a)
          end
        end
      end
    end
  end)
  if not ok then print("Erro na captura: " .. tostring(err)) end
  if not ok or getTickCount() > deadline or captured >= 3 then
    t.destroy()
    if captured == 0 and ok then print(">> Nenhum projétil seu apareceu no tempo limite.") end
    finish()
  end
end
timer.Enabled = true
