# Procedimento de atualização de offsets

Depois de um patch do Grounded 2, três coisas podem mudar. O overlay trata cada uma de um jeito:

| O que muda | Detecção | Ação necessária |
|---|---|---|
| Endereços das globais (GWorld/GNames/GObjects) | `TimeDateStamp-SizeOfImage` do PE ≠ `scan_cache.json` | **nenhuma**: o overlay refaz o pattern scan sozinho |
| Offsets de propriedades refletidas (vida, componentes, câmera…) | log `HealthComponent aponta para '...'`, ESP vazio ou valores absurdos | gerar novo dump (passo 2) |
| Bytes das assinaturas | log `AOB não encontrado` / estado `Failed` | regenerar assinaturas (passo 3) |
| Membros não refletidos (Actors, ComponentToWorld, Bounds) | validação em runtime | **nenhuma**: o `LayoutDetector` testa os candidatos de novo |

## 1. Rodar e ler o log

Abra o jogo, entre num mundo e rode o overlay. Abra o log mais recente em `logs/overlay_*.log` e procure:

- `AOB encontrado [...]`: pattern scan funcionou;
- `FNamePool validado`, `GWorld validado`, `UStruct.SuperStruct validado`: globais OK;
- `ULevel.Actors validado`, `ComponentToWorld validado`, `Bounds validado`: layout OK;
- `Entidades: N rastreadas`: ESP operando.

Se tudo aparecer, não há nada a fazer.

## 2. Offsets refletidos — novo dump

1. Com o jogo aberto num mundo, gere o dump do SDK com o mesmo dumper usado originalmente
   (saída `Grounded2Steam-Win64-Shipping-dump-AAAAMMDD-HHMMSS.jsonl`).
2. Coloque o `.jsonl` em `Overlay\Claude_Test\`, `Overlay\Dumps\` ou ao lado do executável.
3. Reinicie o overlay (ou clique em **Recarregar** no menu).
   O `OffsetBootstrap` encontra o dump mais novo, importa por nome os 36 offsets refletidos e regrava
   `Config/offsets.json`. Cada valor alterado aparece no log como `Offset atualizado: X 0x… → 0x…`.
4. Se uma classe ou propriedade sumir (renomeada pela Obsidian), o log mostra
   `Classe.Propriedade não encontrado no dump`. Nesse caso, procure o novo nome no dump e atualize a
   definição em `UnrealSDK/Offsets.cs` (`Defs`).

Para inspecionar o dump manualmente: cada linha `"kind":"class"` traz `"props":[{"name","offset","size"...}]`
já com os offsets herdados incluídos.

## 3. Assinaturas — regenerar AOBs

Requer Python 3.8+ (sem bibliotecas extras):

```powershell
cd "C:\Apps\Hydra\Jogos\Grounded 2\Grounded 2\Augusta\Binaries\Win64\Overlay\tools"
python sigtool.py "..\..\Grounded2Steam-Win64-Shipping.exe" "..\Claude_Test\<novo-dump>.jsonl" "..\Config\signatures.json"
```

A ferramenta:
1. lê as RVAs das globais na linha `meta` do dump;
2. encontra todas as instruções `[rip+disp32]` que apontam para elas na `.text`;
3. gera padrões com curingas (disp32, `call/jmp rel32`, `jcc rel32`) e expande até serem únicos;
4. confere que cada padrão resolve exatamente para a RVA esperada e grava até 4 por global.

Depois apague `Config/scan_cache.json` (opcional, o overlay já invalida pela build) e reinicie o overlay.
Se a pasta de saída do build for usada (`bin\Release\...`), copie o `signatures.json` gerado para a pasta
`Config` dela também.

Teste feito nesta build: `sigtool.py` encontrou 39/40/40 referências e gerou 12 assinaturas únicas e
verificadas em ~11 s.

## 4. Mudança de versão da engine

O layout de `UObjectBase`, `FNamePool` e `FUObjectArray` é o da UE 5.x. Se o dump reportar outro
`ue_version`, o log avisa (`Dump reporta UE …`). O `FNameResolver` testa automaticamente dois layouts de
cabeçalho de `FNameEntry` e vários offsets de `Blocks`; o `ObjectReader` testa vários offsets de
`SuperStruct`. Mudanças maiores exigem revisar `UnrealSDK/Types.cs` e as entradas `Engine` em `Offsets.cs`.

## Arquivos envolvidos

| Arquivo | Conteúdo | Quem grava |
|---|---|---|
| `Config/offsets.json` | tabela `Classe.Membro → 0x…` + dump de origem | overlay (import do dump / validação runtime) |
| `Config/signatures.json` | lista de AOBs | `sigtool.py` ou manual; o overlay cria com os padrões internos se faltar |
| `Config/scan_cache.json` | RVAs validadas + chave da build | overlay |
| `Config/config.json` | preferências do usuário | overlay (botão Salvar / ao sair) |
