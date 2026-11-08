# G2Overlay — overlay externo para Grounded 2

Overlay **externo e somente leitura** em C# (.NET 8, x64) para o Grounded 2 (Unreal Engine **5.7**,
build `Augusta-CL-3207493`). Desenha ESP de criaturas (caixa 2D verde + barra de vida colorida) sobre a janela
do jogo, com menu translúcido, pattern scanning, atualização automática de offsets, reconexão automática e
logs detalhados.

> Use apenas em partidas solo ou em sessões próprias. Ferramentas que leem memória podem ser detectadas por
> proteções anti-cheat; o uso online é por sua conta e risco.

## Recursos

| Requisito | Implementação |
|---|---|
| Externo, x64, Win10/11 | processo separado; `OpenProcess` + `ReadProcessMemory`; manifesto Per-Monitor-V2 DPI |
| Pattern scanning | 10 AOBs verificados (GWorld, GNames, GObjects) com cache por build, mais `tools/sigtool.py` para regenerar |
| SDK UE 5.7 | offsets importados por **nome** do dump do SDK; membros não refletidos validados em runtime |
| Renderização | Direct2D + DirectWrite (interop COM direto, sem NuGet), janela transparente, topmost, click-through |
| Menu | translúcido, arrastável; INSERT mostra/oculta, END encerra; captura cliques só quando aberto |
| AimBot | só criaturas (nunca jogadores), segurando uma tecla; FOV, suavização e altura do ponto de mira configuráveis; move o mouse via `SendInput` (sem escrever na memória do jogo) |
| ESP de coletáveis | Milk Molars (`BP_Milk_Nugget_*`) em ciano, Tech Chips (`BP_TechChip_Base_C`) em magenta, Keys em amarelo, Recipes em laranja, Unique Chests enterrados em lilás, arenas de chefes (BossManager) em vermelho-claro; filtros e cores configuráveis |
| ESP | bounding box pela malha (ou cápsula), barra de vida 100% verde → 50% amarelo → 25% laranja → 10% vermelho, nome, distância, vida, time |
| WorldToScreen | `CameraCachePrivate.POV` (Location/Rotation/FOV) com a matemática da engine |
| Robustez | máquina de estados, reconexão, validação de cada global/offset, tratamento de exceções por frame |
| Logs | arquivo por execução em `logs/`, assíncrono, níveis Trace…Fatal, resumos periódicos |
| Desempenho | limitador de FPS com timer de alta resolução, caches e leituras em bloco |

## Compilação

Requisitos: **Windows 10/11 x64** e **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`).
Visual Studio 2022 (17.8+) é opcional.

```powershell
cd "C:\Apps\Hydra\Jogos\Grounded 2\Grounded 2\Augusta\Binaries\Win64\Overlay"
dotnet build G2Overlay.sln -c Release
```

Saída: `bin\x64\Release\net8.0-windows\win-x64\G2Overlay.exe` (mais a pasta `Config\`).

Executável único, opcional:

```powershell
dotnet publish G2Overlay.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

> Compilado e testado no seu PC (Windows 11, .NET 8.0.31): conexão, validações e ESP de criaturas funcionando, CPU ≈ 0,4–1%, RAM ≈ 80–110 MB.

## Execução

1. Abra o Grounded 2 e entre num mundo. A janela deve estar em **janela** ou **tela cheia sem borda**:
   overlays externos não aparecem sobre tela cheia exclusiva.
2. Rode `G2Overlay.exe`. Se o jogo rodar como administrador, rode o overlay como administrador também.
3. O overlay conecta sozinho (ou espera o jogo abrir) e começa a desenhar.

| Tecla | Ação |
|---|---|
| **INSERT** | mostra/oculta o menu |
| **END** | encerra o overlay |
| **Botão direito** (padrão, configurável) | segurado: AimBot mira na criatura mais próxima do centro dentro do círculo de FOV (se ativado no menu) |
| **F9** | diagnóstico: grava no log todos os objetos num raio de 15 m (classe, nome do objeto, item/nó) |

Para clicar no menu é preciso cursor livre: abra o inventário ou outro menu do jogo enquanto o overlay está aberto.

Parâmetros de linha de comando: `--console` (janela de console com o log), `--debug`, `--trace`.

Na primeira execução, o overlay procura o dump mais recente (`*dump*.jsonl`) em `.`, `Claude_Test\` e
`Dumps\`, subindo até 6 pastas a partir do executável. Ele importa os offsets e grava `Config\offsets.json`.

## Configuração (`Config\config.json`)

| Chave | Padrão | Descrição |
|---|---|---|
| `EnableOverlay` | true | liga/desliga todo o desenho do ESP |
| `ShowMenuOnStart` | true | menu aberto ao iniciar |
| `FpsLimit` | 60 | FPS do overlay (10–240) |
| `HideWhenGameNotFocused` | true | oculta quando outra janela está em foco |
| `EspBoxes` / `EspHealthBars` / `EspNames` / `EspDistance` / `EspHealthText` / `EspTeam` | — | elementos do ESP |
| `ShowDeadCreatures` / `ShowPlayers` | false | filtros |
| `MaxDistanceMeters` | 150 | alcance do ESP |
| `BoxColor` / `BoxThickness` / `HealthBarHeight` | `#00FF00` / 1.5 / 4 | aparência |
| `EspCollectibles` | true | ESP de coletáveis (Milk Molars, Tech Chips) |
| `CollectibleFilters` | `["milkmolar","milknugget","techchip","uniquechest","bossmanager"]` | termos procurados no nome da classe, `NodeData.RowName` (harvest nodes), RowName do item (itens no chão) e nome do objeto no mapa |
| `DiagnosticRadiusMeters` | 15 | raio do diagnóstico F9 |
| `AimEnabled` | false | liga o AimBot |
| `AimKey` | 2 (botão direito) | código VK da tecla a segurar (menu alterna: botão direito, Mouse 4/5, Alt, Shift, C) |
| `AimFovPixels` | 150 | raio do círculo de seleção de alvo |
| `AimSmoothing` / `AimGain` / `AimMaxStep` | 6 / 1.0 / 40 | velocidade: fração do erro corrigida por frame = Gain/Smoothing; passo máximo por frame |
| `AimHeight` | 0.55 | ponto de mira na caixa (0 base, 0,5 centro, 1 topo) |
| `AimMaxDistanceMeters` | 60 | alcance da mira |
| `AimShowFov` / `AimTargetIncapacitated` | true / false | desenhar o círculo; mirar criaturas nocauteadas |
| `AimWeakSpots` / `EspWeakSpots` | true / true | mirar no ponto fraco mais próximo da mira (se a criatura tiver); marcar pontos fracos no ESP |
| `WeakSpotTerms` | `["weak"]` | termos no nome dos componentes que identificam pontos fracos (ex.: `LeftEyeWeakspot`) |
| `AimIgnoreTamed` | true | não mirar pets e buggies domados (`TameState == Tame` ou classe `*_Buggy_*` / `*_Pet_*`) |
| `EspArrows` | true | flechas no chão (`BP_World_Arrow_*`: Arrow, Mint, Poison, Spicy) em verde-limão — opção própria na aba Coletáveis |
| `CollectibleItemTokens` | `["Key","Recipe"]` | palavras que precisam aparecer inteiras (entre `_`) no nome da classe de um **item no chão**: `BP_Key_CoaltanaMats_C`, `BP_Recipe_SpicyKatana_C` |
| `CollectibleColors` | milk → `#00E5FF`, techchip → `#FF4FD8` | cor por termo |
| `RemovedDefaultFilters` | `[]` | termos padrão que você removeu de propósito (não são readicionados) |
| `CollectibleColor` / `CollectibleBoxExtent` | `#00E5FF` / 25 | cor e meia-aresta (cm) da caixa do coletável |
| `CollectibleMaxDistanceMeters` / `ShowCollectedCollectibles` | 300 / false | alcance; mostrar molares já coletados |
| `BoxSource` | `Mesh` | `Mesh` (bounds da malha) ou `Capsule` |
| `ApplyHealthMultipliers` | true | aplica multiplicadores de NG+/modo personalizado ao MaxHealth |
| `EntityScanIntervalMs` / `FullRevalidateIntervalMs` | 500 / 10000 | frequência do scan de atores |
| `AutoImportDump` / `DumpSearchDirectories` | true / `[".","Claude_Test","Dumps"]` | atualização automática de offsets |
| `LogLevel` / `ConsoleLog` | `Info` / false | logs |

## Documentação

| Documento | Conteúdo |
|---|---|
| [docs/ARQUITETURA.md](docs/ARQUITETURA.md) | arquitetura, diagrama dos módulos, ciclo de vida, loop principal, decisões, desempenho |
| [docs/OFFSETS.md](docs/OFFSETS.md) | todos os offsets, cadeia de ponteiros, estruturas da engine, validação dos não refletidos |
| [docs/ASSINATURAS.md](docs/ASSINATURAS.md) | AOBs/assinaturas, desmontagem, RVAs e método de descoberta |
| [docs/ATUALIZACAO.md](docs/ATUALIZACAO.md) | procedimento de atualização de offsets e assinaturas após patches |
| [docs/VALIDACAO.md](docs/VALIDACAO.md) | checklist de validação completo |

## Solução de problemas

| Sintoma | Causa provável / ação |
|---|---|
| `OpenProcess falhou (erro 5)` | jogo como admin → rode o overlay como admin |
| `AOB não encontrado` para todas | jogo atualizado → `tools/sigtool.py` (ATUALIZACAO.md §3) |
| `HealthComponent aponta para '...'` | offsets desatualizados → gere novo dump (ATUALIZACAO.md §2) |
| Overlay não aparece sobre o jogo | use janela / tela cheia sem borda |
| Caixas deslocadas | confira escala do Windows (o manifesto já é DPI-aware) e se o jogo usa resolução de render diferente da janela |
| "Layout: … C2W …" não fica ok | entre num mundo com o personagem controlável; o log mostra os candidatos testados |
